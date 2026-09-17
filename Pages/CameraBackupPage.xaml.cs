using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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
        private bool _isCheckingCameraRoll;
        private bool _isInitializingScheduledBackup;
        public ObservableCollection<BackupItem> Queue { get; } = new ObservableCollection<BackupItem>();
        public ObservableCollection<BackupItem> RecentlyUploaded { get; } = new ObservableCollection<BackupItem>();

        public CameraBackupPage()
        {
            InitializeComponent();
            _services = ((App)Application.Current).Services;
            Loaded += async (sender, args) =>
            {
                _isInitializingScheduledBackup = true;
                ScheduledBackupToggle.IsOn = _services.CameraBackupScheduler.IsEnabled;
                _isInitializingScheduledBackup = false;
                ScheduledBackupStatus.Text = GetScheduledBackupStatus();
                UpdateLastCameraRollScanStatus();
                await LoadRecentlyUploadedAsync();
            };
        }

        private async void ScheduledBackupToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializingScheduledBackup)
            {
                return;
            }

            try
            {
                if (ScheduledBackupToggle.IsOn)
                {
                    await _services.CameraBackupScheduler.EnableAsync();
                }
                else
                {
                    _services.CameraBackupScheduler.Disable();
                }
                ScheduledBackupStatus.Text = GetScheduledBackupStatus();
            }
            catch (Exception exception)
            {
                _isInitializingScheduledBackup = true;
                ScheduledBackupToggle.IsOn = false;
                _isInitializingScheduledBackup = false;
                ScheduledBackupStatus.Text = GetScheduledBackupStatus();
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private string GetScheduledBackupStatus()
        {
            var lastResult = _services.CameraBackupState.GetLastResult();
            if (!_services.CameraBackupScheduler.IsEnabled)
            {
                return string.IsNullOrEmpty(lastResult)
                    ? "Scheduled Camera Roll backup is off."
                    : "Scheduled Camera Roll backup is off. Last check: " + lastResult;
            }

            return string.IsNullOrEmpty(lastResult)
                ? "Scheduled Camera Roll backup is on. Windows runs it when resources allow."
                : "Scheduled Camera Roll backup is on. " + lastResult;
        }

        private void UpdateLastCameraRollScanStatus()
        {
            var lastScanStarted = _services.CameraBackupState.GetLastScanStarted();
            LastCameraRollScanStatus.Text = lastScanStarted.HasValue
                ? "Last Camera Roll scan started: " + lastScanStarted.Value.ToLocalTime().ToString("g") + "."
                : "Camera Roll has not been scanned yet.";
        }

        private async void CheckCameraRollButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCheckingCameraRoll)
            {
                return;
            }

            try
            {
                _isCheckingCameraRoll = true;
                CheckCameraRollButton.IsEnabled = false;
                ScheduledBackupStatus.Text = "Checking Camera Roll…";
                var progress = new DelegateProgress<string>(status => ScheduledBackupStatus.Text = status);
                var scanTask = _services.CameraBackup.UploadNewCameraRollItemsAsync(
                    CancellationToken.None, progress);
                UpdateLastCameraRollScanStatus();
                var uploadedCount = await scanTask;
                await LoadRecentlyUploadedAsync();
                ScheduledBackupStatus.Text = GetScheduledBackupStatus();
                await PageFeedback.ShowInfoAsync(uploadedCount == 0
                    ? _services.CameraBackupState.GetLastResult()
                    : "Uploaded " + uploadedCount + " new Camera Roll " +
                      (uploadedCount == 1 ? "item." : "items."));
            }
            catch (Exception exception)
            {
                ScheduledBackupStatus.Text = GetScheduledBackupStatus();
                UpdateLastCameraRollScanStatus();
                await PageFeedback.ShowErrorAsync(exception);
            }
            finally
            {
                _isCheckingCameraRoll = false;
                CheckCameraRollButton.IsEnabled = true;
            }
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
                var existingNames = new HashSet<string>(
                    (await _services.Graph.GetChildrenAsync(destination.Id))
                        .Where(item => !item.IsFolder)
                        .Select(item => item.Name),
                    StringComparer.OrdinalIgnoreCase);
                var totalFiles = _files.Count;
                var completedFiles = 0;
                var skippedFiles = 0;
                while (_files.Count > 0)
                {
                    var file = _files[0];
                    if (existingNames.Contains(file.Name))
                    {
                        Queue[0].Status = "Already uploaded";
                        await _services.CameraBackupState.MarkUploadedAsync(file.Path);
                        _files.RemoveAt(0);
                        Queue.RemoveAt(0);
                        skippedFiles++;
                        continue;
                    }

                    Queue[0].Status = "Uploading";
                    UploadProgressText.Text = "Uploading " + (completedFiles + 1) + " of " + totalFiles + ": " + file.Name;
                    var progress = new DelegateProgress<double>(value =>
                    {
                        UploadProgress.Value = (completedFiles + value) / totalFiles;
                    });
                    await _services.Graph.UploadAsync(destination.Id, file, progress, false, true);
                    await _services.CameraBackupState.MarkUploadedAsync(file.Path);
                    await _services.CameraUploadHistory.AddAsync(file.Name);
                    existingNames.Add(file.Name);
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
                await PageFeedback.ShowInfoAsync(completedFiles == 0
                    ? "No new files were uploaded. Skipped " + skippedFiles + " existing " +
                      (skippedFiles == 1 ? "file." : "files.")
                    : "Uploaded " + completedFiles + " " + (completedFiles == 1 ? "file" : "files") +
                      " to LiveDrive Camera Roll." +
                      (skippedFiles == 0 ? string.Empty : " Skipped " + skippedFiles + " existing " +
                          (skippedFiles == 1 ? "file." : "files.")));
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
