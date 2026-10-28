using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveDrive.Helpers;
using LiveDrive.Models;
using LiveDrive.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LiveDrive.Pages
{
    public sealed partial class CameraBackupPage : Page
    {
        private readonly AppServices _services;
        private readonly List<StorageFile> _files = new List<StorageFile>();
        private bool _isUploading;
        private bool _isCheckingCameraRoll;
        private bool _isInitializingScheduledBackup;
        private bool _isRefreshingBackupStatus;
        private bool _isUpdatingScheduledBackup;
        private bool _hasPendingBackupStatusRefresh;
        private readonly DispatcherTimer _backupStatusRefreshTimer;
        private readonly ObservableCollection<ScheduledBackupQueueItem> _scheduledQueueItems =
            new ObservableCollection<ScheduledBackupQueueItem>();
        private ScheduledBackupQueueItem _pendingScheduledQueueRemoval;
        private StackPanel _scheduledQueueRemovalConfirmationPanel;
        private TextBlock _scheduledQueueRemovalConfirmationText;
        public ObservableCollection<BackupItem> Queue { get; } = new ObservableCollection<BackupItem>();
        public ObservableCollection<BackupItem> RecentlyUploaded { get; } = new ObservableCollection<BackupItem>();

        public CameraBackupPage()
        {
            InitializeComponent();
            _services = ((App)Application.Current).Services;
            _backupStatusRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(15)
            };
            _backupStatusRefreshTimer.Tick += BackupStatusRefreshTimer_Tick;
            Loaded += async (sender, args) =>
            {
                BackupDiagnosticsPanel.Visibility = DiagnosticsSettings.ShowTools
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                await RefreshBackupStatusAsync();
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            Application.Current.Resuming -= Application_Resuming;
            Application.Current.Resuming += Application_Resuming;
            _backupStatusRefreshTimer.Start();
            base.OnNavigatedTo(e);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            Application.Current.Resuming -= Application_Resuming;
            _backupStatusRefreshTimer.Stop();
            base.OnNavigatedFrom(e);
        }

        private async void Application_Resuming(object sender, object e)
        {
            await RefreshBackupStatusAsync();
        }

        private async void BackupStatusRefreshTimer_Tick(object sender, object e)
        {
            await RefreshBackupStatusAsync();
        }

        private async Task RefreshBackupStatusAsync(bool refreshScheduledStatus = true)
        {
            if (_isCheckingCameraRoll || _isUploading || _isUpdatingScheduledBackup)
            {
                _hasPendingBackupStatusRefresh = true;
                return;
            }

            if (_isRefreshingBackupStatus)
            {
                _hasPendingBackupStatusRefresh = true;
                return;
            }

            var shouldRefreshScheduledStatus = refreshScheduledStatus;
            do
            {
                _hasPendingBackupStatusRefresh = false;
                try
                {
                    _isRefreshingBackupStatus = true;
                    _isInitializingScheduledBackup = true;
                    ScheduledBackupToggle.IsOn = _services.CameraBackupScheduler.IsEnabled;
                    _isInitializingScheduledBackup = false;
                    if (shouldRefreshScheduledStatus)
                    {
                        ScheduledBackupStatus.Text = GetScheduledBackupStatus();
                    }
                    UpdateLastCameraRollScanStatus();
                    var scheduledQueueItems = await LoadScheduledQueueItemsAsync();
                    ScheduledQueueStatus.Content = scheduledQueueItems.Count == 0
                        ? "Scheduled backup queue is empty."
                        : "Scheduled backup queue: " + scheduledQueueItems.Count + " item" +
                          (scheduledQueueItems.Count == 1 ? " waiting. View queue" : "s waiting. View queue");
                    ScheduledQueueStatus.IsEnabled = scheduledQueueItems.Count > 0;
                    UpdateBackupHealthStatus(scheduledQueueItems);
                    await LoadRecentlyUploadedAsync();
                }
                catch (Exception exception)
                {
                    ScheduledBackupStatus.Text = "Could not refresh Camera Roll status: " + exception.Message;
                }
                finally
                {
                    _isInitializingScheduledBackup = false;
                    _isRefreshingBackupStatus = false;
                }

                shouldRefreshScheduledStatus = true;
            }
            while (_hasPendingBackupStatusRefresh &&
                   !_isCheckingCameraRoll && !_isUploading && !_isUpdatingScheduledBackup);
        }

        private async Task<List<ScheduledBackupQueueItem>> LoadScheduledQueueItemsAsync()
        {
            var pendingPaths = await _services.CameraBackupState.LoadPendingPathsAsync();
            var retryStates = await _services.CameraBackupState.LoadPendingRetryStatesAsync();
            var retryStatesByPath = retryStates.ToDictionary(
                state => state.FilePath, StringComparer.OrdinalIgnoreCase);
            var uploadSession = await _services.CameraBackupState.LoadUploadSessionAsync();
            var now = DateTimeOffset.UtcNow;
            return pendingPaths.Select(path =>
            {
                CameraBackupRetryState retryState;
                retryStatesByPath.TryGetValue(path, out retryState);
                return new ScheduledBackupQueueItem
                {
                    Path = path,
                    Name = Path.GetFileName(path),
                    Status = GetScheduledQueueItemStatus(path, retryState, uploadSession, now),
                    IsLargeUploadInProgress = uploadSession != null && string.Equals(
                        uploadSession.FilePath, path, StringComparison.OrdinalIgnoreCase),
                    IsAwaitingRetry = retryState != null && retryState.NextRetryUtc > now
                };
            }).ToList();
        }

        private static string GetScheduledQueueItemStatus(
            string path, CameraBackupRetryState retryState,
            CameraUploadSessionState uploadSession, DateTimeOffset now)
        {
            if (uploadSession != null && string.Equals(
                uploadSession.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                var percentage = uploadSession.FileSize == 0
                    ? 0
                    : Math.Min(100, (int)Math.Round(
                        (double)uploadSession.NextOffset * 100 / uploadSession.FileSize));
                return "Large upload paused at " + percentage +
                    "%; it will continue at the next scheduled check.";
            }

            if (retryState != null && retryState.NextRetryUtc > now)
            {
                return "Retry " + retryState.UnavailableCount + " after " +
                    retryState.NextRetryUtc.ToLocalTime().ToString("g") + ".";
            }

            return retryState == null
                ? "Waiting to upload."
                : "Ready to retry (attempt " + retryState.UnavailableCount + ").";
        }

        private void UpdateBackupHealthStatus(IReadOnlyCollection<ScheduledBackupQueueItem> scheduledQueueItems)
        {
            if (!_services.CameraBackupScheduler.IsEnabled)
            {
                BackupHealthStatus.Text = "Scheduled backup is off.";
                return;
            }

            if (!_services.CameraBackupScheduler.IsRegistered)
            {
                BackupHealthStatus.Text =
                    "Action: leave scheduled backup on while LiveDrive restores its Windows task.";
                return;
            }

            var largeUpload = scheduledQueueItems.FirstOrDefault(item => item.IsLargeUploadInProgress);
            if (largeUpload != null)
            {
                BackupHealthStatus.Text =
                    "Action: a resumable large upload is in progress. Windows continues it on later scheduled checks.";
                return;
            }

            var deferredCount = scheduledQueueItems.Count(item => item.IsAwaitingRetry);
            if (deferredCount > 0)
            {
                BackupHealthStatus.Text = "Action: " + deferredCount + " queued item" +
                    (deferredCount == 1 ? " is" : "s are") +
                    " waiting to retry. Open Scheduled backup queue to inspect or remove a stuck item.";
                return;
            }

            var lastActivation = _services.CameraBackupState.GetLastBackgroundActivation();
            if (!lastActivation.HasValue)
            {
                BackupHealthStatus.Text =
                    "No background activation has been recorded yet. Windows runs scheduled backup when resources allow.";
                return;
            }

            if (DateTimeOffset.Now - lastActivation.Value.ToLocalTime() > TimeSpan.FromHours(2))
            {
                BackupHealthStatus.Text =
                    "No recent background activation was recorded. Windows may defer scheduled work; open the queue to review waiting items.";
                return;
            }

            BackupHealthStatus.Text = scheduledQueueItems.Count == 0
                ? "Scheduled backup is healthy; no Camera Roll items are waiting."
                : "Scheduled backup is ready to process " + scheduledQueueItems.Count + " queued item" +
                  (scheduledQueueItems.Count == 1 ? "." : "s.");
        }

        private async void ScheduledQueueStatus_Click(object sender, RoutedEventArgs e)
        {
            if (_isCheckingCameraRoll || _isUploading || _isUpdatingScheduledBackup)
            {
                await PageFeedback.ShowInfoAsync(
                    "Wait for the current Camera Roll operation to finish before changing the scheduled queue.");
                return;
            }

            var items = await LoadScheduledQueueItemsAsync();
            _scheduledQueueItems.Clear();
            foreach (var item in items)
            {
                _scheduledQueueItems.Add(item);
            }
            if (_scheduledQueueItems.Count == 0)
            {
                await PageFeedback.ShowInfoAsync("The scheduled backup queue is empty.");
                return;
            }

            var list = new ListView
            {
                ItemsSource = _scheduledQueueItems,
                ItemTemplate = Resources["ScheduledQueueItemTemplate"] as DataTemplate,
                SelectionMode = ListViewSelectionMode.None,
                Height = 360
            };
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = "Removing an item only stops LiveDrive from retrying it. " +
                    "It does not delete the file from your device or OneDrive.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            _scheduledQueueRemovalConfirmationPanel = new StackPanel
            {
                Visibility = Visibility.Collapsed,
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 12)
            };
            _scheduledQueueRemovalConfirmationText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var confirmationButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var confirmRemove = new Button
            {
                Content = "Remove"
            };
            confirmRemove.Click += ConfirmScheduledQueueItemRemoval_Click;
            var cancelRemove = new Button
            {
                Content = "Cancel",
                Margin = new Thickness(8, 0, 0, 0)
            };
            cancelRemove.Click += CancelScheduledQueueItemRemoval_Click;
            confirmationButtons.Children.Add(confirmRemove);
            confirmationButtons.Children.Add(cancelRemove);
            _scheduledQueueRemovalConfirmationPanel.Children.Add(_scheduledQueueRemovalConfirmationText);
            _scheduledQueueRemovalConfirmationPanel.Children.Add(confirmationButtons);
            content.Children.Add(_scheduledQueueRemovalConfirmationPanel);
            content.Children.Add(list);
            await new ContentDialog
            {
                Title = "Scheduled backup queue",
                Content = content,
                CloseButtonText = "Close"
            }.ShowAsync();
            _pendingScheduledQueueRemoval = null;
            _scheduledQueueRemovalConfirmationPanel = null;
            _scheduledQueueRemovalConfirmationText = null;
        }

        private void RemoveScheduledQueueItemButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as FrameworkElement;
            var item = button?.Tag as ScheduledBackupQueueItem;
            if (item == null)
            {
                return;
            }

            _pendingScheduledQueueRemoval = item;
            _scheduledQueueRemovalConfirmationText.Text =
                "Remove " + item.Name + " from the scheduled backup queue? " +
                "This does not delete the file from your device or OneDrive.";
            _scheduledQueueRemovalConfirmationPanel.Visibility = Visibility.Visible;
        }

        private void CancelScheduledQueueItemRemoval_Click(object sender, RoutedEventArgs e)
        {
            _pendingScheduledQueueRemoval = null;
            _scheduledQueueRemovalConfirmationPanel.Visibility = Visibility.Collapsed;
        }

        private async void ConfirmScheduledQueueItemRemoval_Click(object sender, RoutedEventArgs e)
        {
            var item = _pendingScheduledQueueRemoval;
            if (item == null)
            {
                return;
            }

            if (!await _services.CameraBackupState.TryAcquireBackgroundRunLockAsync())
            {
                _scheduledQueueRemovalConfirmationText.Text =
                    "A scheduled Camera Roll check is active. Try removing this item again shortly.";
                return;
            }

            try
            {
                if (await _services.CameraBackupState.RemovePendingPathAsync(item.Path))
                {
                    _scheduledQueueItems.Remove(item);
                    await _services.CameraBackupState.AppendDiagnosticAsync(
                        "Camera Roll queue item removed", item.Name);
                    _services.CameraBackupState.SaveLastResult(
                        "Removed " + item.Name + " from the scheduled Camera Roll queue.");
                }
            }
            catch (Exception exception)
            {
                _scheduledQueueRemovalConfirmationText.Text =
                    "Could not remove this queue item: " + exception.Message;
                return;
            }
            finally
            {
                await _services.CameraBackupState.ReleaseBackgroundRunLockAsync();
            }

            _pendingScheduledQueueRemoval = null;
            _scheduledQueueRemovalConfirmationPanel.Visibility = Visibility.Collapsed;
            await RefreshBackupStatusAsync();
        }

        private async void ScheduledBackupToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializingScheduledBackup || _isUpdatingScheduledBackup)
            {
                return;
            }

            try
            {
                _isUpdatingScheduledBackup = true;
                ScheduledBackupToggle.IsEnabled = false;
                if (ScheduledBackupToggle.IsOn)
                {
                    var initialRun = await _services.CameraBackupScheduler.EnableAsync();
                    ScheduledBackupStatus.Text =
                        initialRun == Windows.ApplicationModel.Background.ApplicationTriggerResult.Allowed
                            ? "Scheduled Camera Roll backup is on. Windows accepted the initial background check."
                            : "Scheduled Camera Roll backup is on. Windows did not start the initial background check (" +
                              initialRun + "); it will run when resources allow.";
                }
                else
                {
                    _services.CameraBackupScheduler.Disable();
                }
            }
            catch (Exception exception)
            {
                _isInitializingScheduledBackup = true;
                ScheduledBackupToggle.IsOn = false;
                _isInitializingScheduledBackup = false;
                ScheduledBackupStatus.Text = GetScheduledBackupStatus();
                await PageFeedback.ShowErrorAsync(exception);
            }
            finally
            {
                _isUpdatingScheduledBackup = false;
                ScheduledBackupToggle.IsEnabled = true;
                await RefreshBackupStatusAsync(!ScheduledBackupToggle.IsOn);
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

            if (!_services.CameraBackupScheduler.IsRegistered)
            {
                return "Scheduled Camera Roll backup is on but its Windows task is being restored.";
            }

            return string.IsNullOrEmpty(lastResult)
                ? "Scheduled Camera Roll backup is on. Windows runs it when resources allow."
                : "Scheduled Camera Roll backup is on. " + lastResult;
        }

        private void UpdateLastCameraRollScanStatus()
        {
            var lastScanStarted = _services.CameraBackupState.GetLastScanStarted();
            var lastActivation = _services.CameraBackupState.GetLastBackgroundActivation();
            var scanStatus = lastScanStarted.HasValue
                ? "Last Camera Roll scan started: " + lastScanStarted.Value.ToLocalTime().ToString("g") + "."
                : "Camera Roll has not been scanned yet.";
            LastCameraRollScanStatus.Text = lastActivation.HasValue
                ? "Last background activation: " + lastActivation.Value.ToLocalTime().ToString("g") + ". " + scanStatus
                : scanStatus;
        }

        private async void CheckCameraRollButton_Click(object sender, RoutedEventArgs e)
        {
            await CheckCameraRollAsync();
        }

        private async void BackupDiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            var entries = await _services.CameraBackupState.LoadDiagnosticsAsync();
            if (entries.Count == 0)
            {
                await PageFeedback.ShowInfoAsync(
                    "No Camera Backup diagnostic events have been recorded yet.");
                return;
            }

            var text = string.Join(Environment.NewLine, entries);
            var log = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                Height = 360
            };
            var dialog = new ContentDialog
            {
                Title = "Camera Backup diagnostics",
                Content = log,
                PrimaryButtonText = "Copy",
                CloseButtonText = "Close"
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
                await PageFeedback.ShowInfoAsync("Camera Backup diagnostics copied.");
            }
        }

        private async void ClearDiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            await _services.CameraBackupState.ClearDiagnosticsAsync();
            await PageFeedback.ShowInfoAsync("Camera Backup diagnostics cleared.");
        }

        private async Task CheckCameraRollAsync()
        {
            if (_isCheckingCameraRoll)
            {
                return;
            }

            try
            {
                _isCheckingCameraRoll = true;
                CheckCameraRollButton.IsEnabled = false;
                ScheduledBackupToggle.IsEnabled = false;
                ScheduledBackupStatus.Text = "Checking Camera Roll…";
                var progress = new DelegateProgress<string>(status => ScheduledBackupStatus.Text = status);
                var scanTask = _services.CameraBackup.UploadNewCameraRollItemsAsync(
                    CancellationToken.None, progress);
                UpdateLastCameraRollScanStatus();
                var uploadedCount = await scanTask;
                await PageFeedback.ShowInfoAsync(uploadedCount == 0
                    ? _services.CameraBackupState.GetLastResult()
                    : "Uploaded " + uploadedCount + " new Camera Roll " +
                      (uploadedCount == 1 ? "item." : "items."));
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
            finally
            {
                _isCheckingCameraRoll = false;
                CheckCameraRollButton.IsEnabled = true;
                ScheduledBackupToggle.IsEnabled = true;
                await RefreshBackupStatusAsync();
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

                    var queueItem = Queue[0];
                    queueItem.Status = "Uploading";
                    queueItem.QueueVisibility = Visibility.Collapsed;
                    UploadProgressText.Text = GetManualUploadProgressText(
                        completedFiles, totalFiles, file.Name, 0);
                    var progress = new DelegateProgress<double>(value =>
                    {
                        UploadProgress.Value = (completedFiles + value) / totalFiles;
                        UploadProgressText.Text = GetManualUploadProgressText(
                            completedFiles, totalFiles, file.Name, value);
                    });
                    try
                    {
                        await _services.Graph.UploadAsync(destination.Id, file, progress, false, true);
                    }
                    catch (Exception exception) when (CameraBackupService.IsNameAlreadyExistsConflict(exception))
                    {
                        Queue[0].Status = "Already uploaded";
                        await _services.CameraBackupState.MarkUploadedAsync(file.Path);
                        existingNames.Add(file.Name);
                        _files.RemoveAt(0);
                        Queue.RemoveAt(0);
                        skippedFiles++;
                        continue;
                    }
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
                var result = completedFiles == 0
                    ? "No new files were uploaded. Skipped " + skippedFiles + " existing " +
                      (skippedFiles == 1 ? "file." : "files.")
                    : "Uploaded " + completedFiles + " " + (completedFiles == 1 ? "file" : "files") +
                      " to LiveDrive Camera Roll." +
                      (skippedFiles == 0 ? string.Empty : " Skipped " + skippedFiles + " existing " +
                          (skippedFiles == 1 ? "file." : "files."));
                _services.CameraBackupState.SaveLastResult(result);
                await PageFeedback.ShowInfoAsync(result);
            }
            catch (Exception exception)
            {
                if (Queue.Count > 0)
                {
                    Queue[0].Status = "Upload failed";
                    Queue[0].QueueVisibility = Visibility.Visible;
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
                await RefreshBackupStatusAsync();
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
            if (RecentlyUploaded.Select(item => item.Name).SequenceEqual(names, StringComparer.Ordinal))
            {
                UpdateRecentlyUploadedButton();
                return;
            }

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

        private static string GetManualUploadProgressText(
            int completedFiles, int totalFiles, string fileName, double progress)
        {
            return "Uploading " + (completedFiles + 1) + " of " + totalFiles + ": " + fileName;
        }
    }
}
