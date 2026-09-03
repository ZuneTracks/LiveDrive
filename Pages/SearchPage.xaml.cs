using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using LiveDrive.Helpers;
using LiveDrive.Models;
using LiveDrive.Services;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace LiveDrive.Pages
{
    public sealed partial class SearchPage : Page
    {
        private readonly AppServices _services;
        public ObservableCollection<DriveItem> Results { get; } = new ObservableCollection<DriveItem>();

        public SearchPage()
        {
            InitializeComponent();
            _services = ((App)Application.Current).Services;
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            await SearchAsync();
        }

        private async void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                await SearchAsync();
            }
        }

        private async Task SearchAsync()
        {
            var query = SearchTextBox.Text.Trim();
            if (string.IsNullOrEmpty(query))
            {
                await PageFeedback.ShowInfoAsync("Enter a name to search for.");
                return;
            }

            LoadingPanel.Visibility = Visibility.Visible;
            EmptyPanel.Visibility = Visibility.Collapsed;
            try
            {
                var items = await _services.Graph.SearchAsync(query);
                Results.Clear();
                foreach (var item in items)
                {
                    Results.Add(item);
                }
                EmptyPanel.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (Results.Count == 0)
                {
                    ((TextBlock)EmptyPanel.Children[0]).Text = "No matching files or folders.";
                }
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
            finally
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
            }
        }
    }
}
