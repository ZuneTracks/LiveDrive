using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiveDrive.Models;
using Windows.Storage;

namespace LiveDrive.Services
{
    public interface IGraphClient
    {
        Task<IReadOnlyList<DriveItem>> GetChildrenAsync(string folderId);
        Task<DriveItem> GetOrCreateRootFolderAsync(string name);
        Task<DriveItem> GetRootFolderAsync();
        Task<DriveItemPage> GetPhotoDeltaPageAsync(string nextLink, string folderId);
        Task<string> GetThumbnailUrlAsync(string itemId, string size, CancellationToken cancellationToken);
        Task<bool> DownloadThumbnailAsync(string thumbnailUrl, StorageFile destination, CancellationToken cancellationToken);
        Task<string> ReadAppFolderFileAsync(string fileName);
        Task WriteAppFolderFileAsync(string fileName, string content);
        Task<IReadOnlyList<DriveItem>> SearchAsync(string query);
        Task UploadAsync(string parentId, StorageFile file, IProgress<double> progress = null, bool renameOnConflict = false);
        Task CopyAsync(DriveItem item, string destinationFolderId);
        Task MoveAsync(DriveItem item, string destinationFolderId);
        Task DeleteAsync(DriveItem item);
        Task DownloadToAsync(DriveItem item, StorageFile destination);
        Task<string> CreateShareLinkAsync(DriveItem item);
    }
}
