using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace LiveDrive.Services
{
    public sealed class CameraBackupService
    {
        private static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".mp4", ".mov"
        };
        private const int MaximumUploadsPerRun = 10;

        private readonly IGraphClient _graph;
        private readonly CameraUploadHistoryStore _history;
        private readonly CameraBackupStateStore _state;

        public CameraBackupService(IGraphClient graph, CameraUploadHistoryStore history, CameraBackupStateStore state)
        {
            _graph = graph;
            _history = history;
            _state = state;
        }

        public async Task<int> UploadNewCameraRollItemsAsync(CancellationToken cancellationToken)
        {
            var files = await GetCameraRollFilesAsync(KnownFolders.CameraRoll);
            var supportedFiles = files
                .Where(file => SupportedExtensions.Contains(Path.GetExtension(file.Name)))
                .ToList();
            var pendingFiles = supportedFiles
                .Where(file => !_state.HasUploaded(file.Path))
                .OrderBy(file => file.DateCreated)
                .ToList();
            if (pendingFiles.Count == 0)
            {
                _state.SaveLastResult("Found " + supportedFiles.Count + " supported Camera Roll " +
                    (supportedFiles.Count == 1 ? "item; it was" : "items; all were") + " already uploaded.");
                return 0;
            }

            var destination = await _graph.GetOrCreateRootFolderAsync("LiveDrive Camera Roll");
            var uploadedCount = 0;
            foreach (var file in pendingFiles.Take(MaximumUploadsPerRun))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _graph.UploadAsync(destination.Id, file, null, true);
                _state.MarkUploaded(file.Path);
                await _history.AddAsync(file.Name);
                uploadedCount++;
            }

            _state.SaveLastResult("Uploaded " + uploadedCount + " new Camera Roll " +
                (uploadedCount == 1 ? "item." : "items.") +
                (pendingFiles.Count > uploadedCount
                    ? " " + (pendingFiles.Count - uploadedCount) + " more new " +
                      (pendingFiles.Count - uploadedCount == 1 ? "item remains." : "items remain.")
                    : string.Empty));
            return uploadedCount;
        }

        private static async Task<IReadOnlyList<StorageFile>> GetCameraRollFilesAsync(StorageFolder root)
        {
            var files = new List<StorageFile>();
            var folders = new Queue<StorageFolder>();
            folders.Enqueue(root);
            while (folders.Count > 0)
            {
                var folder = folders.Dequeue();
                files.AddRange(await folder.GetFilesAsync());
                foreach (var childFolder in await folder.GetFoldersAsync())
                {
                    folders.Enqueue(childFolder);
                }
            }
            return files;
        }
    }
}
