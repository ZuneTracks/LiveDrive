using System;
using System.Linq;
using System.Threading.Tasks;
using LiveDrive.Helpers;
using LiveDrive.Models;
using LiveDrive.Services;
using Windows.ApplicationModel;
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
                SelectLiveTileMode(_services.LiveTile.Mode);
                await UpdateStorageStatusAsync(token != null);
                UpdateLiveTileControls();
                NotificationDiagnosticStatus.Text = string.IsNullOrEmpty(_services.CameraBackupState.GetToastDiagnostic())
                    ? "No notification diagnostic has run yet."
                    : _services.CameraBackupState.GetToastDiagnostic();
            };
        }

        private async void SignInButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _services.Auth.SignInAsync();
                await _services.PhotoIndex.ClearAsync();
                _services.CameraUploadHistory.Clear();
                await _services.CameraBackupState.ClearAsync();
                AccountStatus.Text = "Signed in. Open Drive to browse your files.";
                await UpdateStorageStatusAsync(true);
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
            await _services.CameraBackupState.ClearAsync();
            _services.CameraBackupScheduler.Disable();
            _services.LiveTile.Clear();
            AccountStatus.Text = "Not signed in.";
            StorageStatus.Text = "OneDrive storage is available after you sign in.";
        }

        private async void LiveTileMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || LiveTileMode.SelectedItem == null)
            {
                return;
            }

            try
            {
                var mode = ((ComboBoxItem)LiveTileMode.SelectedItem).Tag as string;
                _services.LiveTile.SetMode(mode);
                await _services.LiveTile.UpdateAsync();
                UpdateLiveTileControls();
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private async void ChooseTileItemButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_services.LiveTile.Mode == LiveTileService.SelectedPhoto)
                {
                    var photos = (await _services.PhotoIndex.LoadAsync()).Items;
                    await ChooseTileItemAsync(photos, "Choose photo for Live Tile");
                }
                else if (_services.LiveTile.Mode == LiveTileService.SelectedAlbum)
                {
                    await ChooseTileItemAsync(await _services.Albums.GetAlbumsAsync(), "Choose album for Live Tile");
                }
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private async void RefreshLiveTileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RefreshLiveTileButton.IsEnabled = false;
                await _services.LiveTile.UpdateAsync();
                LiveTileStatus.Text = _services.LiveTile.Mode == LiveTileService.Off
                    ? "The Live Tile is off."
                    : "Live Tile refreshed.";
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
            finally
            {
                RefreshLiveTileButton.IsEnabled = true;
            }
        }

        private async Task ChooseTileItemAsync<T>(System.Collections.Generic.IEnumerable<T> items, string title)
        {
            var picker = new ListBox { ItemsSource = items, DisplayMemberPath = "Name", SelectionMode = SelectionMode.Single };
            var dialog = new ContentDialog { Title = title, Content = picker, PrimaryButtonText = "Choose", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = false };
            picker.SelectionChanged += (sender, args) => dialog.IsPrimaryButtonEnabled = picker.SelectedItem != null;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || picker.SelectedItem == null)
            {
                return;
            }

            if (picker.SelectedItem is DriveItem photo)
            {
                _services.LiveTile.SetSelection(photo.Id, photo.Name);
            }
            else if (picker.SelectedItem is PhotoAlbum album)
            {
                _services.LiveTile.SetSelection(album.Id, album.Name);
            }
            await _services.LiveTile.UpdateAsync();
            UpdateLiveTileControls();
        }

        private async Task UpdateStorageStatusAsync(bool isSignedIn)
        {
            if (!isSignedIn)
            {
                StorageStatus.Text = "OneDrive storage is available after you sign in.";
                return;
            }
            try
            {
                var quota = await _services.Graph.GetQuotaAsync();
                StorageStatus.Text = "OneDrive storage: " + FormatStorage(quota.Used) + " of " + FormatStorage(quota.Total) + " used.";
            }
            catch (Exception exception)
            {
                StorageStatus.Text = "Could not load OneDrive storage: " + exception.Message;
            }
        }

        private void SelectLiveTileMode(string mode)
        {
            _isInitializing = true;
            LiveTileMode.SelectedItem = LiveTileMode.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, mode, StringComparison.Ordinal));
            _isInitializing = false;
        }

        private void UpdateLiveTileControls()
        {
            var needsSelection = _services.LiveTile.Mode == LiveTileService.SelectedPhoto ||
                _services.LiveTile.Mode == LiveTileService.SelectedAlbum;
            ChooseTileItemButton.Visibility = needsSelection ? Visibility.Visible : Visibility.Collapsed;
            LiveTileStatus.Text = _services.LiveTile.Mode == LiveTileService.Off
                ? "The Live Tile is off."
                : needsSelection && string.IsNullOrEmpty(_services.LiveTile.SelectedName)
                    ? "Choose an item for the Live Tile."
                    : needsSelection ? "Showing " + _services.LiveTile.SelectedName + " on the Live Tile."
                    : "The Live Tile updates when LiveDrive refreshes.";
        }

        private static string FormatStorage(long bytes)
        {
            const long gigabyte = 1024L * 1024 * 1024;
            return string.Format("{0:0.#} GB", (double)bytes / gigabyte);
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

        private void NotificationDiagnosticButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                NotificationDiagnosticStatus.Text = ((App)Application.Current).SendCameraBackupToastDiagnostic();
            }
            catch (Exception exception)
            {
                NotificationDiagnosticStatus.Text = _services.CameraBackupState.GetToastDiagnostic();
                _ = PageFeedback.ShowErrorAsync(exception);
            }
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            var version = Package.Current.Id.Version;
            var content = new StackPanel
            {
                Width = 300,
                Padding = new Thickness(16)
            };
            content.Children.Add(new TextBlock
            {
                Text = "LiveDrive",
                FontSize = 22,
                FontWeight = Windows.UI.Text.FontWeights.SemiLight
            });
            content.Children.Add(CreateAboutTextBlock(
                "An independent, phone-friendly UWP client for accessing your OneDrive through Microsoft Graph on Windows 10 Mobile.",
                new Thickness(0, 10, 0, 0)));
            content.Children.Add(CreateAboutTextBlock(
                "LiveDrive is not affiliated with Microsoft or any Microsoft subsidiaries.",
                new Thickness(0, 10, 0, 0)));
            content.Children.Add(CreateAboutTextBlock(
                string.Format("Build {0}.{1}.{2}.{3}", version.Major, version.Minor, version.Build, version.Revision),
                new Thickness(0, 10, 0, 0)));
            content.Children.Add(CreateAboutTextBlock("Developed by ZuneTracks", new Thickness(0, 10, 0, 0)));
            content.Children.Add(new HyperlinkButton
            {
                Content = "github.com/ZuneTracks",
                NavigateUri = new Uri("https://github.com/ZuneTracks"),
                Padding = new Thickness(0),
                Margin = new Thickness(0, 4, 0, 0)
            });

            var flyout = new Flyout { Content = content };
            flyout.ShowAt(AboutButton);
        }

        private static TextBlock CreateAboutTextBlock(string text, Thickness margin)
        {
            return new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = margin
            };
        }
    }
}
