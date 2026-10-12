using System;
using System.Threading;
using System.Threading.Tasks;
using LiveDrive.Services;
using Windows.ApplicationModel.Background;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace LiveDrive.BackgroundTasks
{
    public sealed class CameraBackupBackgroundTask : IBackgroundTask
    {
        public void Run(IBackgroundTaskInstance taskInstance)
        {
            var deferral = taskInstance.GetDeferral();
            var cancellation = new CancellationTokenSource();
            BackgroundTaskCancellationReason? cancellationReason = null;
            taskInstance.Canceled += (sender, reason) =>
            {
                cancellationReason = reason;
                cancellation.Cancel();
            };
            _ = RunAsync(cancellation, deferral, () => cancellationReason);
        }

        private static async Task RunAsync(
            CancellationTokenSource cancellation,
            BackgroundTaskDeferral deferral,
            Func<BackgroundTaskCancellationReason?> getCancellationReason)
        {
            var state = new CameraBackupStateStore();
            var graph = new GraphClient(new OAuthService(new PasswordVaultTokenStore()));
            var backup = new CameraBackupService(graph, new CameraUploadHistoryStore(), state);
            state.SaveLastBackgroundActivation(DateTimeOffset.Now);
            await state.AppendDiagnosticAsync("Background task activated");
            if (!state.IsScheduledBackupEnabled)
            {
                state.SaveLastResult("Ignored a Camera Roll background activation because scheduled backup is off.");
                await state.AppendDiagnosticAsync("Background task ignored", "Scheduled backup is off.");
                deferral.Complete();
                cancellation.Dispose();
                return;
            }
            if (!await state.TryAcquireBackgroundRunLockAsync())
            {
                await state.AppendDiagnosticAsync("Background task skipped", "Another Camera Roll task holds the lock.");
                deferral.Complete();
                cancellation.Dispose();
                return;
            }
            try
            {
                await state.AppendDiagnosticAsync("Background task lock acquired");
                var uploadedCount = await backup.UploadPendingCameraRollItemsAsync(cancellation.Token);
                await state.AppendDiagnosticAsync("Background task completed", "Uploaded " + uploadedCount + " item(s).");
                if (uploadedCount > 0)
                {
                    SubmitCompletionToast(state, uploadedCount);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                var reason = getCancellationReason();
                state.SaveLastResult(
                    "Background Camera Roll backup paused (" +
                    (reason.HasValue ? reason.Value.ToString() : "unknown reason") + ").");
                await state.AppendDiagnosticAsync(
                    "Background task cancelled", reason.HasValue ? reason.Value.ToString() : "Unknown reason");
            }
            catch (Exception exception)
            {
                state.SaveLastResult("Background Camera Roll backup failed: " + exception.Message);
                state.SaveToastDiagnostic(
                    "Background Camera Roll backup failed at " + DateTimeOffset.Now.ToString("g") + ". " +
                    exception.Message);
                await state.AppendDiagnosticAsync(
                    "Background task failed", exception.GetType().Name + " (0x" +
                    exception.HResult.ToString("X8") + "): " + exception.Message);
            }
            finally
            {
                await state.ReleaseBackgroundRunLockAsync();
                cancellation.Dispose();
                deferral.Complete();
            }
        }

        private static void SubmitCompletionToast(CameraBackupStateStore state, int uploadedCount)
        {
            var notifier = ToastNotificationManager.CreateToastNotifier();
            try
            {
                state.SaveToastDiagnostic(
                    "Scheduled upload completed at " + DateTimeOffset.Now.ToString("g") +
                    "; submitting toast. Device setting: " + notifier.Setting + ".");
                var toastXml = new XmlDocument();
                toastXml.LoadXml("<toast><visual><binding template=\"ToastGeneric\"><text>LiveDrive</text><text>Uploaded " +
                    uploadedCount + " new Camera Roll " + (uploadedCount == 1 ? "item." : "items.") +
                    "</text></binding></visual></toast>");
                notifier.Show(new ToastNotification(toastXml));
                state.SaveToastDiagnostic(
                    "Scheduled upload toast submitted at " + DateTimeOffset.Now.ToString("g") +
                    ". Device setting: " + notifier.Setting + ".");
            }
            catch (Exception exception)
            {
                state.SaveToastDiagnostic(
                    "Scheduled upload toast failed at " + DateTimeOffset.Now.ToString("g") + ". " +
                    exception.Message);
            }
        }

    }
}
