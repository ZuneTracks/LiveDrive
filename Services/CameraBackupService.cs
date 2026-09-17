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

        public async Task<int> UploadNewCameraRollItemsAsync(CancellationToken cancellationToken,
            IProgress<string> progress = null)
        {
            var files = await GetCameraRollFilesAsync(cancellationToken, progress);
            var supportedFiles = files
                .Where(file => SupportedExtensions.Contains(Path.GetExtension(file.Name)))
                .ToList();
            var pendingFiles = new List<StorageFile>();
            foreach (var file in supportedFiles)
            {
                if (!await _state.HasUploadedAsync(file.Path))
                {
                    pendingFiles.Add(file);
                }
            }
            pendingFiles = pendingFiles.OrderBy(file => file.DateCreated).ToList();
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
                progress?.Report("Uploading " + file.Name + "…");
                await _graph.UploadAsync(destination.Id, file, null, true);
                await _state.MarkUploadedAsync(file.Path);
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

        private static async Task<IReadOnlyList<StorageFile>> GetCameraRollFilesAsync(CancellationToken cancellationToken,
            IProgress<string> progress)
        {
            var files = new List<StorageFile>();
            progress?.Report("Scanning Camera Roll…");
            files.AddRange(await GetFilesRecursivelyAsync(KnownFolders.CameraRoll, cancellationToken));

            progress?.Report("Looking for Camera Roll on the SD card…");
            foreach (var deviceRoot in await GetAccessibleFoldersAsync(KnownFolders.RemovableDevices))
            {
                foreach (var cameraRollFolder in await FindCameraRollFoldersAsync(deviceRoot, cancellationToken))
                {
                    progress?.Report("Scanning SD-card Camera Roll…");
                    files.AddRange(await GetFilesRecursivelyAsync(cameraRollFolder, cancellationToken));
                }
            }

            return files
                .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private static async Task<IReadOnlyList<StorageFolder>> FindCameraRollFoldersAsync(
            StorageFolder root, CancellationToken cancellationToken)
        {
            var cameraRollFolders = new List<StorageFolder>();
            var folders = new Queue<Tuple<StorageFolder, int>>();
            folders.Enqueue(Tuple.Create(root, 0));
            while (folders.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = folders.Dequeue();
                var folder = entry.Item1;
                if (string.Equals(folder.Name, "Camera Roll", StringComparison.OrdinalIgnoreCase))
                {
                    cameraRollFolders.Add(folder);
                    continue;
                }

                if (entry.Item2 == 4)
                {
                    continue;
                }

                foreach (var childFolder in await GetAccessibleFoldersAsync(folder))
                {
                    folders.Enqueue(Tuple.Create(childFolder, entry.Item2 + 1));
                }
            }
            return cameraRollFolders;
        }

        private static async Task<IReadOnlyList<StorageFile>> GetFilesRecursivelyAsync(
            StorageFolder root, CancellationToken cancellationToken)
        {
            var files = new List<StorageFile>();
            var folders = new Queue<StorageFolder>();
            folders.Enqueue(root);
            while (folders.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folder = folders.Dequeue();
                files.AddRange(await GetAccessibleFilesAsync(folder));
                foreach (var childFolder in await GetAccessibleFoldersAsync(folder))
                {
                    folders.Enqueue(childFolder);
                }
            }
            return files;
        }

        private static async Task<IReadOnlyList<StorageFolder>> GetAccessibleFoldersAsync(StorageFolder folder)
        {
            try
            {
                return await folder.GetFoldersAsync();
            }
            catch (UnauthorizedAccessException)
            {
                return new List<StorageFolder>();
            }
            catch (FileNotFoundException)
            {
                return new List<StorageFolder>();
            }
        }

        private static async Task<IReadOnlyList<StorageFile>> GetAccessibleFilesAsync(StorageFolder folder)
        {
            try
            {
                return await folder.GetFilesAsync();
            }
            catch (UnauthorizedAccessException)
            {
                return new List<StorageFile>();
            }
            catch (FileNotFoundException)
            {
                return new List<StorageFile>();
            }
        }
    }
}
