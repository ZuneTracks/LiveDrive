using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using LiveDrive.Helpers;
using LiveDrive.Models;
using LiveDrive.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LiveDrive.Pages
{
    public sealed partial class AlbumsPage : Page
    {
        private readonly AppServices _services;
        public ObservableCollection<PhotoAlbum> Albums { get; } = new ObservableCollection<PhotoAlbum>();

        public AlbumsPage()
        {
            InitializeComponent();
            _services = ((App)Application.Current).Services;
            Loaded += async (sender, args) => await LoadAlbumsAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadAlbumsAsync();
        }

        private async void NewAlbumButton_Click(object sender, RoutedEventArgs e)
        {
            var nameBox = new TextBox { PlaceholderText = "Album name" };
            var dialog = new ContentDialog
            {
                Title = "New album",
                Content = nameBox,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel"
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                await _services.Albums.CreateAlbumAsync(nameBox.Text);
                await LoadAlbumsAsync();
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private async Task LoadAlbumsAsync()
        {
            try
            {
                var albums = await _services.Albums.GetAlbumsAsync();
                Albums.Clear();
                foreach (var album in albums)
                {
                    Albums.Add(album);
                }
                EmptyPanel.Visibility = Albums.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private void AlbumsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            Frame.Navigate(typeof(PhotosPage), (PhotoAlbum)e.ClickedItem);
        }
    }
}
