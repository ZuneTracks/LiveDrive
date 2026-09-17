using System;
using System.Threading;
using LiveDrive.Services;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Background;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LiveDrive
{
    sealed partial class App : Application
    {
        private const string ThemeSettingKey = "AppTheme";

        public App()
        {
            InitializeComponent();
            RequestedTheme = ReadSavedTheme();
            Services = new AppServices();
            Suspending += OnSuspending;
        }

        public AppServices Services { get; }

        public ApplicationTheme CurrentTheme => RequestedTheme;

        public void SaveThemePreference(bool useDarkTheme)
        {
            var theme = useDarkTheme ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationData.Current.LocalSettings.Values[ThemeSettingKey] = theme.ToString();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            if (rootFrame.Content == null)
            {
                rootFrame.Navigate(typeof(MainPage), e.Arguments);
            }

            Window.Current.Activate();
        }

        protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            base.OnBackgroundActivated(args);
            var deferral = args.TaskInstance.GetDeferral();
            var cancellation = new CancellationTokenSource();
            args.TaskInstance.Canceled += (sender, reason) => cancellation.Cancel();
            RunCameraBackupAsync(cancellation, deferral);
        }

        private async void RunCameraBackupAsync(CancellationTokenSource cancellation, BackgroundTaskDeferral deferral)
        {
            try
            {
                var uploadedCount = await Services.CameraBackup.UploadNewCameraRollItemsAsync(cancellation.Token);
                if (uploadedCount > 0)
                {
                    try
                    {
                        await Services.LiveTile.UpdateAsync();
                    }
                    catch (Exception exception)
                    {
                        Services.CameraBackupState.SaveLastResult("Camera Roll upload completed, but Live Tile update failed: " +
                            exception.Message);
                    }
                    ShowCameraBackupToast(uploadedCount);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Services.CameraBackupState.SaveLastResult("Scheduled Camera Roll backup was canceled.");
            }
            catch (Exception exception)
            {
                Services.CameraBackupState.SaveLastResult("Scheduled Camera Roll backup failed: " + exception.Message);
            }
            finally
            {
                cancellation.Dispose();
                deferral.Complete();
            }
        }

        private static void ShowCameraBackupToast(int uploadedCount)
        {
            var toastXml = new XmlDocument();
            toastXml.LoadXml("<toast><visual><binding template=\"ToastGeneric\"><text>LiveDrive</text><text>Uploaded " +
                uploadedCount + " new Camera Roll " + (uploadedCount == 1 ? "item." : "items.") +
                "</text></binding></visual></toast>");
            ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(toastXml));
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new System.Exception("Failed to load page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            e.SuspendingOperation.GetDeferral().Complete();
        }

        private static ApplicationTheme ReadSavedTheme()
        {
            object setting;
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(ThemeSettingKey, out setting) &&
                string.Equals(setting as string, ApplicationTheme.Dark.ToString(), System.StringComparison.OrdinalIgnoreCase))
            {
                return ApplicationTheme.Dark;
            }

            return ApplicationTheme.Light;
        }
    }
}
