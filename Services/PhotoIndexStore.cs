using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiveDrive.Models;
using Windows.Data.Json;
using Windows.Storage;

namespace LiveDrive.Services
{
    public sealed class PhotoIndexStore
    {
        private const string CacheFolderName = "LiveDrivePhotoCache";
        private const string ThumbnailsFolderName = "Thumbnails";
        private const string IndexFileName = "index.json";
        private const string PreviewFileName = "preview.json";
        private const int PreviewItemCount = 150;
        private const ulong ThumbnailCacheLimit = 64 * 1024 * 1024;
        private PhotoIndexSnapshot _inMemorySnapshot;
        private readonly SemaphoreSlim _saveLock = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, DriveItem> _pendingEnsuredItems =
            new Dictionary<string, DriveItem>(StringComparer.Ordinal);

        public async Task<PhotoIndexSnapshot> LoadAsync()
        {
            if (_inMemorySnapshot != null)
            {
                return _inMemorySnapshot;
            }

            var cacheFolder = await GetCacheFolderAsync();
            StorageFile indexFile;
            try
            {
                indexFile = await cacheFolder.GetFileAsync(IndexFileName);
            }
            catch (FileNotFoundException)
            {
                _inMemorySnapshot = new PhotoIndexSnapshot
                {
                    Items = new List<DriveItem>(),
                    SourceFolders = new List<PhotoSourceFolder>()
                };
                return _inMemorySnapshot;
            }

            var root = JsonObject.Parse(await FileIO.ReadTextAsync(indexFile));
            var items = ReadItems(root.GetNamedArray("items", new JsonArray()));

            var sourceFolders = new List<PhotoSourceFolder>();
            var sourceValues = root.GetNamedArray("sourceFolders", new JsonArray());
            foreach (var value in sourceValues)
            {
                var source = value.GetObject();
                sourceFolders.Add(new PhotoSourceFolder
                {
                    Id = source.GetNamedString("id"),
                    Name = source.GetNamedString("name", "Unnamed folder"),
                    DeltaLink = source.GetNamedString("deltaLink", string.Empty)
                });
            }
            _inMemorySnapshot = new PhotoIndexSnapshot { Items = items, SourceFolders = sourceFolders };
            return _inMemorySnapshot;
        }

        public async Task<IReadOnlyList<DriveItem>> LoadPreviewAsync()
        {
            StorageFile previewFile;
            try
            {
                previewFile = await (await GetCacheFolderAsync()).GetFileAsync(PreviewFileName);
            }
            catch (FileNotFoundException)
            {
                return new List<DriveItem>();
            }

            var root = JsonObject.Parse(await FileIO.ReadTextAsync(previewFile));
            return ReadItems(root.GetNamedArray("items", new JsonArray()));
        }

        public async Task SaveAsync(IEnumerable<DriveItem> items, IEnumerable<PhotoSourceFolder> sourceFolders)
        {
            await _saveLock.WaitAsync();
            try
            {
                var itemList = items.ToList();
                foreach (var ensuredItem in _pendingEnsuredItems.Values)
                {
                    var existingIndex = itemList.FindIndex(item => item.Id == ensuredItem.Id);
                    if (existingIndex < 0)
                    {
                        itemList.Add(ensuredItem);
                    }
                    else
                    {
                        itemList[existingIndex] = ensuredItem;
                    }
                }
                await SaveCoreAsync(itemList, sourceFolders);
                _pendingEnsuredItems.Clear();
            }
            finally
            {
                _saveLock.Release();
            }
        }

        private async Task SaveCoreAsync(IEnumerable<DriveItem> items, IEnumerable<PhotoSourceFolder> sourceFolders)
        {
            var itemList = items.ToList();
            var sourceFolderList = sourceFolders.ToList();
            var root = new JsonObject();
            root.SetNamedValue("items", SerializeItems(itemList));
            var sourceValues = new JsonArray();
            foreach (var source in sourceFolderList)
            {
                var entry = new JsonObject();
                entry.SetNamedValue("id", JsonValue.CreateStringValue(source.Id));
                entry.SetNamedValue("name", JsonValue.CreateStringValue(source.Name));
                entry.SetNamedValue("deltaLink", JsonValue.CreateStringValue(source.DeltaLink ?? string.Empty));
                sourceValues.Add(entry);
            }
            root.SetNamedValue("sourceFolders", sourceValues);
            var file = await (await GetCacheFolderAsync()).CreateFileAsync(IndexFileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, root.Stringify());
            await SavePreviewAsync(itemList);
            _inMemorySnapshot = new PhotoIndexSnapshot { Items = itemList, SourceFolders = sourceFolderList };
        }

        public async Task SavePreviewAsync(IEnumerable<DriveItem> items)
        {
            var root = new JsonObject();
            root.SetNamedValue("items", SerializeItems(items.Take(PreviewItemCount)));
            var file = await (await GetCacheFolderAsync()).CreateFileAsync(PreviewFileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, root.Stringify());
        }

        public async Task EnsureItemsAsync(IEnumerable<DriveItem> items)
        {
            await _saveLock.WaitAsync();
            try
            {
                var snapshot = await LoadAsync();
                var indexedItems = snapshot.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
                var changed = false;
                foreach (var item in items)
                {
                    if (item == null || string.IsNullOrEmpty(item.Id))
                    {
                        continue;
                    }

                    indexedItems[item.Id] = item;
                    _pendingEnsuredItems[item.Id] = item;
                    changed = true;
                }

                if (changed)
                {
                    await SaveCoreAsync(indexedItems.Values, snapshot.SourceFolders);
                }
            }
            finally
            {
                _saveLock.Release();
            }
        }

        public async Task CacheThumbnailAsync(DriveItem item, IGraphClient graph, bool preferLarge, CancellationToken cancellationToken)
        {
            var existingUri = await GetLocalThumbnailUriAsync(item.Id, preferLarge);
            if (!string.IsNullOrEmpty(existingUri))
            {
                item.ThumbnailUrl = existingUri;
                return;
            }

            var remoteUri = await graph.GetThumbnailUrlAsync(item.Id, preferLarge ? "large" : "medium", cancellationToken);
            if (string.IsNullOrEmpty(remoteUri))
            {
                return;
            }

            item.ThumbnailUrl = remoteUri;
            var file = await (await GetThumbnailsFolderAsync()).CreateFileAsync(
                GetThumbnailFileName(item.Id, preferLarge), CreationCollisionOption.ReplaceExisting);
            try
            {
                if (await graph.DownloadThumbnailAsync(remoteUri, file, cancellationToken))
                {
                    item.ThumbnailUrl = GetLocalThumbnailUri(item.Id, preferLarge);
                }
                else
                {
                    await file.DeleteAsync();
                    item.ThumbnailUrl = string.Empty;
                }
            }
            catch
            {
                await file.DeleteAsync();
                throw;
            }
        }

        public async Task TrimThumbnailsAsync()
        {
            var files = await (await GetThumbnailsFolderAsync()).GetFilesAsync();
            var sizedFiles = new List<Tuple<StorageFile, ulong, DateTimeOffset>>();
            ulong totalSize = 0;
            foreach (var file in files)
            {
                var properties = await file.GetBasicPropertiesAsync();
                sizedFiles.Add(Tuple.Create(file, properties.Size, file.DateCreated));
                totalSize += properties.Size;
            }

            foreach (var file in sizedFiles.OrderBy(entry => entry.Item3))
            {
                if (totalSize <= ThumbnailCacheLimit)
                {
                    break;
                }
                await file.Item1.DeleteAsync();
                totalSize -= file.Item2;
            }
        }

        public async Task RemoveThumbnailAsync(string itemId)
        {
            var folder = await GetThumbnailsFolderAsync();
            foreach (var preferLarge in new[] { false, true })
            {
                try
                {
                    await (await folder.GetFileAsync(GetThumbnailFileName(itemId, preferLarge))).DeleteAsync();
                }
                catch (FileNotFoundException)
                {
                }
            }
        }

        public async Task RemoveItemAsync(string itemId)
        {
            await _saveLock.WaitAsync();
            try
            {
                var snapshot = await LoadAsync();
                _pendingEnsuredItems.Remove(itemId);
                await RemoveThumbnailAsync(itemId);
                await SaveCoreAsync(snapshot.Items.Where(item => item.Id != itemId), snapshot.SourceFolders);
            }
            finally
            {
                _saveLock.Release();
            }
        }

        public async Task ClearAsync()
        {
            await _saveLock.WaitAsync();
            try
            {
                _inMemorySnapshot = null;
                _pendingEnsuredItems.Clear();
                try
                {
                    await (await ApplicationData.Current.LocalFolder.GetFolderAsync(CacheFolderName)).DeleteAsync();
                }
                catch (FileNotFoundException)
                {
                }
            }
            finally
            {
                _saveLock.Release();
            }
        }

        private async Task<string> GetLocalThumbnailUriAsync(string itemId, bool preferLarge)
        {
            try
            {
                await (await GetThumbnailsFolderAsync()).GetFileAsync(GetThumbnailFileName(itemId, preferLarge));
                return GetLocalThumbnailUri(itemId, preferLarge);
            }
            catch (FileNotFoundException)
            {
                return string.Empty;
            }
        }

        private static string GetLocalThumbnailUri(string itemId, bool preferLarge)
        {
            return "ms-appdata:///local/" + CacheFolderName + "/" + ThumbnailsFolderName + "/" +
                GetThumbnailFileName(itemId, preferLarge);
        }

        private static List<DriveItem> ReadItems(JsonArray values)
        {
            var items = new List<DriveItem>();
            foreach (var value in values)
            {
                var entry = value.GetObject();
                items.Add(new DriveItem
                {
                    Id = entry.GetNamedString("id"),
                    Name = entry.GetNamedString("name", "Unnamed item"),
                    Size = (long)entry.GetNamedNumber("size", 0),
                    LastModified = entry.GetNamedString("lastModified", string.Empty),
                    MimeType = entry.GetNamedString("mimeType", string.Empty),
                    DateTaken = entry.GetNamedString("dateTaken", string.Empty)
                });
            }
            return items;
        }

        private static JsonArray SerializeItems(IEnumerable<DriveItem> items)
        {
            var values = new JsonArray();
            foreach (var item in items)
            {
                var entry = new JsonObject();
                entry.SetNamedValue("id", JsonValue.CreateStringValue(item.Id));
                entry.SetNamedValue("name", JsonValue.CreateStringValue(item.Name));
                entry.SetNamedValue("size", JsonValue.CreateNumberValue(item.Size));
                entry.SetNamedValue("lastModified", JsonValue.CreateStringValue(item.LastModified ?? string.Empty));
                entry.SetNamedValue("mimeType", JsonValue.CreateStringValue(item.MimeType ?? string.Empty));
                entry.SetNamedValue("dateTaken", JsonValue.CreateStringValue(item.DateTaken ?? string.Empty));
                values.Add(entry);
            }
            return values;
        }

        private static string GetThumbnailFileName(string itemId, bool preferLarge)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(itemId));
                return BitConverter.ToString(hash).Replace("-", string.Empty) + (preferLarge ? ".large.jpg" : ".jpg");
            }
        }

        private static async Task<StorageFolder> GetCacheFolderAsync()
        {
            return await ApplicationData.Current.LocalFolder.CreateFolderAsync(CacheFolderName, CreationCollisionOption.OpenIfExists);
        }

        private static async Task<StorageFolder> GetThumbnailsFolderAsync()
        {
            return await (await GetCacheFolderAsync()).CreateFolderAsync(ThumbnailsFolderName, CreationCollisionOption.OpenIfExists);
        }
    }
}
