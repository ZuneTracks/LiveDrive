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
            if (!await state.TryAcquireBackgroundRunLockAsync())
            {
                deferral.Complete();
                cancellation.Dispose();
                return;
            }
            try
            {
                var uploadedCount = await backup.UploadNextPendingCameraRollItemAsync(cancellation.Token);
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
            }
            catch (Exception exception)
            {
                state.SaveLastResult("Background Camera Roll backup failed: " + exception.Message);
                state.SaveToastDiagnostic(
                    "Background Camera Roll backup failed at " + DateTimeOffset.Now.ToString("g") + ". " +
                    exception.Message);
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
