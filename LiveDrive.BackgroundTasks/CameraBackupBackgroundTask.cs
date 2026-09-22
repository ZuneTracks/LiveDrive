using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveDrive.Services;
using Windows.ApplicationModel.Background;
using Windows.Data.Xml.Dom;
using Windows.Storage;
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
            try
            {
                var uploadedCount = await backup.UploadNewCameraRollItemsAsync(cancellation.Token);
                if (uploadedCount > 0)
                {
                    SubmitCompletionToast(state, uploadedCount);
                    cancellation.Token.ThrowIfCancellationRequested();

                    try
                    {
                        await UpdateRelevantLiveTileAsync(graph);
                    }
                    catch (Exception exception)
                    {
                        state.SaveLastResult(
                            "Camera Roll upload completed, but Live Tile update failed: " + exception.Message);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                var reason = getCancellationReason();
                state.SaveLastResult(
                    "Scheduled Camera Roll backup was canceled (" +
                    (reason.HasValue ? reason.Value.ToString() : "unknown reason") + ").");
            }
            catch (Exception exception)
            {
                state.SaveLastResult("Scheduled Camera Roll backup failed: " + exception.Message);
                state.SaveToastDiagnostic(
                    "Scheduled Camera Roll backup failed at " + DateTimeOffset.Now.ToString("g") + ". " +
                    exception.Message);
            }
            finally
            {
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

        private static async Task UpdateRelevantLiveTileAsync(GraphClient graph)
        {
            object modeValue;
            if (!ApplicationData.Current.LocalSettings.Values.TryGetValue("LiveTileMode", out modeValue))
            {
                return;
            }

            var mode = modeValue as string;
            string title;
            string detail;
            if (mode == "StorageUsage")
            {
                var quota = await graph.GetQuotaAsync();
                title = "OneDrive storage";
                detail = FormatSize(quota.Used) + " of " + FormatSize(quota.Total) + " used";
            }
            else if (mode == "RecentFile")
            {
                var recent = (await graph.GetRecentAsync()).FirstOrDefault();
                title = "Recent file";
                detail = recent == null ? "No recent files found." : recent.Name;
            }
            else
            {
                return;
            }

            var xml = new XmlDocument();
            xml.LoadXml("<tile><visual displayName=\"LiveDrive\"><binding template=\"TileMedium\"><text hint-style=\"caption\">" +
                Escape(title) + "</text><text hint-style=\"captionsubtle\">" + Escape(detail) +
                "</text></binding><binding template=\"TileWide\"><text hint-style=\"caption\">" + Escape(title) +
                "</text><text hint-style=\"captionsubtle\">" + Escape(detail) +
                "</text></binding></visual></tile>");
            var updater = TileUpdateManager.CreateTileUpdaterForApplication();
            updater.Clear();
            updater.EnableNotificationQueue(false);
            updater.Update(new TileNotification(xml));
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private static string FormatSize(long size)
        {
            const long gigabyte = 1024L * 1024 * 1024;
            const long megabyte = 1024L * 1024;
            return size >= gigabyte ? string.Format("{0:0.#} GB", (double)size / gigabyte) :
                size >= megabyte ? string.Format("{0:0.#} MB", (double)size / megabyte) :
                string.Format("{0:N0} bytes", size);
        }
    }
}
