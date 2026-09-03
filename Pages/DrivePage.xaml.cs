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
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LiveDrive.Pages
{
    public sealed partial class DrivePage : Page
    {
        private readonly AppServices _services;
        private readonly ObservableCollection<DriveItem> _items = new ObservableCollection<DriveItem>();
        private string _currentFolderId;
        private readonly System.Collections.Generic.Stack<string> _history = new System.Collections.Generic.Stack<string>();

        public DrivePage()
        {
            InitializeComponent();
            _services = ((App)Application.Current).Services;
            ItemsList.ItemsSource = _items;
            UpdateCommandState();
            Loaded += async (sender, args) => await LoadFolderAsync();
        }

        private async Task LoadFolderAsync()
        {
            LoadingPanel.Visibility = Visibility.Visible;
            EmptyPanel.Visibility = Visibility.Collapsed;
            try
            {
                var items = await _services.Graph.GetChildrenAsync(_currentFolderId);
                ItemsList.SelectedItems.Clear();
                _items.Clear();
                foreach (var item in items)
                {
                    _items.Add(item);
                }
                UpdateCommandState();
                EmptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

        private async void UpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_history.Count > 0)
            {
                _currentFolderId = _history.Pop();
                await LoadFolderAsync();
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadFolderAsync();
        }

        private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCommandState();
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (ItemsList.SelectedItems.Count == _items.Count)
            {
                ItemsList.SelectedItems.Clear();
            }
            else
            {
                ItemsList.SelectAll();
            }
            UpdateCommandState();
        }

        private async void ViewButton_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedItems().SingleOrDefault();
            if (item == null)
            {
                return;
            }

            if (item.IsFolder)
            {
                _history.Push(_currentFolderId);
                _currentFolderId = item.Id;
                await LoadFolderAsync();
                return;
            }

            await DownloadAndOpenAsync(item);
        }

        private async void SaveAsButton_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedItems().SingleOrDefault();
            if (item != null)
            {
                await SaveAsAsync(item);
            }
        }

        private async void ShareButton_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedItems().SingleOrDefault();
            if (item != null)
            {
                await CreateShareLinkAsync(item);
            }
        }

        private async void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            await StageClipboardAsync(DriveClipboardOperation.Copy);
        }

        private async void MoveButton_Click(object sender, RoutedEventArgs e)
        {
            await StageClipboardAsync(DriveClipboardOperation.Move);
        }

        private async Task StageClipboardAsync(DriveClipboardOperation operation)
        {
            var selectedItems = GetSelectedItems();
            if (selectedItems.Count == 0)
            {
                return;
            }

            _services.Clipboard.Store(selectedItems, _currentFolderId, operation);
            ItemsList.SelectedItems.Clear();
            UpdateCommandState();
            try
            {
                var destination = await PickDestinationFolderAsync(
                    operation == DriveClipboardOperation.Copy ? "Choose copy destination" : "Choose move destination",
                    "Open folders to choose where the selected item(s) should be placed.",
                    "Paste here");
                if (destination == null)
                {
                    return;
                }
                if (!_services.Clipboard.CanPasteInto(destination.Id))
                {
                    await PageFeedback.ShowInfoAsync("Choose a different destination folder.");
                    return;
                }
                await PasteClipboardAsync(destination.Id, destination.Name);
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private async void PasteButton_Click(object sender, RoutedEventArgs e)
        {
            await PasteClipboardAsync(_currentFolderId, "this folder");
        }

        private async Task PasteClipboardAsync(string destinationFolderId, string destinationName)
        {
            if (!_services.Clipboard.CanPasteInto(destinationFolderId))
            {
                return;
            }
            try
            {
                var destinationId = destinationFolderId;
                if (string.IsNullOrEmpty(destinationId))
                {
                    destinationId = (await _services.Graph.GetRootFolderAsync()).Id;
                }

                foreach (var item in _services.Clipboard.Items)
                {
                    if (_services.Clipboard.Operation == DriveClipboardOperation.Copy)
                    {
                        await _services.Graph.CopyAsync(item, destinationId);
                    }
                    else
                    {
                        await _services.Graph.MoveAsync(item, destinationId);
                    }
                }

                var isMove = _services.Clipboard.Operation == DriveClipboardOperation.Move;
                var count = _services.Clipboard.Items.Count;
                if (isMove)
                {
                    _services.Clipboard.Clear();
                }
                await LoadFolderAsync();
                await PageFeedback.ShowInfoAsync((isMove ? "Moved " : "Copied ") + count + " item(s) to " + destinationName + ".");
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = GetSelectedItems();
            if (selectedItems.Count == 0)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "Delete selected items?",
                Content = "Delete " + selectedItems.Count + " item(s) from OneDrive? They can be restored from the OneDrive recycle bin.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel"
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                foreach (var item in selectedItems)
                {
                    await _services.Graph.DeleteAsync(item);
                }
                await LoadFolderAsync();
                await PageFeedback.ShowInfoAsync("Deleted " + selectedItems.Count + " item(s).");
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private async void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var destination = await PickDestinationFolderAsync(
                    "Choose upload folder",
                    "Open folders to choose where this file will be uploaded.",
                    "Use this folder");
                if (destination == null)
                {
                    return;
                }

                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                var file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                UploadProgress.Value = 0;
                UploadProgressText.Text = "Uploading " + file.Name + "…";
                UploadProgressPanel.Visibility = Visibility.Visible;
                var progress = new DelegateProgress<double>(value => UploadProgress.Value = value);
                await _services.Graph.UploadAsync(destination.Id, file, progress);
                UploadProgress.Value = 1;
                await LoadFolderAsync();
                await PageFeedback.ShowInfoAsync("Uploaded to " + destination.Name + ".");
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
            finally
            {
                UploadProgressPanel.Visibility = Visibility.Collapsed;
            }
        }

        private async Task<DriveItem> PickDestinationFolderAsync(string title, string instructions, string primaryButtonText)
        {
            return await OneDriveFolderPicker.PickAsync(_services.Graph, title, instructions, primaryButtonText);
        }

        private async Task SaveAsAsync(DriveItem item)
        {
            if (item.IsFolder)
            {
                return;
            }
            try
            {
                var picker = new FileSavePicker { SuggestedFileName = item.Name };
                picker.FileTypeChoices.Add("File", new[] { GetExtension(item.Name) });
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

        private async Task DownloadAndOpenAsync(DriveItem item)
        {
            try
            {
                var file = await CreateTemporaryDownloadFileAsync(item.Name);
                await _services.Graph.DownloadToAsync(item, file);
                if (!await Launcher.LaunchFileAsync(file))
                {
                    await PageFeedback.ShowInfoAsync("No app on this device can open this file. Use the action menu to save it.");
                }
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private async Task CreateShareLinkAsync(DriveItem item)
        {
            try
            {
                var link = await _services.Graph.CreateShareLinkAsync(item);
                if (string.IsNullOrEmpty(link))
                {
                    await PageFeedback.ShowInfoAsync("Microsoft Graph did not return a link.");
                    return;
                }
                var copied = false;
                try
                {
                    var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    package.SetText(link);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                    copied = true;
                }
                catch (Exception)
                {
                    // Clipboard access may be unavailable on an older phone shell.
                }
                await PageFeedback.ShowInfoAsync(copied ? "View link copied to the clipboard." : link);
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private static async Task<StorageFile> CreateTemporaryDownloadFileAsync(string originalName)
        {
            return await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                Guid.NewGuid().ToString("N") + GetSafeExtension(originalName),
                CreationCollisionOption.FailIfExists);
        }

        private static string GetExtension(string name)
        {
            var extension = name.LastIndexOf('.');
            return extension >= 0 ? name.Substring(extension) : ".dat";
        }

        private static string GetSafeExtension(string name)
        {
            var extension = GetExtension(name);
            if (extension.Length > 12)
            {
                return ".dat";
            }

            for (var index = 1; index < extension.Length; index++)
            {
                if (!char.IsLetterOrDigit(extension[index]))
                {
                    return ".dat";
                }
            }
            return extension.Length > 1 ? extension : ".dat";
        }

        private List<DriveItem> GetSelectedItems()
        {
            return ItemsList.SelectedItems.Cast<DriveItem>().ToList();
        }

        private void UpdateCommandState()
        {
            var selectedItems = GetSelectedItems();
            var isSingleFile = selectedItems.Count == 1 && !selectedItems[0].IsFolder;
            SelectAllButton.IsEnabled = _items.Count > 0;
            SelectAllButton.Label = _items.Count > 0 && selectedItems.Count == _items.Count ? "Clear selection" : "Select all";
            ViewButton.IsEnabled = selectedItems.Count == 1;
            SaveAsButton.IsEnabled = isSingleFile;
            ShareButton.IsEnabled = isSingleFile;
            CopyButton.IsEnabled = selectedItems.Count > 0;
            MoveButton.IsEnabled = selectedItems.Count > 0;
            DeleteButton.IsEnabled = selectedItems.Count > 0;
            PasteButton.IsEnabled = _services.Clipboard.CanPasteInto(_currentFolderId);
        }
    }
}
