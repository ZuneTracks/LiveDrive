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
            var backgroundLockAcquired = false;
            try
            {
                state.SaveLastBackgroundActivation(DateTimeOffset.Now);
                await state.AppendDiagnosticAsync("Background task activated");
                if (!state.IsScheduledBackupEnabled)
                {
                    state.SaveLastResult("Ignored a Camera Roll background activation because scheduled backup is off.");
                    await state.AppendDiagnosticAsync("Background task ignored", "Scheduled backup is off.");
                    return;
                }

                await state.AppendDiagnosticAsync("Background task acquiring lock");
                backgroundLockAcquired = await state.TryAcquireBackgroundRunLockAsync();
                if (!backgroundLockAcquired)
                {
                    state.SaveLastResult(
                        "Scheduled Camera Roll backup is waiting for another Camera Roll operation to finish.");
                    await state.AppendDiagnosticAsync(
                        "Background task skipped", "Another Camera Roll task holds the lock.");
                    return;
                }

                await state.AppendDiagnosticAsync("Background task lock acquired");
                var graph = new GraphClient(new OAuthService(new PasswordVaultTokenStore()));
                var backup = new CameraBackupService(graph, new CameraUploadHistoryStore(), state);
                var uploadedCount = await backup.UploadPendingCameraRollItemsAsync(cancellation.Token);
                await state.AppendDiagnosticAsync("Background task completed", "Uploaded " + uploadedCount + " item(s).");
                if (uploadedCount > 0)
                {
                    SubmitCompletionToast(state, uploadedCount);
                }
            }
            catch (OperationCanceledException exception)
            {
                var reason = getCancellationReason();
                state.SaveLastResult(
                    reason.HasValue
                        ? "Background Camera Roll backup paused (" + reason.Value + "). It will retry later."
                        : "Background Camera Roll backup was interrupted by Windows or the network. " +
                          "Queued items remain and will retry later.");
                await state.AppendDiagnosticAsync(
                    "Background task cancelled",
                    reason.HasValue
                        ? reason.Value.ToString()
                        : exception.GetType().Name + " without a Windows cancellation signal.");
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
                if (backgroundLockAcquired)
                {
                    await state.ReleaseBackgroundRunLockAsync();
                }
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
