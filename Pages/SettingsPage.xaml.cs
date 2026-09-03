using System;
using LiveDrive.Helpers;
using LiveDrive.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LiveDrive.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private readonly AppServices _services;
        private bool _isInitializing;

        public SettingsPage()
        {
            InitializeComponent();
            _services = ((App)Application.Current).Services;
            _isInitializing = true;
            ThemeToggle.IsOn = ((App)Application.Current).CurrentTheme == ApplicationTheme.Dark;
            _isInitializing = false;
            ThemeStatus.Text = "Theme changes take effect the next time LiveDrive starts.";
            Loaded += async (sender, args) =>
            {
                var token = await _services.Auth.GetStoredTokenAsync();
                AccountStatus.Text = token == null ? "Not signed in." : "Signed in. Your session is stored securely on this device.";
            };
        }

        private async void SignInButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _services.Auth.SignInAsync();
                await _services.PhotoIndex.ClearAsync();
                _services.CameraUploadHistory.Clear();
                AccountStatus.Text = "Signed in. Open Drive to browse your files.";
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private async void SignOutButton_Click(object sender, RoutedEventArgs e)
        {
            await _services.Auth.SignOutAsync();
            await _services.PhotoIndex.ClearAsync();
            _services.CameraUploadHistory.Clear();
            AccountStatus.Text = "Not signed in.";
        }

        private async void ThemeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isInitializing)
            {
                ((App)Application.Current).SaveThemePreference(ThemeToggle.IsOn);
                ThemeStatus.Text = "Theme saved. Close and reopen LiveDrive to apply it.";
                await PageFeedback.ShowInfoAsync("Theme saved. Close and reopen LiveDrive to apply it.");
            }
        }
    }
}
