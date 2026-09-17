using System;
using LiveDrive.Pages;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LiveDrive
{
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            InitializeComponent();
            NavigationList.SelectedIndex = 0;
            Loaded += async (sender, args) => await LoadStorageUsageAsync();
        }

        private async System.Threading.Tasks.Task LoadStorageUsageAsync()
        {
            try
            {
                var token = await ((App)Application.Current).Services.Auth.GetStoredTokenAsync();
                if (token == null)
                {
                    StorageUsageText.Text = "OneDrive storage: sign in to view.";
                    StorageUsageBar.Visibility = Visibility.Collapsed;
                    return;
                }

                var quota = await ((App)Application.Current).Services.Graph.GetQuotaAsync();
                StorageUsageText.Text = "OneDrive storage: " + FormatStorage(quota.Used) + " of " +
                    FormatStorage(quota.Total) + " used";
                if (quota.Total > 0)
                {
                    StorageUsageBar.Value = Math.Min(1, Math.Max(0, (double)quota.Used / quota.Total));
                    StorageUsageBar.Visibility = Visibility.Visible;
                }
                else
                {
                    StorageUsageBar.Visibility = Visibility.Collapsed;
                }
            }
            catch (InvalidOperationException)
            {
                StorageUsageText.Text = "OneDrive storage: unavailable.";
                StorageUsageBar.Visibility = Visibility.Collapsed;
            }
        }

        private static string FormatStorage(long bytes)
        {
            const long gigabyte = 1024L * 1024 * 1024;
            return string.Format("{0:0.#} GB", (double)bytes / gigabyte);
        }

        private void NavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = NavigationList.SelectedItem as ListBoxItem;
            if (selected != null)
            {
                Navigate(selected.Tag as string);
            }
        }

        private void NavigateButton_Click(object sender, RoutedEventArgs e)
        {
            Navigate((sender as FrameworkElement).Tag as string);
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            ShellSplitView.IsPaneOpen = !ShellSplitView.IsPaneOpen;
        }

        private void Navigate(string destination)
        {
            if (destination == "Drive")
            {
                ContentFrame.Navigate(typeof(DrivePage));
            }
            else if (destination == "Search")
            {
                ContentFrame.Navigate(typeof(SearchPage));
            }
            else if (destination == "Photos")
            {
                ContentFrame.Navigate(typeof(PhotosPage));
            }
            else if (destination == "Videos")
            {
                ContentFrame.Navigate(typeof(PhotosPage), "Videos");
            }
            else if (destination == "Albums")
            {
                ContentFrame.Navigate(typeof(AlbumsPage));
            }
            else if (destination == "Collections")
            {
                ContentFrame.Navigate(typeof(CollectionsPage));
            }
            else if (destination == "Backup")
            {
                ContentFrame.Navigate(typeof(CameraBackupPage));
            }
            else if (destination == "Settings")
            {
                ContentFrame.Navigate(typeof(SettingsPage));
            }

            if (ShellSplitView.DisplayMode == SplitViewDisplayMode.Overlay)
            {
                ShellSplitView.IsPaneOpen = false;
            }
        }
    }
}
