using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveDrive.Models;
using Windows.Storage;
using Windows.Storage.Search;
#if BACKGROUND_TASK
using CameraBackupRetryState = LiveDrive.BackgroundTasks.CameraBackupRetryState;
using CameraUploadSessionState = LiveDrive.BackgroundTasks.CameraUploadSessionState;
#endif

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
        private const int MaximumUploadsPerBackgroundRun = 1;
        private const uint RecentFilesPerBackgroundRun = 12;
        private const int MaximumPendingPaths = 100;
        private const int MaximumLargeUploadChunksPerBackgroundRun = 8;
        private const int InitialUnavailableRetryMinutes = 15;
        private const int MaximumUnavailableRetryMinutes = 360;
        private static readonly TimeSpan MaximumBackgroundWorkDuration = TimeSpan.FromSeconds(18);

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
                progress?.Report(FormatUploadProgress(file.Name, 0));
                try
                {
                    await _graph.UploadAsync(
                        destination.Id,
                        file,
                        new Progress<double>(value => progress?.Report(FormatUploadProgress(file.Name, value))),
                        false,
                        true);
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
            var activationStarted = DateTimeOffset.UtcNow;
            _state.SaveLastScanStarted(DateTimeOffset.Now);
            var pendingPaths = (await _state.LoadPendingPathsAsync()).ToList();
            if (pendingPaths.Count == 0)
            {
                await QueueRecentCameraRollFilesAsync(pendingPaths, cancellationToken);
            }
            var retryStates = (await _state.LoadPendingRetryStatesAsync())
                .ToDictionary(state => state.FilePath, StringComparer.OrdinalIgnoreCase);
            await _state.AppendDiagnosticAsync(
                "Scheduled queue checked", pendingPaths.Count + " item(s) waiting.");
            if (pendingPaths.Count == 0)
            {
                _state.SaveLastResult("No new recent Camera Roll items are waiting to upload.");
                return 0;
            }

            var destinationFolderId = _state.GetDestinationFolderId();
            var uploadedCount = 0;
            var skippedCount = 0;
            var uploadSession = await _state.LoadUploadSessionAsync();
            if (uploadSession != null &&
                !pendingPaths.Contains(uploadSession.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                await _state.ClearUploadSessionAsync();
                uploadSession = null;
            }
            while (pendingPaths.Count > 0 &&
                uploadedCount + skippedCount < MaximumUploadsPerBackgroundRun)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = DateTimeOffset.UtcNow;
                var path = uploadSession == null
                    ? pendingPaths.FirstOrDefault(candidate => IsReadyToRetry(retryStates, candidate, now))
                    : uploadSession.FilePath;
                if (string.IsNullOrEmpty(path) || !IsReadyToRetry(retryStates, path, now))
                {
                    SaveWaitingForRetryResult(pendingPaths, retryStates, now);
                    return 0;
                }

                StorageFile file;
                try
                {
                    file = await StorageFile.GetFileFromPathAsync(path);
                }
                catch (FileNotFoundException)
                {
                    await DeferUnavailablePathAsync(
                        path, retryStates, "The file was not available.", pendingPaths.Count);
                    return 0;
                }
                catch (UnauthorizedAccessException)
                {
                    await DeferUnavailablePathAsync(
                        path, retryStates, "Access to the file was unavailable.", pendingPaths.Count);
                    return 0;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(destinationFolderId))
                {
                    var destination = await _graph.GetOrCreateRootFolderAsync(
                        "LiveDrive Camera Roll", cancellationToken);
                    destinationFolderId = destination.Id;
                    _state.SaveDestinationFolderId(destinationFolderId);
                }

                if (await _graph.ItemExistsAsync(destinationFolderId, file.Name, cancellationToken))
                {
                    if (uploadSession != null && string.Equals(
                        uploadSession.FilePath, file.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        await _state.ClearUploadSessionAsync();
                        uploadSession = null;
                    }
                    await _state.MarkUploadedAsync(file.Path);
                    await _state.AppendDiagnosticAsync(
                        "Camera Roll item reconciled", file.Name + " already exists in OneDrive.");
                    retryStates.Remove(path);
                    RemovePendingPath(pendingPaths, path);
                    await _state.SavePendingPathsAsync(pendingPaths);
                    await _state.SavePendingRetryStatesAsync(retryStates.Values);
                    skippedCount++;
                    continue;
                }

                try
                {
                    var properties = await file.GetBasicPropertiesAsync();
                    if (properties.Size > 4 * 1024 * 1024)
                    {
                        if (uploadSession != null &&
                            !string.Equals(uploadSession.FilePath, file.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            await _state.ClearUploadSessionAsync();
                            uploadSession = null;
                        }

                        var refreshSessionOffset = uploadSession != null;
                        var chunksUploaded = 0;
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (chunksUploaded >= MaximumLargeUploadChunksPerBackgroundRun ||
                                DateTimeOffset.UtcNow - activationStarted >= MaximumBackgroundWorkDuration)
                            {
                                if (uploadSession != null)
                                {
                                    await SaveLargeUploadProgressAsync(
                                        file.Name, uploadSession, properties.Size, chunksUploaded);
                                }
                                else
                                {
                                    _state.SaveLastResult(
                                        "Scheduled Camera Roll backup used its available time before starting " +
                                        file.Name + ". It will retry at the next scheduled check.");
                                }
                                return 0;
                            }

                            var result = await _graph.UploadLargeFileChunkAsync(
                                destinationFolderId, file, uploadSession, false, true, cancellationToken,
                                refreshSessionOffset);
                            chunksUploaded++;
                            refreshSessionOffset = false;
                            if (result.IsComplete)
                            {
                                await _state.ClearUploadSessionAsync();
                                uploadSession = null;
                                break;
                            }

                            uploadSession = result.Session;
                            await _state.SaveUploadSessionAsync(uploadSession);
                        }
                    }
                    else
                    {
                        await _graph.UploadAsync(destinationFolderId, file, null, false, true, cancellationToken);
                    }
                }
                catch (Exception exception) when (IsNameAlreadyExistsConflict(exception))
                {
                    await _state.ClearUploadSessionAsync();
                    uploadSession = null;
                    await _state.MarkUploadedAsync(file.Path);
                    retryStates.Remove(path);
                    RemovePendingPath(pendingPaths, path);
                    await _state.SavePendingPathsAsync(pendingPaths);
                    await _state.SavePendingRetryStatesAsync(retryStates.Values);
                    skippedCount++;
                    continue;
                }

                await _state.MarkUploadedAsync(file.Path);
                await _history.AddAsync(file.Name);
                await _state.AppendDiagnosticAsync("Camera Roll item uploaded", file.Name);
                retryStates.Remove(path);
                RemovePendingPath(pendingPaths, path);
                await _state.SavePendingPathsAsync(pendingPaths);
                await _state.SavePendingRetryStatesAsync(retryStates.Values);
                uploadedCount++;
            }

            await _state.SavePendingPathsAsync(pendingPaths);
            await _state.SavePendingRetryStatesAsync(retryStates.Values);
            _state.SaveLastResult(
                "Uploaded " + uploadedCount + " Camera Roll " +
                (uploadedCount == 1 ? "item." : "items.") +
                (skippedCount == 0 ? string.Empty : " Skipped " + skippedCount + " existing " +
                    (skippedCount == 1 ? "item." : "items.")) +
                " " +
                pendingPaths.Count + " queued item" + (pendingPaths.Count == 1 ? " remains." : "s remain."));
            return uploadedCount;
        }

        private async Task SaveLargeUploadProgressAsync(
            string fileName, CameraUploadSessionState session, ulong fileSize, int chunksUploaded)
        {
            await _state.SaveUploadSessionAsync(session);
            await _state.AppendDiagnosticAsync(
                "Large upload advanced", fileName + " at " + session.NextOffset +
                " of " + fileSize + " bytes after " + chunksUploaded + " chunk(s).");
            _state.SaveLastResult(
                FormatUploadProgress(fileName, (double)session.NextOffset / fileSize) +
                " It will continue at the next scheduled check.");
        }

        private async Task DeferUnavailablePathAsync(
            string path, IDictionary<string, CameraBackupRetryState> retryStates,
            string reason, int queuedCount)
        {
            CameraBackupRetryState retryState;
            if (!retryStates.TryGetValue(path, out retryState))
            {
                retryState = new CameraBackupRetryState { FilePath = path };
                retryStates[path] = retryState;
            }

            retryState.UnavailableCount++;
            retryState.NextRetryUtc = DateTimeOffset.UtcNow.Add(
                GetUnavailableRetryDelay(retryState.UnavailableCount));
            await _state.SavePendingRetryStatesAsync(retryStates.Values);
            var fileName = Path.GetFileName(path);
            await _state.AppendDiagnosticAsync(
                "Camera Roll item deferred",
                fileName + "; attempt " + retryState.UnavailableCount + ". " + reason);
            _state.SaveLastResult(
                "Camera Roll item " + fileName + " is temporarily unavailable. It remains queued and will retry after " +
                retryState.NextRetryUtc.ToLocalTime().ToString("g") + ". " +
                queuedCount + " queued item" + (queuedCount == 1 ? " remains." : "s remain."));
        }

        private void SaveWaitingForRetryResult(
            IEnumerable<string> pendingPaths,
            IDictionary<string, CameraBackupRetryState> retryStates,
            DateTimeOffset now)
        {
            var nextRetry = pendingPaths
                .Select(path => retryStates.ContainsKey(path) ? retryStates[path].NextRetryUtc : now)
                .Where(timestamp => timestamp > now)
                .OrderBy(timestamp => timestamp)
                .FirstOrDefault();
            _state.SaveLastResult(
                "Waiting to retry temporarily unavailable Camera Roll items after " +
                nextRetry.ToLocalTime().ToString("g") + ". " +
                pendingPaths.Count() + " queued item" +
                (pendingPaths.Count() == 1 ? " remains." : "s remain."));
        }

        private static bool IsReadyToRetry(
            IDictionary<string, CameraBackupRetryState> retryStates, string path, DateTimeOffset now)
        {
            CameraBackupRetryState retryState;
            return !retryStates.TryGetValue(path, out retryState) || retryState.NextRetryUtc <= now;
        }

        private static TimeSpan GetUnavailableRetryDelay(int unavailableCount)
        {
            var multiplier = 1 << Math.Min(Math.Max(0, unavailableCount - 1), 5);
            var minutes = Math.Min(MaximumUnavailableRetryMinutes, InitialUnavailableRetryMinutes * multiplier);
            return TimeSpan.FromMinutes(minutes);
        }

        private static void RemovePendingPath(IList<string> pendingPaths, string path)
        {
            for (var index = 0; index < pendingPaths.Count; index++)
            {
                if (string.Equals(pendingPaths[index], path, StringComparison.OrdinalIgnoreCase))
                {
                    pendingPaths.RemoveAt(index);
                    return;
                }
            }
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
            QueueCameraRollFiles(recentFiles, uploadedPaths, queuedPaths, pendingPaths, cancellationToken);

            if (pendingPaths.Count < MaximumPendingPaths)
            {
                var offset = _state.GetPendingScanOffset();
                var olderFiles = await query.GetFilesAsync(offset, RecentFilesPerBackgroundRun);
                QueueCameraRollFiles(olderFiles, uploadedPaths, queuedPaths, pendingPaths, cancellationToken);
                _state.SavePendingScanOffset(olderFiles.Count < RecentFilesPerBackgroundRun
                    ? 0
                    : offset + (uint)olderFiles.Count);
            }

            await _state.SavePendingPathsAsync(pendingPaths);
        }

        private static void QueueCameraRollFiles(
            IReadOnlyList<StorageFile> files, ISet<string> uploadedPaths, ISet<string> queuedPaths,
            ICollection<string> pendingPaths, CancellationToken cancellationToken)
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pendingPaths.Count >= MaximumPendingPaths)
                {
                    return;
                }
                if (SupportedExtensions.Contains(Path.GetExtension(file.Name)) &&
                    !uploadedPaths.Contains(file.Path) &&
                    queuedPaths.Add(file.Path))
                {
                    pendingPaths.Add(file.Path);
                }
            }
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

        private static string FormatUploadProgress(string fileName, double progress)
        {
            var percentage = Math.Max(0, Math.Min(100, (int)Math.Round(progress * 100)));
            return "Uploading " + fileName + " (" + percentage + "% uploaded)…";
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
