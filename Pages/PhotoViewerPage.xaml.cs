using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiveDrive.Helpers;
using LiveDrive.Models;
using LiveDrive.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace LiveDrive.Pages
{
    public sealed partial class PhotoViewerPage : Page
    {
        private readonly AppServices _services;
        private IReadOnlyList<DriveItem> _items = new List<DriveItem>();
        private int _loadVersion;

        public PhotoViewerPage()
        {
            InitializeComponent();
            _services = ((App)Application.Current).Services;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            var request = e.Parameter as PhotoViewerRequest;
            if (request == null || request.Items == null || request.Items.Count == 0)
            {
                if (Frame.CanGoBack)
                {
                    Frame.GoBack();
                }
                return;
            }

            _items = request.Items;
            PhotoFlipView.ItemsSource = _items;
            PhotoFlipView.SelectedIndex = Math.Max(0, Math.Min(request.SelectedIndex, _items.Count - 1));
            await LoadSelectedPhotoAsync();
            base.OnNavigatedTo(e);
        }

        private async void PhotoFlipView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await LoadSelectedPhotoAsync();
        }

        private async Task LoadSelectedPhotoAsync()
        {
            var item = PhotoFlipView.SelectedItem as DriveItem;
            if (item == null)
            {
                return;
            }

            var loadVersion = ++_loadVersion;
            LoadingPanel.Visibility = Visibility.Visible;
            PhotoNameText.Text = item.Name;
            UpdateNavigationState();
            try
            {
                var file = await CreateTemporaryPhotoFileAsync(item.Name);
                await _services.Graph.DownloadToAsync(item, file);
                if (loadVersion != _loadVersion)
                {
                    return;
                }

                using (var stream = await file.OpenReadAsync())
                {
                    var image = new BitmapImage();
                    await image.SetSourceAsync(stream);
                    if (loadVersion == _loadVersion)
                    {
                        PhotoImage.Source = image;
                    }
                }
            }
            catch (Exception exception)
            {
                if (loadVersion == _loadVersion)
                {
                    await PageFeedback.ShowErrorAsync(exception);
                }
            }
            finally
            {
                if (loadVersion == _loadVersion)
                {
                    LoadingPanel.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            PhotoFlipView.SelectedIndex--;
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            PhotoFlipView.SelectedIndex++;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private async void SaveAsButton_Click(object sender, RoutedEventArgs e)
        {
            var item = PhotoFlipView.SelectedItem as DriveItem;
            if (item == null)
            {
                return;
            }

            try
            {
                var picker = new FileSavePicker { SuggestedFileName = item.Name };
                picker.FileTypeChoices.Add("Photo", new[] { GetPhotoExtension(item.Name) });
                var destination = await picker.PickSaveFileAsync();
                if (destination != null)
                {
                    await _services.Graph.DownloadToAsync(item, destination);
                    await PageFeedback.ShowInfoAsync("Saved to " + destination.Name + ".");
                }
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private void UpdateNavigationState()
        {
            PreviousButton.IsEnabled = PhotoFlipView.SelectedIndex > 0;
            NextButton.IsEnabled = PhotoFlipView.SelectedIndex >= 0 &&
                PhotoFlipView.SelectedIndex < _items.Count - 1;
        }

        private static async Task<StorageFile> CreateTemporaryPhotoFileAsync(string originalName)
        {
            return await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                Guid.NewGuid().ToString("N") + GetPhotoExtension(originalName),
                CreationCollisionOption.FailIfExists);
        }

        private static string GetPhotoExtension(string name)
        {
            var index = name == null ? -1 : name.LastIndexOf('.');
            return index >= 0 && name.Length - index <= 12 ? name.Substring(index) : ".jpg";
        }
    }
}
