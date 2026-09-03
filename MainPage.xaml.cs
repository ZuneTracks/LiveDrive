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
