using System;
using LiveDrive.Services;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
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

        public string SendCameraBackupToastDiagnostic()
        {
            return ShowCameraBackupToast(1, true);
        }

        private string ShowCameraBackupToast(int uploadedCount, bool isDiagnostic)
        {
            var notifier = ToastNotificationManager.CreateToastNotifier();
            var notifierSetting = notifier.Setting.ToString();
            try
            {
                var toastXml = new XmlDocument();
                var message = isDiagnostic
                    ? "Notification diagnostic test."
                    : "Uploaded " + uploadedCount + " new Camera Roll " +
                      (uploadedCount == 1 ? "item." : "items.");
                toastXml.LoadXml("<toast><visual><binding template=\"ToastGeneric\"><text>LiveDrive</text><text>" +
                    message + "</text></binding></visual></toast>");
                notifier.Show(new ToastNotification(toastXml));
                var result = "Toast submitted at " + DateTimeOffset.Now.ToString("g") +
                    ". Device setting: " + notifierSetting + ".";
                Services.CameraBackupState.SaveToastDiagnostic(result);
                return result;
            }
            catch (Exception exception)
            {
                var result = "Toast failed at " + DateTimeOffset.Now.ToString("g") +
                    ". Device setting: " + notifierSetting + ". " + exception.Message;
                Services.CameraBackupState.SaveToastDiagnostic(result);
                throw;
            }
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
