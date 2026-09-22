using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Search;

namespace LiveDrive.Services
{
#if BACKGROUND_TASK
    internal sealed class CameraBackupService
#else
    public sealed class CameraBackupService
#endif
    {
        private static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".mp4", ".mov"
        };
        private const int MaximumUploadsPerRun = 10;
        private const int MaximumUploadsPerBackgroundRun = 5;
        private const uint RecentFilesPerBackgroundRun = 12;

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
            _state.SaveLastScanStarted(DateTimeOffset.Now);
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

            var destination = await _graph.GetOrCreateRootFolderAsync(
                "LiveDrive Camera Roll", cancellationToken);
            var existingNames = new HashSet<string>(
                (await _graph.GetChildrenAsync(destination.Id))
                    .Where(item => !item.IsFolder)
                    .Select(item => item.Name),
                StringComparer.OrdinalIgnoreCase);
            var skippedFiles = pendingFiles
                .Where(file => existingNames.Contains(file.Name))
                .ToList();
            var newNames = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
            var filesToUpload = new List<StorageFile>();
            foreach (var file in pendingFiles.Where(file => !existingNames.Contains(file.Name)))
            {
                if (newNames.Add(file.Name))
                {
                    filesToUpload.Add(file);
                }
                else
                {
                    skippedFiles.Add(file);
                }
            }
            foreach (var file in skippedFiles)
            {
                await _state.MarkUploadedAsync(file.Path);
            }
            var skippedCount = skippedFiles.Count;

            var uploadedCount = 0;
            var reconciledAfterUploadCount = 0;
            var failedCount = 0;
            string firstFailure = null;
            var candidatesToUpload = filesToUpload.Take(MaximumUploadsPerRun).ToList();
            var attemptedCount = 0;
            foreach (var file in candidatesToUpload)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attemptedCount++;
                progress?.Report("Uploading " + file.Name + "…");
                try
                {
                    await _graph.UploadAsync(destination.Id, file, null, false, true);
                }
                catch (Exception exception) when (IsNameAlreadyExistsConflict(exception))
                {
                    await _state.MarkUploadedAsync(file.Path);
                    existingNames.Add(file.Name);
                    skippedCount++;
                    reconciledAfterUploadCount++;
                    continue;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failedCount++;
                    if (firstFailure == null)
                    {
                        firstFailure = FormatFailure(file.Name, exception);
                    }
                    progress?.Report("Could not upload " + file.Name + "; continuing.");
                    continue;
                }
                await _state.MarkUploadedAsync(file.Path);
                await _history.AddAsync(file.Name);
                existingNames.Add(file.Name);
                uploadedCount++;
            }

            _state.SaveLastResult("Uploaded " + uploadedCount + " new Camera Roll " +
                (uploadedCount == 1 ? "item." : "items.") +
                (skippedCount == 0 ? string.Empty : " Skipped " + skippedCount + " existing " +
                    (skippedCount == 1 ? "file." : "files.")) +
                (failedCount == 0 ? string.Empty : " Failed " + failedCount + " " +
                    (failedCount == 1 ? "file." : "files.") + " First failure: " + firstFailure) +
                " Attempted " + attemptedCount + " of " + filesToUpload.Count + " new " +
                (filesToUpload.Count == 1 ? "item." : "items.") +
                (filesToUpload.Count - uploadedCount - reconciledAfterUploadCount > 0
                    ? " " + (filesToUpload.Count - uploadedCount - reconciledAfterUploadCount) + " more new " +
                      (filesToUpload.Count - uploadedCount - reconciledAfterUploadCount == 1 ? "item remains." : "items remain.")
                    : string.Empty));
            return uploadedCount;
        }

        public async Task<int> UploadPendingCameraRollItemsAsync(CancellationToken cancellationToken)
        {
            _state.SaveLastScanStarted(DateTimeOffset.Now);
            var pendingPaths = (await _state.LoadPendingPathsAsync()).ToList();
            await QueueRecentCameraRollFilesAsync(pendingPaths, cancellationToken);
            if (pendingPaths.Count == 0)
            {
                _state.SaveLastResult("No new recent Camera Roll items are waiting to upload.");
                return 0;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var destinationFolderId = _state.GetDestinationFolderId();
            if (string.IsNullOrEmpty(destinationFolderId))
            {
                var destination = await _graph.GetOrCreateRootFolderAsync(
                    "LiveDrive Camera Roll", cancellationToken);
                destinationFolderId = destination.Id;
                _state.SaveDestinationFolderId(destinationFolderId);
            }

            var uploadedCount = 0;
            var skippedCount = 0;
            while (pendingPaths.Count > 0 &&
                uploadedCount + skippedCount < MaximumUploadsPerBackgroundRun)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = pendingPaths[0];
                StorageFile file;
                try
                {
                    file = await StorageFile.GetFileFromPathAsync(path);
                }
                catch (FileNotFoundException)
                {
                    pendingPaths.RemoveAt(0);
                    await _state.SavePendingPathsAsync(pendingPaths);
                    skippedCount++;
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    pendingPaths.RemoveAt(0);
                    await _state.SavePendingPathsAsync(pendingPaths);
                    skippedCount++;
                    continue;
                }

                try
                {
                    await _graph.UploadAsync(destinationFolderId, file, null, false, true, cancellationToken);
                }
                catch (Exception exception) when (IsNameAlreadyExistsConflict(exception))
                {
                    await _state.MarkUploadedAsync(file.Path);
                    pendingPaths.RemoveAt(0);
                    await _state.SavePendingPathsAsync(pendingPaths);
                    skippedCount++;
                    continue;
                }

                await _state.MarkUploadedAsync(file.Path);
                await _history.AddAsync(file.Name);
                pendingPaths.RemoveAt(0);
                await _state.SavePendingPathsAsync(pendingPaths);
                uploadedCount++;
            }

            await _state.SavePendingPathsAsync(pendingPaths);
            _state.SaveLastResult(
                "Uploaded " + uploadedCount + " Camera Roll " +
                (uploadedCount == 1 ? "item." : "items.") +
                (skippedCount == 0 ? string.Empty : " Skipped " + skippedCount + " unavailable or existing " +
                    (skippedCount == 1 ? "item." : "items.")) +
                " " +
                pendingPaths.Count + " queued item" + (pendingPaths.Count == 1 ? " remains." : "s remain."));
            return uploadedCount;
        }

        private async Task QueueRecentCameraRollFilesAsync(
            List<string> pendingPaths, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uploadedPaths = new HashSet<string>(
                await _state.LoadUploadedPathsAsync(), StringComparer.OrdinalIgnoreCase);
            var queuedPaths = new HashSet<string>(pendingPaths, StringComparer.OrdinalIgnoreCase);
            var query = KnownFolders.CameraRoll.CreateFileQuery(CommonFileQuery.OrderByDate);
            var recentFiles = await query.GetFilesAsync(0, RecentFilesPerBackgroundRun);
            foreach (var file in recentFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SupportedExtensions.Contains(Path.GetExtension(file.Name)) &&
                    !uploadedPaths.Contains(file.Path) &&
                    queuedPaths.Add(file.Path))
                {
                    pendingPaths.Add(file.Path);
                }
            }
            await _state.SavePendingPathsAsync(pendingPaths);
        }

        internal static bool IsNameAlreadyExistsConflict(Exception exception)
        {
            return exception is InvalidOperationException &&
                exception.Message.IndexOf("(409)", StringComparison.Ordinal) >= 0 &&
                exception.Message.IndexOf("nameAlreadyExists", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FormatFailure(string fileName, Exception exception)
        {
            const int MaximumMessageLength = 160;
            var message = exception.Message
                .Replace("\r", " ")
                .Replace("\n", " ");
            if (message.Length > MaximumMessageLength)
            {
                message = message.Substring(0, MaximumMessageLength) + "…";
            }
            return fileName + ": " + message;
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
