using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveDrive.Models;
using Windows.Networking.BackgroundTransfer;
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
        private const int MaximumUploadsPerBackgroundRun = 10;
        private const uint RecentFilesPerBackgroundRun = 12;
        private const int MaximumPendingPaths = 100;
        private const int MaximumLargeUploadChunksPerBackgroundRun = 16;
        private const int InitialUnavailableRetryMinutes = 15;
        private const int MaximumUnavailableRetryMinutes = 360;
        private static readonly TimeSpan MaximumBackgroundWorkDuration = TimeSpan.FromSeconds(18);
        private static readonly TimeSpan MinimumTimeToStartAnotherItem = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan DefaultLargeUploadChunkDuration = TimeSpan.FromSeconds(3);
        // Graph upload sessions expire; restart transfers that have not finished within two days.
        private static readonly TimeSpan MaximumTransferAge = TimeSpan.FromHours(48);

        private readonly IGraphClient _graph;
        private readonly CameraUploadHistoryStore _history;
        private readonly CameraBackupStateStore _state;
        private readonly CameraTransferService _transfers = new CameraTransferService();

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

        public async Task<int> UploadPendingCameraRollItemsAsync(CancellationToken cancellationToken,
            IEnumerable<UploadOperation> finishedTransfers = null)
        {
            var activationStarted = DateTimeOffset.UtcNow;
            _state.SaveLastScanStarted(DateTimeOffset.Now);
            var allowCellular = _state.IsCellularBackupAllowed;
            var pendingPaths = (await _state.LoadPendingPathsAsync()).ToList();
            var retryStates = (await _state.LoadPendingRetryStatesAsync())
                .ToDictionary(state => state.FilePath, StringComparer.OrdinalIgnoreCase);
            var destinationFolderId = _state.GetDestinationFolderId();
            var transferRecords = (await _state.LoadTransferRecordsAsync()).ToList();
            var transferCounts = new TransferRunCounts();
            IDictionary<Guid, UploadOperation> operations = null;
            string transferFailure = null;
            try
            {
                operations = await _transfers.GetOperationsAsync(finishedTransfers);
            }
            catch (Exception exception)
            {
                transferFailure = DescribeException(exception);
            }
            if (operations != null)
            {
                await ReconcileTransfersAsync(
                    transferRecords, operations, pendingPaths, retryStates, transferCounts);
            }

            var activePaths = new HashSet<string>(
                transferRecords.Select(record => record.FilePath), StringComparer.OrdinalIgnoreCase);
            if (pendingPaths.All(activePaths.Contains))
            {
                await QueueRecentCameraRollFilesAsync(pendingPaths, cancellationToken);
            }
            await _state.AppendDiagnosticAsync(
                "Scheduled queue checked", pendingPaths.Count + " item(s) waiting, " +
                transferRecords.Count + " in Background Transfer.");
            if (pendingPaths.Count == 0 && transferCounts.Uploaded + transferCounts.Reconciled == 0)
            {
                _state.SaveLastResult("No new recent Camera Roll items are waiting to upload.");
                return 0;
            }

            var uploadedCount = 0;
            var skippedCount = transferCounts.Reconciled;
            var deferredCount = 0;
            var uploadSession = await _state.LoadUploadSessionAsync();
            if (uploadSession != null &&
                !pendingPaths.Contains(uploadSession.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                await _state.ClearUploadSessionAsync();
                uploadSession = null;
            }

            var inProcessPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (uploadSession != null)
            {
                inProcessPaths.Add(uploadSession.FilePath);
            }
            var transfersAvailable = operations != null;
            var startedTransfers = 0;
            CameraTransferService.TransferBatch batch = null;
            try
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var path in pendingPaths.ToList())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!transfersAvailable ||
                        transferRecords.Count >= CameraTransferService.MaximumActiveTransfers ||
                        (startedTransfers + skippedCount > 0 &&
                         GetRemainingBackgroundTime(activationStarted) < MinimumTimeToStartAnotherItem))
                    {
                        break;
                    }
                    if (activePaths.Contains(path) || inProcessPaths.Contains(path) ||
                        !IsReadyToRetry(retryStates, path, now))
                    {
                        continue;
                    }

                    var lookup = await TryGetFileAsync(path);

                    var file = lookup.Item1;
                    if (file == null)
                    {
                        await DeferUnavailablePathAsync(path, retryStates, lookup.Item2, pendingPaths.Count);
                        deferredCount++;
                        continue;
                    }

                    var size = (await file.GetBasicPropertiesAsync()).Size;
                    if (!CameraTransferService.CanTransfer(size))
                    {
                        // Videos of 60 MiB or more use LiveDrive's resumable chunked uploader.
                        inProcessPaths.Add(path);
                        continue;
                    }

                    destinationFolderId = await EnsureDestinationFolderIdAsync(destinationFolderId, cancellationToken);
                    CameraUploadSessionState session = null;
                    if (!await _graph.ItemExistsAsync(destinationFolderId, file.Name, cancellationToken))
                    {
                        try
                        {
                            session = await _graph.CreateFileUploadSessionAsync(
                                destinationFolderId, file, true, cancellationToken);
                        }
                        catch (Exception exception) when (IsNameAlreadyExistsConflict(exception))
                        {
                        }
                    }
                    if (session == null)
                    {
                        await ReconcileExistingAsync(path, pendingPaths, retryStates);
                        skippedCount++;
                        continue;
                    }

                    if (batch == null)
                    {
                        batch = _transfers.BeginBatch();
                        if (batch.SetupFailure != null)
                        {
                            await _state.AppendDiagnosticAsync(
                                "Background Transfer completion trigger unavailable", batch.SetupFailure);
                        }
                    }

                    Guid transferId;
                    try
                    {
                        transferId = _transfers.Start(batch, file, size, session.UploadUrl, allowCellular);
                    }
                    catch (Exception exception)
                    {
                        transfersAvailable = false;
                        transferFailure = DescribeException(exception);
                        inProcessPaths.Add(path);
                        continue;
                    }

                    transferRecords.Add(new CameraTransferRecord
                    {
                        TransferId = transferId,
                        FilePath = path,
                        FileSize = size,
                        StartedUtc = DateTimeOffset.UtcNow
                    });
                    activePaths.Add(path);
                    await _state.SaveTransferRecordsAsync(transferRecords);
                    startedTransfers++;
                }
            }
            finally
            {
                _transfers.CompleteBatch(batch);
            }

            if (transferFailure != null)
            {
                await _state.AppendDiagnosticAsync(
                    "Background Transfer unavailable", transferFailure + "; using in-app uploads.");
            }

            var inProcessBlocked = CameraTransferService.ShouldDeferInProcessUpload(allowCellular);
            string continuationNote = null;
            TimeSpan? lastChunkDuration = _state.GetLastLargeUploadChunkDuration();
            while (pendingPaths.Count > 0 && !inProcessBlocked &&
                uploadedCount + skippedCount < MaximumUploadsPerBackgroundRun)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hasCompletedWork = uploadedCount + skippedCount + startedTransfers > 0;
                if (hasCompletedWork &&
                    GetRemainingBackgroundTime(activationStarted) < MinimumTimeToStartAnotherItem)
                {
                    break;
                }

                var now = DateTimeOffset.UtcNow;
                var path = uploadSession == null
                    ? pendingPaths.FirstOrDefault(candidate =>
                        !activePaths.Contains(candidate) &&
                        (!transfersAvailable || inProcessPaths.Contains(candidate)) &&
                        IsReadyToRetry(retryStates, candidate, now))
                    : uploadSession.FilePath;
                if (string.IsNullOrEmpty(path) || !IsReadyToRetry(retryStates, path, now))
                {
                    if (!hasCompletedWork && deferredCount == 0 && transferRecords.Count == 0 &&
                        transferCounts.Uploaded == 0)
                    {
                        SaveWaitingForRetryResult(pendingPaths, retryStates, now);
                        return 0;
                    }
                    break;
                }

                var lookup = await TryGetFileAsync(path);

                var file = lookup.Item1;
                if (file == null)
                {
                    await DeferUnavailablePathAsync(path, retryStates, lookup.Item2, pendingPaths.Count);
                    deferredCount++;
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                destinationFolderId = await EnsureDestinationFolderIdAsync(destinationFolderId, cancellationToken);

                // A resumed session was already checked for a remote duplicate when it started.
                var isResumingSession = uploadSession != null && string.Equals(
                    uploadSession.FilePath, file.Path, StringComparison.OrdinalIgnoreCase);
                if (!isResumingSession &&
                    await _graph.ItemExistsAsync(destinationFolderId, file.Name, cancellationToken))
                {
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
                        if (uploadSession != null && !isResumingSession)
                        {
                            await _state.ClearUploadSessionAsync();
                            uploadSession = null;
                        }

                        var refreshSessionOffset = uploadSession != null;
                        var chunksUploaded = 0;
                        var stopRun = false;
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            // Always make some progress on an otherwise idle run, even on a slow network.
                            var mustAttemptChunk = chunksUploaded == 0 && !hasCompletedWork;
                            var remaining = GetRemainingBackgroundTime(activationStarted);
                            if (chunksUploaded >= MaximumLargeUploadChunksPerBackgroundRun ||
                                remaining <= TimeSpan.Zero ||
                                (!mustAttemptChunk &&
                                 remaining < (lastChunkDuration ?? DefaultLargeUploadChunkDuration)))
                            {
                                if (uploadSession != null)
                                {
                                    continuationNote = await SaveLargeUploadProgressAsync(
                                        file.Name, uploadSession, properties.Size, chunksUploaded);
                                }
                                stopRun = true;
                                break;
                            }

                            var chunkTimer = Stopwatch.StartNew();
                            var result = await _graph.UploadLargeFileChunkAsync(
                                destinationFolderId, file, uploadSession, false, true, cancellationToken,
                                refreshSessionOffset);
                            lastChunkDuration = chunkTimer.Elapsed;
                            _state.SaveLastLargeUploadChunkDuration(chunkTimer.Elapsed);
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

                        if (stopRun)
                        {
                            break;
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
            await _state.SaveTransferRecordsAsync(transferRecords);
            var totalUploaded = uploadedCount + transferCounts.Uploaded;
            var waitingForWifi = inProcessBlocked && (inProcessPaths.Count > 0 || !transfersAvailable);
            await _state.AppendDiagnosticAsync(
                "Scheduled upload pass finished",
                uploadedCount + " uploaded in-app, " + transferCounts.Uploaded + " finished by Background Transfer, " +
                startedTransfers + " transfer(s) started, " + transferRecords.Count + " active, " +
                transferCounts.Requeued + " requeued, " + transferCounts.Failed + " failed, " +
                skippedCount + " reconciled, " + deferredCount + " deferred in " +
                (int)(DateTimeOffset.UtcNow - activationStarted).TotalMilliseconds + " ms." +
                (waitingForWifi ? " In-app uploads are waiting for Wi-Fi." : string.Empty));
            _state.SaveLastResult(
                "Uploaded " + totalUploaded + " Camera Roll " +
                (totalUploaded == 1 ? "item." : "items.") +
                (transferRecords.Count == 0 ? string.Empty : " " + transferRecords.Count +
                    (transferRecords.Count == 1 ? " item is" : " items are") + " uploading in the background.") +
                (skippedCount == 0 ? string.Empty : " Skipped " + skippedCount + " existing " +
                    (skippedCount == 1 ? "item." : "items.")) +
                (deferredCount + transferCounts.Failed == 0 ? string.Empty : " Deferred " +
                    (deferredCount + transferCounts.Failed) + " " +
                    (deferredCount + transferCounts.Failed == 1 ? "item" : "items") + " to retry later.") +
                (waitingForWifi ? " Large videos are waiting for Wi-Fi." : string.Empty) +
                (continuationNote == null ? string.Empty : " " + continuationNote) +
                " " +
                pendingPaths.Count + " queued item" + (pendingPaths.Count == 1 ? " remains." : "s remain."));
            return totalUploaded;
        }

        private sealed class TransferRunCounts
        {
            public int Uploaded { get; set; }
            public int Reconciled { get; set; }
            public int Requeued { get; set; }
            public int Failed { get; set; }
        }

        private async Task ReconcileTransfersAsync(
            List<CameraTransferRecord> records, IDictionary<Guid, UploadOperation> operations,
            List<string> pendingPaths, IDictionary<string, CameraBackupRetryState> retryStates,
            TransferRunCounts counts)
        {
            var now = DateTimeOffset.UtcNow;
            var recordedIds = new HashSet<Guid>(records.Select(record => record.TransferId));
            foreach (var orphan in operations.Values.Where(operation => !recordedIds.Contains(operation.Guid)))
            {
                uint ignoredStatus;
                if (CameraTransferService.Classify(orphan, out ignoredStatus) ==
                    CameraTransferService.TransferOutcome.Active)
                {
                    CameraTransferService.Cancel(orphan);
                }
            }

            foreach (var record in records.ToList())
            {
                UploadOperation operation;
                operations.TryGetValue(record.TransferId, out operation);
                uint statusCode;
                var outcome = CameraTransferService.Classify(operation, out statusCode);
                var isPending = pendingPaths.Contains(record.FilePath, StringComparer.OrdinalIgnoreCase);
                var fileName = Path.GetFileName(record.FilePath);
                if (outcome == CameraTransferService.TransferOutcome.Active)
                {
                    if (isPending && now - record.StartedUtc < MaximumTransferAge)
                    {
                        continue;
                    }
                    CameraTransferService.Cancel(operation);
                    outcome = CameraTransferService.TransferOutcome.Restart;
                }

                records.Remove(record);
                if (!isPending)
                {
                    continue;
                }

                switch (outcome)
                {
                    case CameraTransferService.TransferOutcome.Succeeded:
                        await _state.MarkUploadedAsync(record.FilePath);
                        await _history.AddAsync(fileName);
                        await _state.AppendDiagnosticAsync(
                            "Camera Roll item uploaded", fileName + " by Background Transfer.");
                        retryStates.Remove(record.FilePath);
                        RemovePendingPath(pendingPaths, record.FilePath);
                        counts.Uploaded++;
                        break;
                    case CameraTransferService.TransferOutcome.AlreadyExists:
                        await ReconcileExistingAsync(record.FilePath, pendingPaths, retryStates);
                        counts.Reconciled++;
                        break;
                    case CameraTransferService.TransferOutcome.Failed:
                        await DeferUnavailablePathAsync(record.FilePath, retryStates,
                            "Background upload failed" + FormatStatusCode(statusCode) + ".", pendingPaths.Count);
                        counts.Failed++;
                        break;
                    default:
                        // The next pass checks OneDrive first, so a finished-but-unreported upload is reconciled.
                        await _state.AppendDiagnosticAsync(
                            "Background upload will restart", fileName + FormatStatusCode(statusCode) + ".");
                        counts.Requeued++;
                        break;
                }
            }

            await _state.SavePendingPathsAsync(pendingPaths);
            await _state.SavePendingRetryStatesAsync(retryStates.Values);
            await _state.SaveTransferRecordsAsync(records);
        }

        private async Task ReconcileExistingAsync(
            string path, List<string> pendingPaths, IDictionary<string, CameraBackupRetryState> retryStates)
        {
            await _state.MarkUploadedAsync(path);
            await _state.AppendDiagnosticAsync(
                "Camera Roll item reconciled", Path.GetFileName(path) + " already exists in OneDrive.");
            retryStates.Remove(path);
            RemovePendingPath(pendingPaths, path);
            await _state.SavePendingPathsAsync(pendingPaths);
            await _state.SavePendingRetryStatesAsync(retryStates.Values);
        }

        private async Task<string> EnsureDestinationFolderIdAsync(
            string destinationFolderId, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(destinationFolderId))
            {
                return destinationFolderId;
            }

            var destination = await _graph.GetOrCreateRootFolderAsync("LiveDrive Camera Roll", cancellationToken);
            _state.SaveDestinationFolderId(destination.Id);
            return destination.Id;
        }

        private static async Task<Tuple<StorageFile, string>> TryGetFileAsync(string path)
        {
            try
            {
                return Tuple.Create(await StorageFile.GetFileFromPathAsync(path), (string)null);
            }
            catch (FileNotFoundException)
            {
                return Tuple.Create((StorageFile)null, "The file was not available.");
            }
            catch (UnauthorizedAccessException)
            {
                return Tuple.Create((StorageFile)null, "Access to the file was unavailable.");
            }
        }

        private static string FormatStatusCode(uint statusCode)
        {
            return statusCode == 0 ? string.Empty : " (HTTP " + statusCode + ")";
        }

        private static string DescribeException(Exception exception)
        {
            return exception.GetType().Name + " (0x" + exception.HResult.ToString("X8") + ")";
        }

        public async Task CancelBackgroundTransfersAsync()
        {
            try
            {
                await _transfers.CancelAllAsync();
            }
            catch (Exception exception)
            {
                await _state.AppendDiagnosticAsync(
                    "Background Transfer cancellation failed", DescribeException(exception));
            }
            await _state.SaveTransferRecordsAsync(new CameraTransferRecord[0]);
        }

        public async Task ApplyCellularPreferenceAsync(bool allowCellular)
        {
            _state.SetCellularBackupAllowed(allowCellular);
            await _transfers.ApplyCostPolicyAsync(allowCellular);
        }

        public async Task<IDictionary<string, string>> GetBackgroundTransferStatusesAsync()
        {
            return await _transfers.DescribeTransfersAsync(await _state.LoadTransferRecordsAsync());
        }

        private static TimeSpan GetRemainingBackgroundTime(DateTimeOffset activationStarted)
        {
            return MaximumBackgroundWorkDuration - (DateTimeOffset.UtcNow - activationStarted);
        }

        private async Task<string> SaveLargeUploadProgressAsync(
            string fileName, CameraUploadSessionState session, ulong fileSize, int chunksUploaded)
        {
            await _state.SaveUploadSessionAsync(session);
            await _state.AppendDiagnosticAsync(
                "Large upload advanced", fileName + " at " + session.NextOffset +
                " of " + fileSize + " bytes after " + chunksUploaded + " chunk(s).");
            return FormatUploadProgress(fileName, (double)session.NextOffset / fileSize) +
                " It will continue at the next scheduled check.";
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
            var enrollment = _state.GetScheduledBackupEnrollment();
            var skippedBeforeEnrollment = 0;
            var recentFiles = await query.GetFilesAsync(0, RecentFilesPerBackgroundRun);
            skippedBeforeEnrollment += QueueCameraRollFiles(
                recentFiles, uploadedPaths, queuedPaths, pendingPaths, enrollment, cancellationToken);

            if (pendingPaths.Count < MaximumPendingPaths)
            {
                var offset = _state.GetPendingScanOffset();
                var olderFiles = await query.GetFilesAsync(offset, RecentFilesPerBackgroundRun);
                skippedBeforeEnrollment += QueueCameraRollFiles(
                    olderFiles, uploadedPaths, queuedPaths, pendingPaths, enrollment, cancellationToken);
                _state.SavePendingScanOffset(olderFiles.Count < RecentFilesPerBackgroundRun
                    ? 0
                    : offset + (uint)olderFiles.Count);
            }

            if (skippedBeforeEnrollment > 0)
            {
                await _state.AppendDiagnosticAsync(
                    "Scheduled discovery skipped older media",
                    skippedBeforeEnrollment + " item(s) predate scheduled-backup enrollment.");
            }
            await _state.SavePendingPathsAsync(pendingPaths);
        }

        private static int QueueCameraRollFiles(
            IReadOnlyList<StorageFile> files, ISet<string> uploadedPaths, ISet<string> queuedPaths,
            ICollection<string> pendingPaths, DateTimeOffset? enrollment,
            CancellationToken cancellationToken)
        {
            var skippedBeforeEnrollment = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pendingPaths.Count >= MaximumPendingPaths)
                {
                    return skippedBeforeEnrollment;
                }
                if (!SupportedExtensions.Contains(Path.GetExtension(file.Name)))
                {
                    continue;
                }
                if (enrollment.HasValue && file.DateCreated <= enrollment.Value)
                {
                    skippedBeforeEnrollment++;
                    continue;
                }
                if (!uploadedPaths.Contains(file.Path) &&
                    queuedPaths.Add(file.Path))
                {
                    pendingPaths.Add(file.Path);
                }
            }
            return skippedBeforeEnrollment;
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
