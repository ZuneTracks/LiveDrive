using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiveDrive.Models;
using Windows.Data.Json;
using Windows.Storage;

namespace LiveDrive.Services
{
    public sealed class GraphClient : IGraphClient
    {
        private const string GraphRoot = "https://graph.microsoft.com/v1.0";
        private readonly OAuthService _auth;
        private readonly HttpClient _http = new HttpClient();

        public GraphClient(OAuthService auth)
        {
            _auth = auth;
        }

        public async Task<IReadOnlyList<DriveItem>> GetChildrenAsync(string folderId)
        {
            var path = string.IsNullOrEmpty(folderId)
                ? "/me/drive/root/children"
                : "/me/drive/items/" + Uri.EscapeDataString(folderId) + "/children";
            var json = await GetJsonAsync(path + "?$select=id,name,folder,file,size,createdDateTime,lastModifiedDateTime,parentReference,@microsoft.graph.downloadUrl&$orderby=name");
            return ReadItems(json);
        }

        public async Task<DriveItem> GetOrCreateRootFolderAsync(string name)
        {
            var existing = (await GetChildrenAsync(null)).FirstOrDefault(item =>
                item.IsFolder && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                return existing;
            }

            var payload = new JsonObject();
            payload.SetNamedValue("name", JsonValue.CreateStringValue(name));
            payload.SetNamedValue("folder", new JsonObject());
            payload.SetNamedValue("@microsoft.graph.conflictBehavior", JsonValue.CreateStringValue("fail"));
            var request = new HttpRequestMessage(HttpMethod.Post, "/me/drive/root/children")
            {
                Content = new StringContent(payload.Stringify(), Encoding.UTF8, "application/json")
            };
            var created = await SendJsonAsync(request);
            return new DriveItem
            {
                Id = created.GetNamedString("id"),
                Name = created.GetNamedString("name", name),
                IsFolder = true
            };
        }

        public async Task<DriveItem> GetRootFolderAsync()
        {
            var root = await GetJsonAsync("/me/drive/root?$select=id,name,folder");
            return new DriveItem
            {
                Id = root.GetNamedString("id"),
                Name = root.GetNamedString("name", "OneDrive"),
                IsFolder = true
            };
        }

        public async Task<OneDriveQuota> GetQuotaAsync()
        {
            var drive = await GetJsonAsync("/me/drive?$select=quota");
            var quota = drive.GetNamedObject("quota", null);
            return new OneDriveQuota
            {
                Used = quota == null ? 0 : (long)quota.GetNamedNumber("used", 0),
                Total = quota == null ? 0 : (long)quota.GetNamedNumber("total", 0)
            };
        }

        public async Task<IReadOnlyList<DriveItem>> GetRecentAsync()
        {
            return ReadItems(await GetJsonAsync("/me/drive/recent?$select=id,name,file,folder,size,createdDateTime,lastModifiedDateTime,parentReference"));
        }

        public async Task<DriveItemPage> GetPhotoDeltaPageAsync(string nextLink, string folderId)
        {
            const string fields = "$select=id,name,folder,file,photo,size,createdDateTime,lastModifiedDateTime,parentReference,deleted&$top=100";
            var path = string.IsNullOrEmpty(folderId)
                ? "/me/drive/root/delta?" + fields
                : "/me/drive/items/" + Uri.EscapeDataString(folderId) + "/delta?" + fields;
            var json = await GetJsonAsync(string.IsNullOrEmpty(nextLink) ? path : nextLink);
            return new DriveItemPage
            {
                Items = ReadItems(json),
                NextLink = json.GetNamedString("@odata.nextLink", string.Empty),
                DeltaLink = json.GetNamedString("@odata.deltaLink", string.Empty)
            };
        }

        public async Task<string> GetThumbnailUrlAsync(string itemId, string size, CancellationToken cancellationToken)
        {
            var thumbnailSize = size == "large" ? "large" : "medium";
            using (var request = new HttpRequestMessage(HttpMethod.Get,
                "/me/drive/items/" + Uri.EscapeDataString(itemId) + "/thumbnails/0/" + thumbnailSize))
            using (var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, true, cancellationToken))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return string.Empty;
                }

                var json = JsonObject.Parse(await response.Content.ReadAsStringAsync());
                return json.GetNamedString("url", string.Empty);
            }
        }

        public async Task<bool> DownloadThumbnailAsync(string thumbnailUrl, StorageFile destination, CancellationToken cancellationToken)
        {
            using (var response = await _http.GetAsync(thumbnailUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return false;
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Thumbnail download failed (" + (int)response.StatusCode + ").");
                }

                using (var source = await response.Content.ReadAsStreamAsync())
                using (var output = await destination.OpenStreamForWriteAsync())
                {
                    output.SetLength(0);
                    await source.CopyToAsync(output);
                }
                return true;
            }
        }

        public async Task<string> ReadAppFolderFileAsync(string fileName)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, GraphRoot + GetAppFolderFilePath(fileName));
                var token = await _auth.GetAccessTokenAsync();
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                using (var response = await _http.SendAsync(request))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return null;
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        var error = await response.Content.ReadAsStringAsync();
                        throw new InvalidOperationException("Could not read album data (" + (int)response.StatusCode + "): " + error);
                    }
                    return await response.Content.ReadAsStringAsync();
                }
            }

            public async Task WriteAppFolderFileAsync(string fileName, string content)
            {
                var request = new HttpRequestMessage(HttpMethod.Put, GetAppFolderFilePath(fileName))
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/json")
                };
                await SendAsync(request);
            }

            public async Task<IReadOnlyList<DriveItem>> SearchAsync(string query)
        {
            var escapedQuery = Uri.EscapeDataString(query.Replace("'", "''"));
            var path = "/me/drive/root/search(q='" + escapedQuery + "')?$select=id,name,folder,file,size,createdDateTime,lastModifiedDateTime,parentReference,@microsoft.graph.downloadUrl";
            return ReadItems(await GetJsonAsync(path));
        }

        public async Task UploadAsync(string parentId, StorageFile file, IProgress<double> progress = null, bool renameOnConflict = false)
        {
            progress?.Report(0);
            var properties = await file.GetBasicPropertiesAsync();
            if (properties.Size > 4 * 1024 * 1024)
            {
                await UploadLargeFileAsync(parentId, file, properties.Size, progress, renameOnConflict);
                return;
            }

            var target = string.IsNullOrEmpty(parentId)
                ? "/me/drive/root:/" + Uri.EscapeDataString(file.Name) + ":/content"
                : "/me/drive/items/" + Uri.EscapeDataString(parentId) + ":/" + Uri.EscapeDataString(file.Name) + ":/content";
            if (renameOnConflict)
            {
                target += "?@microsoft.graph.conflictBehavior=rename";
            }
            using (var request = new HttpRequestMessage(HttpMethod.Put, GraphRoot + target)
            {
                Content = new StreamContent(await file.OpenStreamForReadAsync())
            })
            {
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");
                using (var response = await SendAsync(request))
                {
                }
            }
            progress?.Report(1);
        }

        public async Task CopyAsync(DriveItem item, string destinationFolderId)
        {
            var payload = CreateParentReferencePayload(destinationFolderId);
            payload.SetNamedValue("@microsoft.graph.conflictBehavior", JsonValue.CreateStringValue("rename"));
            using (var request = new HttpRequestMessage(HttpMethod.Post,
                "/me/drive/items/" + Uri.EscapeDataString(item.Id) + "/copy")
            {
                Content = new StringContent(payload.Stringify(), Encoding.UTF8, "application/json")
            })
            using (var response = await SendAsync(request))
            {
            }
        }

        public async Task MoveAsync(DriveItem item, string destinationFolderId)
        {
            var payload = CreateParentReferencePayload(destinationFolderId);
            var destinationName = await GetAvailableDestinationNameAsync(destinationFolderId, item.Name);
            if (!string.Equals(destinationName, item.Name, StringComparison.Ordinal))
            {
                payload.SetNamedValue("name", JsonValue.CreateStringValue(destinationName));
            }
            using (var request = new HttpRequestMessage(new HttpMethod("PATCH"),
                "/me/drive/items/" + Uri.EscapeDataString(item.Id))
            {
                Content = new StringContent(payload.Stringify(), Encoding.UTF8, "application/json")
            })
            using (var response = await SendAsync(request))
            {
            }
        }

        public async Task DeleteAsync(DriveItem item)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Delete,
                "/me/drive/items/" + Uri.EscapeDataString(item.Id)))
            using (var response = await SendAsync(request))
            {
            }
        }

        private async Task UploadLargeFileAsync(string parentId, StorageFile file, ulong size, IProgress<double> progress,
            bool renameOnConflict)
        {
            var target = string.IsNullOrEmpty(parentId)
                ? "/me/drive/root:/" + Uri.EscapeDataString(file.Name) + ":/createUploadSession"
                : "/me/drive/items/" + Uri.EscapeDataString(parentId) + ":/" + Uri.EscapeDataString(file.Name) + ":/createUploadSession";
            var sessionRequest = new HttpRequestMessage(HttpMethod.Post, target)
            {
                Content = new StringContent(
                    "{\"item\":{\"@microsoft.graph.conflictBehavior\":\"" + (renameOnConflict ? "rename" : "replace") + "\"}}",
                    Encoding.UTF8,
                    "application/json")
            };
            var session = await SendJsonAsync(sessionRequest);
            var uploadUrl = session.GetNamedString("uploadUrl", string.Empty);
            if (string.IsNullOrEmpty(uploadUrl))
            {
                throw new InvalidOperationException("Microsoft Graph did not return an upload session.");
            }

            const int chunkSize = 5 * 1024 * 1024;
            using (var stream = await file.OpenStreamForReadAsync())
            {
                var buffer = new byte[chunkSize];
                long offset = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    using (var content = new ByteArrayContent(buffer, 0, read))
                    using (var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content })
                    {
                        content.Headers.Add("Content-Range", "bytes " + offset + "-" + (offset + read - 1) + "/" + size);
                        var response = await _http.SendAsync(request);
                        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 202)
                        {
                            var error = await response.Content.ReadAsStringAsync();
                            response.Dispose();
                            throw new InvalidOperationException("Upload failed (" + (int)response.StatusCode + "): " + error);
                        }
                        response.Dispose();
                    }
                    offset += read;
                    progress?.Report((double)offset / size);
                }
            }
        }

        private static JsonObject CreateParentReferencePayload(string destinationFolderId)
        {
            var parentReference = new JsonObject();
            parentReference.SetNamedValue("id", JsonValue.CreateStringValue(destinationFolderId));
            var payload = new JsonObject();
            payload.SetNamedValue("parentReference", parentReference);
            return payload;
        }

        private async Task<string> GetAvailableDestinationNameAsync(string parentId, string name)
        {
            if (!await ItemExistsAsync(parentId, name))
            {
                return name;
            }

            var extension = Path.GetExtension(name);
            var nameWithoutExtension = string.IsNullOrEmpty(extension)
                ? name
                : name.Substring(0, name.Length - extension.Length);
            for (var suffix = 1; ; suffix++)
            {
                var candidate = nameWithoutExtension + " (" + suffix + ")" + extension;
                if (!await ItemExistsAsync(parentId, candidate))
                {
                    return candidate;
                }
            }
        }

        private async Task<bool> ItemExistsAsync(string parentId, string name)
        {
            var path = "/me/drive/items/" + Uri.EscapeDataString(parentId) + ":/" +
                Uri.EscapeDataString(name) + "?$select=id";
            using (var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path),
                HttpCompletionOption.ResponseContentRead, true))
            {
                return response.StatusCode != HttpStatusCode.NotFound;
            }
        }

        public async Task DownloadToAsync(DriveItem item, StorageFile destination)
        {
            using (var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get,
                "/me/drive/items/" + Uri.EscapeDataString(item.Id) + "/content"), HttpCompletionOption.ResponseHeadersRead))
            using (var source = await response.Content.ReadAsStreamAsync())
            using (var destinationStream = await destination.OpenStreamForWriteAsync())
            {
                destinationStream.SetLength(0);
                await source.CopyToAsync(destinationStream);
            }
        }

        public async Task<string> CreateShareLinkAsync(DriveItem item)
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                "/me/drive/items/" + Uri.EscapeDataString(item.Id) + "/createLink")
            {
                Content = new StringContent("{\"type\":\"view\",\"scope\":\"anonymous\"}", Encoding.UTF8, "application/json")
            };
            var json = await SendJsonAsync(request);
            var link = json.GetNamedObject("link", null);
            return link == null ? string.Empty : link.GetNamedString("webUrl", string.Empty);
        }

        private async Task<JsonObject> GetJsonAsync(string path)
        {
            const int maximumAttempts = 3;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, path))
                    {
                        return await SendJsonAsync(request);
                    }
                }
                catch (Exception exception) when (IsTransientNetworkFailure(exception) && attempt < maximumAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt));
                }
            }
        }

        private async Task<JsonObject> SendJsonAsync(HttpRequestMessage request)
        {
            using (var response = await SendAsync(request))
            {
                return JsonObject.Parse(await response.Content.ReadAsStringAsync());
            }
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, bool allowNotFound = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var token = await _auth.GetAccessTokenAsync();
            {
                request.RequestUri = request.RequestUri.IsAbsoluteUri
                    ? request.RequestUri
                    : new Uri(GraphRoot + request.RequestUri.OriginalString);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                var response = await _http.SendAsync(request, completion, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    response.Dispose();
                    throw new InvalidOperationException("Your Microsoft sign-in is no longer valid. Sign in again.");
                }
                if (!response.IsSuccessStatusCode && !(allowNotFound && response.StatusCode == HttpStatusCode.NotFound))
                {
                    var error = await response.Content.ReadAsStringAsync();
                    response.Dispose();
                    throw new InvalidOperationException("Graph request failed (" + (int)response.StatusCode + "): " + error);
                }
                return response;
            }
        }

        private static bool IsTransientNetworkFailure(Exception exception)
        {
            const int ConnectionResetHResult = unchecked((int)0x80072EFF);
            return exception.HResult == ConnectionResetHResult || exception is HttpRequestException;
        }

        private static IReadOnlyList<DriveItem> ReadItems(JsonObject json)
        {
            var items = new List<DriveItem>();
            var values = json.GetNamedArray("value", null);
            if (values == null)
            {
                return items;
            }

            foreach (var value in values)
            {
                var entry = value.GetObject();
                var file = entry.GetNamedObject("file", null);
                var photo = entry.GetNamedObject("photo", null);
                var parentReference = entry.GetNamedObject("parentReference", null);
                var thumbnailUrl = string.Empty;
                var thumbnails = entry.GetNamedArray("thumbnails", null);
                if (thumbnails != null && thumbnails.Count > 0)
                {
                    foreach (var thumbnail in thumbnails)
                    {
                        var medium = thumbnail.GetObject().GetNamedObject("medium", null);
                        if (medium != null)
                        {
                            thumbnailUrl = medium.GetNamedString("url", string.Empty);
                        }
                        break;
                    }
                }
                items.Add(new DriveItem
                {
                    Id = entry.GetNamedString("id"),
                    Name = entry.GetNamedString("name", "Unnamed item"),
                    IsFolder = entry.GetNamedObject("folder", null) != null,
                    Size = (long)entry.GetNamedNumber("size", 0),
                    Created = entry.GetNamedString("createdDateTime", string.Empty),
                    LastModified = entry.GetNamedString("lastModifiedDateTime", string.Empty),
                    OneDriveLocation = parentReference == null ? string.Empty : parentReference.GetNamedString("path", string.Empty),
                    DownloadUrl = entry.GetNamedString("@microsoft.graph.downloadUrl", string.Empty),
                    ThumbnailUrl = thumbnailUrl,
                    MimeType = file == null ? string.Empty : file.GetNamedString("mimeType", string.Empty),
                    DateTaken = photo == null ? string.Empty : photo.GetNamedString("takenDateTime", string.Empty),
                    IsDeleted = entry.GetNamedObject("deleted", null) != null
                });
            }
            return items;
        }

        private static string GetAppFolderFilePath(string fileName)
        {
            return "/me/drive/special/approot:/" + Uri.EscapeDataString(fileName) + ":/content";
        }
    }
}
