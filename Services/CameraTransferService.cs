using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiveDrive.Models;
using Windows.ApplicationModel.Background;
using Windows.Networking.BackgroundTransfer;
using Windows.Networking.Connectivity;
using Windows.Storage;

namespace LiveDrive.Services
{
    // Hands Camera Roll uploads to Windows Background Transfer so bytes keep moving after
    // LiveDrive's short background task ends. Upload-session URLs are pre-authorized, so no
    // OAuth token is ever given to the transfer service.
#if BACKGROUND_TASK
    internal sealed class CameraTransferService
#else
    public sealed class CameraTransferService
#endif
    {
        public const int MaximumActiveTransfers = 5;
        // Microsoft Graph accepts less than 60 MiB in a single upload-session request.
        public const ulong MaximumTransferBytes = 60UL * 1024 * 1024;
        public const string CompletionTaskName = "LiveDriveCameraBackupTransferCompletion";
        private const string TransferGroupName = "LiveDriveCameraBackupTransfers";
        private const string TaskEntryPoint = "LiveDrive.BackgroundTasks.CameraBackupBackgroundTask";
        private static readonly BackgroundTransferGroup TransferGroup =
            BackgroundTransferGroup.CreateGroup(TransferGroupName);

        public enum TransferOutcome
        {
            Active,
            Succeeded,
            AlreadyExists,
            Restart,
            Failed,
            Missing
        }

        public sealed class TransferBatch
        {
            internal BackgroundTransferCompletionGroup CompletionGroup { get; set; }
            internal BackgroundTaskRegistration Registration { get; set; }
            public int StartedCount { get; internal set; }
            public string SetupFailure { get; internal set; }
        }

        public static bool CanTransfer(ulong size)
        {
            return size > 0 && size < MaximumTransferBytes;
        }

        public static BackgroundTransferCostPolicy GetCostPolicy(bool allowCellular)
        {
            return allowCellular
                ? BackgroundTransferCostPolicy.Default
                : BackgroundTransferCostPolicy.UnrestrictedOnly;
        }

        public static bool ShouldDeferInProcessUpload(bool allowCellular)
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile == null)
            {
                return false;
            }

            var cost = profile.GetConnectionCost();
            if (cost.Roaming)
            {
                return true;
            }

            var isMetered = cost.NetworkCostType == NetworkCostType.Fixed ||
                cost.NetworkCostType == NetworkCostType.Variable;
            return isMetered && !allowCellular;
        }

        public async Task<IDictionary<Guid, UploadOperation>> GetOperationsAsync(
            IEnumerable<UploadOperation> additionalOperations = null)
        {
            var operations = new Dictionary<Guid, UploadOperation>();
            foreach (var operation in await BackgroundUploader.GetCurrentUploadsForTransferGroupAsync(TransferGroup))
            {
                operations[operation.Guid] = operation;
            }
            if (additionalOperations != null)
            {
                foreach (var operation in additionalOperations.Where(operation => operation != null))
                {
                    operations[operation.Guid] = operation;
                }
            }
            return operations;
        }

        public static TransferOutcome Classify(UploadOperation operation, out uint statusCode)
        {
            statusCode = 0;
            if (operation == null)
            {
                return TransferOutcome.Missing;
            }

            var status = operation.Progress.Status;
            if (status != BackgroundTransferStatus.Completed &&
                status != BackgroundTransferStatus.Error &&
                status != BackgroundTransferStatus.Canceled)
            {
                return TransferOutcome.Active;
            }

            try
            {
                var response = operation.GetResponseInformation();
                if (response != null)
                {
                    statusCode = response.StatusCode;
                }
            }
            catch (Exception)
            {
            }

            switch (status)
            {
                case BackgroundTransferStatus.Completed:
                    if (statusCode == 409)
                    {
                        return TransferOutcome.AlreadyExists;
                    }
                    if (statusCode >= 200 && statusCode < 300)
                    {
                        return TransferOutcome.Succeeded;
                    }
                    // Without a response, requeue; the next pass checks OneDrive before uploading again.
                    return statusCode == 0 ? TransferOutcome.Restart : TransferOutcome.Failed;
                case BackgroundTransferStatus.Error:
                    if (statusCode == 409)
                    {
                        return TransferOutcome.AlreadyExists;
                    }
                    // The upload session expired or no longer matches the file; start a new one.
                    return statusCode == 404 || statusCode == 410 || statusCode == 416
                        ? TransferOutcome.Restart
                        : TransferOutcome.Failed;
                case BackgroundTransferStatus.Canceled:
                    return TransferOutcome.Restart;
                default:
                    return TransferOutcome.Active;
            }
        }

        public static string DescribeProgress(UploadOperation operation)
        {
            if (operation == null)
            {
                return "Finishing background upload…";
            }

            var progress = operation.Progress;
            var percentage = progress.TotalBytesToSend == 0
                ? 0
                : Math.Min(100, (int)Math.Round((double)progress.BytesSent * 100 / progress.TotalBytesToSend));
            switch (progress.Status)
            {
                case BackgroundTransferStatus.Running:
                    return "Uploading in the background (" + percentage + "%).";
                case BackgroundTransferStatus.PausedCostedNetwork:
                    return "Waiting for Wi-Fi (" + percentage + "% uploaded). Allow cellular data to continue now.";
                case BackgroundTransferStatus.PausedNoNetwork:
                    return "Waiting for a network connection (" + percentage + "% uploaded).";
                case BackgroundTransferStatus.PausedSystemPolicy:
                    return "Paused by Windows, for example by Battery Saver (" + percentage + "% uploaded).";
                case BackgroundTransferStatus.Idle:
                    return "Queued with Windows Background Transfer.";
                case BackgroundTransferStatus.Completed:
                case BackgroundTransferStatus.Error:
                case BackgroundTransferStatus.Canceled:
                    return "Finishing background upload…";
                default:
                    return "Background upload paused (" + percentage + "% uploaded).";
            }
        }

        public TransferBatch BeginBatch()
        {
            var batch = new TransferBatch();
            try
            {
                var completionGroup = new BackgroundTransferCompletionGroup();
                var builder = new BackgroundTaskBuilder
                {
                    Name = CompletionTaskName,
                    TaskEntryPoint = TaskEntryPoint
                };
                builder.SetTrigger(completionGroup.Trigger);
                batch.Registration = builder.Register();
                batch.CompletionGroup = completionGroup;
            }
            catch (Exception exception)
            {
                // Without a completion trigger the next scheduled check still records results.
                batch.SetupFailure = exception.GetType().Name + " (0x" + exception.HResult.ToString("X8") + ")";
            }
            return batch;
        }

        public Guid Start(TransferBatch batch, StorageFile file, ulong size, string uploadUrl, bool allowCellular)
        {
            var uploader = batch.CompletionGroup == null
                ? new BackgroundUploader()
                : new BackgroundUploader(batch.CompletionGroup);
            uploader.TransferGroup = TransferGroup;
            uploader.Method = "PUT";
            uploader.CostPolicy = GetCostPolicy(allowCellular);
            uploader.SetRequestHeader("Content-Range", "bytes 0-" + (size - 1) + "/" + size);
            var operation = uploader.CreateUpload(new Uri(uploadUrl), file);
            _ = operation.StartAsync();
            batch.StartedCount++;
            return operation.Guid;
        }

        public void CompleteBatch(TransferBatch batch)
        {
            if (batch == null || batch.CompletionGroup == null)
            {
                return;
            }

            if (batch.StartedCount > 0)
            {
                batch.CompletionGroup.Enable();
            }
            else
            {
                batch.Registration?.Unregister(false);
            }
        }

        public async Task ApplyCostPolicyAsync(bool allowCellular)
        {
            var policy = GetCostPolicy(allowCellular);
            foreach (var operation in (await GetOperationsAsync()).Values)
            {
                if (operation.CostPolicy != policy)
                {
                    operation.CostPolicy = policy;
                }
            }
        }

        public async Task<IDictionary<string, string>> DescribeTransfersAsync(
            IEnumerable<CameraTransferRecord> records)
        {
            var operations = await GetOperationsAsync();
            var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                UploadOperation operation;
                operations.TryGetValue(record.TransferId, out operation);
                descriptions[record.FilePath] = DescribeProgress(operation);
            }
            return descriptions;
        }

        public static void Cancel(UploadOperation operation)
        {
            if (operation == null)
            {
                return;
            }

            try
            {
                operation.AttachAsync().Cancel();
            }
            catch (Exception)
            {
            }
        }

        public async Task CancelAllAsync()
        {
            foreach (var operation in (await GetOperationsAsync()).Values)
            {
                Cancel(operation);
            }
            foreach (var registration in BackgroundTaskRegistration.AllTasks.Values
                .Where(task => task.Name == CompletionTaskName).ToList())
            {
                registration.Unregister(false);
            }
        }
    }
}
