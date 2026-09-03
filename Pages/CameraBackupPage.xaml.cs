using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using LiveDrive.Helpers;
using LiveDrive.Models;
using LiveDrive.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LiveDrive.Pages
{
    public sealed partial class CameraBackupPage : Page
    {
        private readonly AppServices _services;
        private readonly List<StorageFile> _files = new List<StorageFile>();
        private bool _isUploading;
        public ObservableCollection<BackupItem> Queue { get; } = new ObservableCollection<BackupItem>();
        public ObservableCollection<BackupItem> RecentlyUploaded { get; } = new ObservableCollection<BackupItem>();

        public CameraBackupPage()
        {
            InitializeComponent();
            _services = ((App)Application.Current).Services;
            Loaded += async (sender, args) => await LoadRecentlyUploadedAsync();
        }

        private async void ChooseMediaButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".mov");
            var picked = await picker.PickMultipleFilesAsync();
            var skippedDuplicates = 0;
            foreach (var file in picked)
            {
                if (_files.Any(queuedFile => string.Equals(queuedFile.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    skippedDuplicates++;
                    continue;
                }
                _files.Add(file);
                Queue.Add(new BackupItem { Name = file.Name });
            }
            EmptyText.Visibility = Queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (skippedDuplicates > 0)
            {
                await PageFeedback.ShowInfoAsync(skippedDuplicates + " duplicate " +
                    (skippedDuplicates == 1 ? "file was" : "files were") + " already in the upload queue.");
            }
        }

        private async void UploadQueueButton_Click(object sender, RoutedEventArgs e)
        {
            if (_files.Count == 0)
            {
                await PageFeedback.ShowInfoAsync("Choose one or more photos or videos first.");
                return;
            }

            UploadProgress.Value = 0;
            UploadProgressText.Text = "Preparing LiveDrive Camera Roll…";
            UploadProgressPanel.Visibility = Visibility.Visible;
            _isUploading = true;
            ChooseMediaButton.IsEnabled = false;
            UploadButton.IsEnabled = false;
            try
            {
                var destination = await _services.Graph.GetOrCreateRootFolderAsync("LiveDrive Camera Roll");
                var totalFiles = _files.Count;
                var completedFiles = 0;
                while (_files.Count > 0)
                {
                    var file = _files[0];
                    Queue[0].Status = "Uploading";
                    UploadProgressText.Text = "Uploading " + (completedFiles + 1) + " of " + totalFiles + ": " + file.Name;
                    var progress = new DelegateProgress<double>(value =>
                    {
                        UploadProgress.Value = (completedFiles + value) / totalFiles;
                    });
                    await _services.Graph.UploadAsync(destination.Id, file, progress, true);
                    await _services.CameraUploadHistory.AddAsync(file.Name);
                    RecentlyUploaded.Insert(0, new BackupItem { Name = file.Name, Status = "Uploaded" });
                    if (RecentlyUploaded.Count > 50)
                    {
                        RecentlyUploaded.RemoveAt(RecentlyUploaded.Count - 1);
                    }
                    _files.RemoveAt(0);
                    Queue.RemoveAt(0);
                    completedFiles++;
                    UpdateRecentlyUploadedButton();
                }
                UploadProgress.Value = 1;
                await PageFeedback.ShowInfoAsync("Queue uploaded to LiveDrive Camera Roll.");
            }
            catch (Exception exception)
            {
                if (Queue.Count > 0)
                {
                    Queue[0].Status = "Upload failed";
                }
                await PageFeedback.ShowErrorAsync(exception);
            }
            finally
            {
                _isUploading = false;
                ChooseMediaButton.IsEnabled = true;
                UploadButton.IsEnabled = true;
                EmptyText.Visibility = Queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                UploadProgressPanel.Visibility = Visibility.Collapsed;
            }
        }

        private async void RemoveQueueItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUploading)
            {
                await PageFeedback.ShowInfoAsync("Wait for the current upload to finish before changing the queue.");
                return;
            }

            var button = sender as FrameworkElement;
            var item = button?.DataContext as BackupItem;
            var index = item == null ? -1 : Queue.IndexOf(item);
            if (index < 0)
            {
                return;
            }

            _files.RemoveAt(index);
            Queue.RemoveAt(index);
            EmptyText.Visibility = Queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void RecentlyUploadedButton_Click(object sender, RoutedEventArgs e)
        {
            if (RecentlyUploaded.Count == 0)
            {
                await PageFeedback.ShowInfoAsync("No uploads have completed yet.");
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "Recently uploaded",
                Content = new ListView
                {
                    ItemsSource = RecentlyUploaded,
                    DisplayMemberPath = "Name",
                    Height = 320
                },
                CloseButtonText = "Close"
            };
            await dialog.ShowAsync();
        }

        private async Task LoadRecentlyUploadedAsync()
        {
            var names = await _services.CameraUploadHistory.LoadAsync();
            RecentlyUploaded.Clear();
            foreach (var name in names)
            {
                RecentlyUploaded.Add(new BackupItem { Name = name, Status = "Uploaded" });
            }
            UpdateRecentlyUploadedButton();
        }

        private void UpdateRecentlyUploadedButton()
        {
            RecentlyUploadedButton.Content = RecentlyUploaded.Count == 0
                ? "Recently uploaded"
                : "Recently uploaded (" + RecentlyUploaded.Count + ")";
        }
    }
}
