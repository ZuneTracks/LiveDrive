using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveDrive.Helpers;
using LiveDrive.Models;
using LiveDrive.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace LiveDrive.Pages
{
    public sealed partial class PhotosPage : Page
    {
        private readonly AppServices _services;
        private const string ThumbnailSizeSettingName = "PhotoThumbnailSize";
        private const string PhotoSortSettingName = "PhotoSort";
        private const int InitialPhotoBatchSize = 150;
        private const int PhotoBatchSize = 100;
        private bool _isLoading;
        private bool _isSelecting;
        private bool _isDeletingPhotos;
        private bool _thumbnailErrorShown;
        private int _thumbnailCacheCount;
        private string _thumbnailSize;
        private string _photoSort;
        private List<DriveItem> _allPhotos = new List<DriveItem>();
        private int _displayedPhotoCount;
        private bool _isAppendingPhotoBatch;
        private bool _hasFullPhotoIndex;
        private ScrollViewer _photoScrollViewer;
        private PhotoAlbum _album;
        private PhotoCollection _collection;
        private readonly DataTransferManager _shareManager;
        private StorageFile _shareFile;
        private string _shareTitle;
        private readonly HashSet<string> _queuedThumbnailKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _unavailableThumbnailKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _thumbnailCacheSlots = new SemaphoreSlim(2);
        private CancellationTokenSource _thumbnailCacheCancellation = new CancellationTokenSource();
        public ObservableCollection<DriveItem> Photos { get; } = new ObservableCollection<DriveItem>();

        public PhotosPage()
        {
            InitializeComponent();
            _services = ((App)Application.Current).Services;
            _shareManager = DataTransferManager.GetForCurrentView();
            _shareManager.DataRequested += ShareManager_DataRequested;
            ApplyThumbnailSize(ApplicationData.Current.LocalSettings.Values[ThumbnailSizeSettingName] as string);
            ApplyPhotoSort(ApplicationData.Current.LocalSettings.Values[PhotoSortSettingName] as string);
            PhotosGrid.Loaded += (sender, args) =>
            {
                ApplyThumbnailSize(_thumbnailSize);
                AttachPhotoScrollViewer();
            };
            Loaded += async (sender, args) => await LoadPhotosAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadPhotosAsync();
        }

        private async void SourceFoldersButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
            {
                return;
            }

            try
            {
                var sources = await PromptForSourceFoldersAsync();
                if (sources == null)
                {
                    return;
                }

                await _services.PhotoIndex.ClearAsync();
                await _services.PhotoIndex.SaveAsync(new List<DriveItem>(), sources);
                RenderPhotos(new List<DriveItem>());
                await LoadPhotosAsync();
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private void ThumbnailSize_Click(object sender, RoutedEventArgs e)
        {
            ApplyThumbnailSize((sender as FrameworkElement).Tag as string);
        }

        private async void PhotoSort_Click(object sender, RoutedEventArgs e)
        {
            ApplyPhotoSort((sender as FrameworkElement).Tag as string);
            RenderPhotos(_allPhotos);
            if (_album == null && _collection == null)
            {
                await _services.PhotoIndex.SavePreviewAsync(Photos);
            }
        }

        private void ApplyThumbnailSize(string size)
        {
            var normalizedSize = size == "Small" || size == "Large" ? size : "Medium";
            _thumbnailSize = normalizedSize;
            var panel = PhotosGrid.ItemsPanelRoot as ItemsWrapGrid;
            if (normalizedSize == "Small")
            {
                SetThumbnailDimensions(panel, 96, 126);
            }
            else if (normalizedSize == "Large")
            {
                SetThumbnailDimensions(panel, 216, 246);
            }
            else
            {
                SetThumbnailDimensions(panel, 144, 174);
            }

            ApplicationData.Current.LocalSettings.Values[ThumbnailSizeSettingName] = normalizedSize;
            ViewButton.Label = normalizedSize;
            QueueVisibleThumbnailCaching();
        }

        private void ApplyPhotoSort(string sort)
        {
            _photoSort = sort == "DateTakenOldest" || sort == "ModifiedNewest" ||
                         sort == "NameAscending" || sort == "NameDescending"
                ? sort
                : "DateTakenNewest";
            ApplicationData.Current.LocalSettings.Values[PhotoSortSettingName] = _photoSort;
            SortButton.Label = GetPhotoSortLabel(_photoSort);
        }

        private static void SetThumbnailDimensions(ItemsWrapGrid panel, double width, double height)
        {
            if (panel == null)
            {
                return;
            }
            panel.ItemWidth = width;
            panel.ItemHeight = height;
        }

        private void AttachPhotoScrollViewer()
        {
            if (_photoScrollViewer != null)
            {
                return;
            }

            _photoScrollViewer = FindVisualChild<ScrollViewer>(PhotosGrid);
            if (_photoScrollViewer != null)
            {
                _photoScrollViewer.ViewChanged += PhotosGrid_ViewChanged;
            }
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            var childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (var index = 0; index < childCount; index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                var matchingChild = child as T;
                if (matchingChild != null)
                {
                    return matchingChild;
                }

                var descendant = FindVisualChild<T>(child);
                if (descendant != null)
                {
                    return descendant;
                }
            }
            return null;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (_thumbnailCacheCancellation.IsCancellationRequested)
            {
                _thumbnailCacheCancellation = new CancellationTokenSource();
            }
            _album = e.Parameter as PhotoAlbum;
            _collection = e.Parameter as PhotoCollection;
            PageTitle.Text = _album != null ? _album.Name : _collection != null ? _collection.Title : "Photos";
            SetEmptyMessage();
            base.OnNavigatedTo(e);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _thumbnailCacheCancellation.Cancel();
            _shareManager.DataRequested -= ShareManager_DataRequested;
            base.OnNavigatedFrom(e);
        }

        private async Task LoadPhotosAsync()
        {
            if (_isLoading)
            {
                return;
            }

            _isLoading = true;
            LoadingPanel.Visibility = Visibility.Visible;
            SyncStatusPanel.Visibility = Visibility.Visible;
            EmptyPanel.Visibility = Visibility.Collapsed;
            try
            {
                var hasPreview = false;
                if (_album == null && _collection == null)
                {
                    var preview = await _services.PhotoIndex.LoadPreviewAsync();
                    if (preview.Count > 0)
                    {
                        RenderPhotos(preview);
                        LoadingPanel.Visibility = Visibility.Collapsed;
                        SetSyncStatus("Showing cached photos. Loading your library…");
                        hasPreview = true;
                    }
                }

                var snapshot = await Task.Run(async () => await _services.PhotoIndex.LoadAsync());
                _hasFullPhotoIndex = true;
                if (snapshot.SourceFolders.Count == 0)
                {
                    var sources = await PromptForSourceFoldersAsync();
                    if (sources == null)
                    {
                        ShowFolderSelectionRequired();
                        return;
                    }

                    await _services.PhotoIndex.ClearAsync();
                    snapshot = new PhotoIndexSnapshot
                    {
                        Items = new List<DriveItem>(),
                        SourceFolders = sources
                    };
                    await _services.PhotoIndex.SaveAsync(snapshot.Items, snapshot.SourceFolders);
                }

                if (_album != null || _collection != null)
                {
                    RenderPhotos(FilterPhotos(snapshot.Items));
                    SetSyncStatus("Showing cached photos.");
                    SetEmptyMessage();
                    EmptyPanel.Visibility = Photos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    return;
                }
                RenderPhotos(snapshot.Items);
                LoadingPanel.Visibility = Photos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                SetSyncStatus(Photos.Count == 0
                    ? "Finding photos across your drive…"
                    : "Showing cached photos. Checking for updates…");
                if (!hasPreview)
                {
                    await _services.PhotoIndex.SavePreviewAsync(Photos);
                }

                var indexedPhotos = snapshot.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
                var hasVisiblePhotos = Photos.Count > 0;
                var lastPublished = DateTimeOffset.UtcNow;

                foreach (var source in snapshot.SourceFolders)
                {
                    var nextLink = source.DeltaLink;
                    string deltaLink = null;
                    do
                    {
                        var page = await _services.Graph.GetPhotoDeltaPageAsync(nextLink, source.Id);
                        foreach (var item in page.Items)
                        {
                            if (item.IsDeleted)
                            {
                                if (indexedPhotos.Remove(item.Id))
                                {
                                    await _services.PhotoIndex.RemoveThumbnailAsync(item.Id);
                                }
                            }
                            else if (IsMediaFile(item))
                            {
                                if (indexedPhotos.ContainsKey(item.Id))
                                {
                                    await _services.PhotoIndex.RemoveThumbnailAsync(item.Id);
                                }
                                indexedPhotos[item.Id] = item;
                                _unavailableThumbnailKeys.Remove(item.Id + "|medium");
                                _unavailableThumbnailKeys.Remove(item.Id + "|large");
                            }
                            else
                            {
                                if (indexedPhotos.Remove(item.Id))
                                {
                                    await _services.PhotoIndex.RemoveThumbnailAsync(item.Id);
                                }
                            }
                        }

                        if (!hasVisiblePhotos && indexedPhotos.Count > 0)
                        {
                            RenderPhotos(indexedPhotos.Values);
                            LoadingPanel.Visibility = Visibility.Collapsed;
                            hasVisiblePhotos = true;
                        }

                        nextLink = page.NextLink;
                        if (DateTimeOffset.UtcNow - lastPublished >= TimeSpan.FromSeconds(3))
                        {
                            SetSyncStatus("Found " + indexedPhotos.Count + " photos. Scanning " + source.Name + "…");
                            lastPublished = DateTimeOffset.UtcNow;
                        }

                        if (!string.IsNullOrEmpty(page.DeltaLink))
                        {
                            deltaLink = page.DeltaLink;
                        }
                    }
                    while (!string.IsNullOrEmpty(nextLink));

                    if (!string.IsNullOrEmpty(deltaLink))
                    {
                        source.DeltaLink = deltaLink;
                    }
                }

                RenderPhotos(indexedPhotos.Values);
                LoadingPanel.Visibility = _allPhotos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                await _services.PhotoIndex.SaveAsync(_allPhotos, snapshot.SourceFolders);
                EmptyPanel.Visibility = _allPhotos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
            finally
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                SyncStatusPanel.Visibility = Visibility.Collapsed;
                _isLoading = false;
            }
        }

        private void SetSyncStatus(string text)
        {
            LoadingText.Text = text;
            SyncStatusText.Text = text;
        }

        private async Task<List<PhotoSourceFolder>> PromptForSourceFoldersAsync()
        {
            var selected = new Dictionary<string, PhotoSourceFolder>(StringComparer.Ordinal);
            var history = new List<PhotoSourceFolder>();
            var folderList = new ListView { SelectionMode = ListViewSelectionMode.None, Height = 320 };
            var folderPath = new TextBlock { Text = "OneDrive", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var backButton = new Button { Content = "Back", IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
            var navigation = new StackPanel { Orientation = Orientation.Horizontal };
            navigation.Children.Add(backButton);
            navigation.Children.Add(folderPath);
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = "Select each folder whose photos and videos LiveDrive should include. Open a folder to choose a nested photo folder.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            content.Children.Add(navigation);
            content.Children.Add(folderList);
            var dialog = new ContentDialog
            {
                Title = "Choose photo folders",
                Content = content,
                PrimaryButtonText = "Use selected folders",
                CloseButtonText = "Cancel",
                IsPrimaryButtonEnabled = false
            };

            Func<string, string, Task> loadFolder = null;
            loadFolder = async (folderId, folderName) =>
            {
                folderList.Items.Clear();
                folderPath.Text = history.Count == 0
                    ? "OneDrive"
                    : string.Join(" / ", history.Select(folder => folder.Name));
                backButton.IsEnabled = history.Count > 0;
                var folders = (await _services.Graph.GetChildrenAsync(folderId))
                    .Where(item => item.IsFolder)
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (folders.Count == 0)
                {
                    folderList.Items.Add(new TextBlock
                    {
                        Text = "No subfolders here.",
                        Opacity = 0.65,
                        Margin = new Thickness(12)
                    });
                    return;
                }

                foreach (var folder in folders)
                {
                    var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    var selector = new CheckBox
                    {
                        IsChecked = selected.ContainsKey(folder.Id),
                        Tag = folder,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8)
                    };
                    selector.Checked += (sender, args) =>
                    {
                        selected[folder.Id] = new PhotoSourceFolder { Id = folder.Id, Name = folder.Name };
                        dialog.IsPrimaryButtonEnabled = true;
                    };
                    selector.Unchecked += (sender, args) =>
                    {
                        selected.Remove(folder.Id);
                        dialog.IsPrimaryButtonEnabled = selected.Count > 0;
                    };
                    var openButton = new Button
                    {
                        Content = folder.Name,
                        Tag = folder,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left
                    };
                    openButton.Click += async (sender, args) =>
                    {
                        history.Add(new PhotoSourceFolder { Id = folder.Id, Name = folder.Name });
                        await loadFolder(folder.Id, folder.Name);
                    };
                    Grid.SetColumn(selector, 0);
                    Grid.SetColumn(openButton, 1);
                    row.Children.Add(selector);
                    row.Children.Add(openButton);
                    folderList.Items.Add(row);
                }
            };

            backButton.Click += async (sender, args) =>
            {
                if (history.Count == 0)
                {
                    return;
                }
                history.RemoveAt(history.Count - 1);
                var parent = history.LastOrDefault();
                await loadFolder(parent == null ? string.Empty : parent.Id, parent == null ? "OneDrive" : parent.Name);
            };

            await loadFolder(string.Empty, "OneDrive");
            return await dialog.ShowAsync() == ContentDialogResult.Primary && selected.Count > 0
                ? selected.Values.OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : null;
        }

        private void ShowFolderSelectionRequired()
        {
            Photos.Clear();
            EmptyTitle.Text = "Choose photo folders to begin.";
            EmptyDescription.Text = "LiveDrive will only scan folders you select.";
            EmptyPanel.Visibility = Visibility.Visible;
        }

        private async void PhotosGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }

            await CacheVisibleThumbnailAsync(args.Item as DriveItem);
        }

        private void QueueVisibleThumbnailCaching()
        {
            if (!_hasFullPhotoIndex || _thumbnailSize != "Large")
            {
                return;
            }

            for (var index = 0; index < PhotosGrid.Items.Count; index++)
            {
                var container = PhotosGrid.ContainerFromIndex(index) as FrameworkElement;
                var item = container == null ? null : container.DataContext as DriveItem;
                if (item != null)
                {
                    _ = CacheVisibleThumbnailAsync(item);
                }
            }
        }

        private async Task CacheVisibleThumbnailAsync(DriveItem item)
        {
            if (item == null)
            {
                return;
            }

            var preferLarge = _hasFullPhotoIndex && _thumbnailSize == "Large";
            var cacheKey = item.Id + (preferLarge ? "|large" : "|medium");
            if ((!preferLarge && !string.IsNullOrEmpty(item.ThumbnailUrl)) ||
                (preferLarge && IsLargeThumbnail(item.ThumbnailUrl)) ||
                _unavailableThumbnailKeys.Contains(cacheKey) || !_queuedThumbnailKeys.Add(cacheKey))
            {
                return;
            }

            var cancellationToken = _thumbnailCacheCancellation.Token;
            try
            {
                await _thumbnailCacheSlots.WaitAsync(cancellationToken);
                try
                {
                    await _services.PhotoIndex.CacheThumbnailAsync(item, _services.Graph, preferLarge, cancellationToken);
                    if (string.IsNullOrEmpty(item.ThumbnailUrl))
                    {
                        _unavailableThumbnailKeys.Add(cacheKey);
                    }
                    if (Interlocked.Increment(ref _thumbnailCacheCount) % 12 == 0)
                    {
                        await _services.PhotoIndex.TrimThumbnailsAsync();
                    }
                }
                finally
                {
                    _thumbnailCacheSlots.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (!_thumbnailErrorShown)
                {
                    _thumbnailErrorShown = true;
                    await PageFeedback.ShowErrorAsync(exception);
                }
            }
            finally
            {
                _queuedThumbnailKeys.Remove(cacheKey);
            }
        }

        private static bool IsLargeThumbnail(string thumbnailUrl)
        {
            return !string.IsNullOrEmpty(thumbnailUrl) &&
                   thumbnailUrl.IndexOf(".large.jpg", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void PhotoTile_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started)
            {
                return;
            }

            e.Handled = true;
            var tile = (FrameworkElement)sender;
            var item = tile.Tag as DriveItem;
            if (item == null)
            {
                return;
            }

            var menu = new MenuFlyout();
            var view = new MenuFlyoutItem { Text = "View" };
            view.Click += async (menuSender, menuArgs) => await ViewPhotoAsync(item);
            var addToAlbum = new MenuFlyoutItem { Text = "Add to album" };
            addToAlbum.Click += async (menuSender, menuArgs) => await AddPhotosToAlbumAsync(new[] { item });
            var saveAs = new MenuFlyoutItem { Text = "Save as" };
            saveAs.Click += async (menuSender, menuArgs) => await SavePhotoAsAsync(item);
            var share = new MenuFlyoutItem { Text = "Share" };
            share.Click += async (menuSender, menuArgs) => await SharePhotoAsync(item);
            var copy = new MenuFlyoutItem { Text = "Copy" };
            copy.Click += async (menuSender, menuArgs) => await CopyOrMovePhotoAsync(item, false);
            var move = new MenuFlyoutItem { Text = "Move" };
            move.Click += async (menuSender, menuArgs) => await CopyOrMovePhotoAsync(item, true);
            var delete = new MenuFlyoutItem { Text = "Delete" };
            delete.Click += async (menuSender, menuArgs) => await DeletePhotoAsync(item);
            menu.Items.Add(view);
            menu.Items.Add(addToAlbum);
            menu.Items.Add(saveAs);
            menu.Items.Add(share);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(copy);
            menu.Items.Add(move);
            menu.Items.Add(delete);
            menu.ShowAt(tile);
        }

        private async Task ViewPhotoAsync(DriveItem item)
        {
            try
            {
                var file = await CreateTemporaryPhotoFileAsync(item.Name);
                await _services.Graph.DownloadToAsync(item, file);
                if (!await Launcher.LaunchFileAsync(file))
                {
                    await PageFeedback.ShowInfoAsync("No app on this device can open this photo.");
                }
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private async Task SavePhotoAsAsync(DriveItem item)
        {
            try
            {
                var picker = new FileSavePicker { SuggestedFileName = item.Name };
                picker.FileTypeChoices.Add("Photo or video", new[] { GetPhotoExtension(item.Name) });
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

        private async Task SharePhotoAsync(DriveItem item)
        {
            try
            {
                _shareFile = await CreateTemporaryPhotoFileAsync(item.Name);
                await _services.Graph.DownloadToAsync(item, _shareFile);
                _shareTitle = item.Name;
                DataTransferManager.ShowShareUI();
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private async Task CopyOrMovePhotoAsync(DriveItem item, bool move)
        {
            try
            {
                var destination = await OneDriveFolderPicker.PickAsync(
                    _services.Graph,
                    move ? "Choose move destination" : "Choose copy destination",
                    "Open folders to choose where this photo should be placed.",
                    move ? "Move here" : "Copy here");
                if (destination == null)
                {
                    return;
                }

                var destinationId = destination.Id;
                if (string.IsNullOrEmpty(destinationId))
                {
                    destinationId = (await _services.Graph.GetRootFolderAsync()).Id;
                }
                if (destinationId == item.Id)
                {
                    await PageFeedback.ShowInfoAsync("Choose a different destination folder.");
                    return;
                }

                if (move)
                {
                    await _services.Graph.MoveAsync(item, destinationId);
                    Photos.Remove(item);
                }
                else
                {
                    await _services.Graph.CopyAsync(item, destinationId);
                }
                await PageFeedback.ShowInfoAsync((move ? "Moved " : "Copied ") + item.Name + " to " + destination.Name + ".");
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private async Task DeletePhotoAsync(DriveItem item)
        {
            await DeletePhotosAsync(new[] { item });
        }

        private async Task DeletePhotosAsync(IReadOnlyList<DriveItem> selectedPhotos)
        {
            var photosToDelete = selectedPhotos
                .Where(item => item != null && !string.IsNullOrEmpty(item.Id))
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (photosToDelete.Count == 0 || _isDeletingPhotos)
            {
                return;
            }

            var count = photosToDelete.Count;
            var dialog = new ContentDialog
            {
                Title = count == 1 ? "Delete photo?" : "Delete photos?",
                Content = count == 1
                    ? "Delete " + photosToDelete[0].Name + " from OneDrive? It can be restored from the OneDrive recycle bin."
                    : "Delete " + count + " photos from OneDrive? They can be restored from the OneDrive recycle bin.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel"
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                _isDeletingPhotos = true;
                DeleteSelectedButton.IsEnabled = false;
                foreach (var photo in photosToDelete)
                {
                    await _services.Graph.DeleteAsync(photo);
                    Photos.Remove(photo);
                    _allPhotos.RemoveAll(item => item.Id == photo.Id);
                    await _services.PhotoIndex.RemoveItemAsync(photo.Id);
                }
                ClearPhotoSelection();
                await PageFeedback.ShowInfoAsync(count == 1
                    ? "Deleted " + photosToDelete[0].Name + "."
                    : "Deleted " + count + " photos.");
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
            finally
            {
                _isDeletingPhotos = false;
                DeleteSelectedButton.IsEnabled = _isSelecting && PhotosGrid.SelectedItems.Count > 0;
            }
        }

        private void ShareManager_DataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            if (_shareFile == null)
            {
                args.Request.FailWithDisplayText("The selected photo is not ready to share.");
                return;
            }

            args.Request.Data.Properties.Title = _shareTitle ?? "LiveDrive photo";
            args.Request.Data.SetStorageItems(new[] { _shareFile });
        }

        private void SelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSelecting)
            {
                ExitSelectionMode();
                return;
            }

            _isSelecting = true;
            PhotosGrid.SelectionMode = ListViewSelectionMode.Multiple;
            PhotosGrid.IsItemClickEnabled = false;
            SelectionButton.Label = "Done";
            AddToAlbumButton.IsEnabled = false;
            DeleteSelectedButton.IsEnabled = false;
        }

        private void PhotosGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var hasSelection = _isSelecting && PhotosGrid.SelectedItems.Count > 0;
            AddToAlbumButton.IsEnabled = hasSelection;
            DeleteSelectedButton.IsEnabled = hasSelection && !_isDeletingPhotos;
        }

        private async void AddToAlbumButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedPhotos = PhotosGrid.SelectedItems.Cast<DriveItem>().ToList();
            await AddPhotosToAlbumAsync(selectedPhotos);
        }

        private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedPhotos = PhotosGrid.SelectedItems.Cast<DriveItem>().ToList();
            await DeletePhotosAsync(selectedPhotos);
        }

        private async Task AddPhotosToAlbumAsync(IReadOnlyList<DriveItem> selectedPhotos)
        {
            var photosToAdd = selectedPhotos
                .Where(item => item != null && !string.IsNullOrEmpty(item.Id))
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (photosToAdd.Count == 0)
            {
                return;
            }

            try
            {
                var albums = await _services.Albums.GetAlbumsAsync();
                if (albums.Count == 0)
                {
                    await PageFeedback.ShowInfoAsync("Create an album first, then select photos to add.");
                    return;
                }

                var picker = new ListBox
                {
                    ItemsSource = albums,
                    DisplayMemberPath = "Name",
                    SelectionMode = SelectionMode.Single
                };
                var dialog = new ContentDialog
                {
                    Title = "Add to album",
                    Content = picker,
                    PrimaryButtonText = "Add",
                    CloseButtonText = "Cancel",
                    IsPrimaryButtonEnabled = false
                };
                picker.SelectionChanged += (sender, args) =>
                    dialog.IsPrimaryButtonEnabled = picker.SelectedItem is PhotoAlbum;
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                var album = picker.SelectedItem as PhotoAlbum;
                if (album == null)
                {
                    await PageFeedback.ShowInfoAsync("Select an album before adding photos.");
                    return;
                }

                await _services.PhotoIndex.EnsureItemsAsync(photosToAdd);
                await _services.Albums.AddPhotosAsync(album.Id, photosToAdd.Select(item => item.Id));
                await PageFeedback.ShowInfoAsync("Added " + photosToAdd.Count + " photos to " + album.Name + ".");
                ClearPhotoSelection();
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private void ExitSelectionMode()
        {
            ClearPhotoSelection();
            _isSelecting = false;
            PhotosGrid.IsItemClickEnabled = true;
            PhotosGrid.SelectionMode = ListViewSelectionMode.None;
            SelectionButton.Label = "Select";
            AddToAlbumButton.IsEnabled = false;
            DeleteSelectedButton.IsEnabled = false;
        }

        private void ClearPhotoSelection()
        {
            if (PhotosGrid.SelectionMode != ListViewSelectionMode.None && PhotosGrid.SelectedItems.Count > 0)
            {
                PhotosGrid.SelectedItems.Clear();
            }
        }

        private void RenderPhotos(IEnumerable<DriveItem> items)
        {
            Photos.Clear();
            _allPhotos = SortPhotos(items).ToList();
            _displayedPhotoCount = 0;
            AppendPhotoBatch(InitialPhotoBatchSize);
        }

        private void PhotosGrid_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer == null || _displayedPhotoCount >= _allPhotos.Count ||
                scrollViewer.VerticalOffset + scrollViewer.ViewportHeight < scrollViewer.ExtentHeight - 200)
            {
                return;
            }
            AppendPhotoBatch(PhotoBatchSize);
        }

        private void AppendPhotoBatch(int batchSize)
        {
            if (_isAppendingPhotoBatch || _displayedPhotoCount >= _allPhotos.Count)
            {
                return;
            }

            _isAppendingPhotoBatch = true;
            var endIndex = Math.Min(_displayedPhotoCount + batchSize, _allPhotos.Count);
            for (var index = _displayedPhotoCount; index < endIndex; index++)
            {
                Photos.Add(_allPhotos[index]);
            }
            _displayedPhotoCount = endIndex;
            _isAppendingPhotoBatch = false;
        }

        private IEnumerable<DriveItem> SortPhotos(IEnumerable<DriveItem> items)
        {
            if (_photoSort == "DateTakenOldest")
            {
                return items.OrderBy(GetPhotoDate);
            }
            if (_photoSort == "ModifiedNewest")
            {
                return items.OrderByDescending(GetLastModifiedDate);
            }
            if (_photoSort == "NameAscending")
            {
                return items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
            }
            if (_photoSort == "NameDescending")
            {
                return items.OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase);
            }
            return items.OrderByDescending(GetPhotoDate);
        }

        private IEnumerable<DriveItem> FilterPhotos(IEnumerable<DriveItem> items)
        {
            if (_album != null)
            {
                return items.Where(item => _album.ItemIds.Contains(item.Id));
            }

            if (_collection.IsOnThisDay)
            {
                var today = DateTimeOffset.Now;
                return items.Where(item => IsOnThisDay(item, today));
            }

            return items.Where(item => GetMonthKey(item) == _collection.MonthKey);
        }

        private void SetEmptyMessage()
        {
            if (_album != null)
            {
                EmptyTitle.Text = "This album is empty.";
                EmptyDescription.Text = "Add photos from Photos to see them here.";
                return;
            }

            EmptyTitle.Text = "No photos found.";
            EmptyDescription.Text = "Photos in your selected OneDrive folders will appear here.";
        }

        private static string GetMonthKey(DriveItem item)
        {
            DateTimeOffset date;
            if (!DateTimeOffset.TryParse(item.DateTaken, out date) &&
                !DateTimeOffset.TryParse(item.LastModified, out date))
            {
                return string.Empty;
            }
            return date.ToString("yyyy-MM");
        }

        private static DateTimeOffset GetPhotoDate(DriveItem item)
        {
            DateTimeOffset date;
            if (DateTimeOffset.TryParse(item.DateTaken, out date) ||
                DateTimeOffset.TryParse(item.LastModified, out date))
            {
                return date;
            }
            return DateTimeOffset.MinValue;
        }

        private static bool IsOnThisDay(DriveItem item, DateTimeOffset today)
        {
            var date = GetPhotoDate(item);
            return date != DateTimeOffset.MinValue && date.Year < today.Year &&
                   date.Month == today.Month && date.Day == today.Day;
        }

        private static DateTimeOffset GetLastModifiedDate(DriveItem item)
        {
            DateTimeOffset date;
            return DateTimeOffset.TryParse(item.LastModified, out date) ? date : DateTimeOffset.MinValue;
        }

        private static string GetPhotoSortLabel(string sort)
        {
            if (sort == "DateTakenOldest")
            {
                return "Date: oldest";
            }
            if (sort == "ModifiedNewest")
            {
                return "Modified";
            }
            if (sort == "NameAscending")
            {
                return "Name: A-Z";
            }
            if (sort == "NameDescending")
            {
                return "Name: Z-A";
            }
            return "Date: newest";
        }

        private static bool IsMediaFile(DriveItem item)
        {
            return item.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                   item.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        }

        private async void PhotosGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            await ViewPhotoAsync((DriveItem)e.ClickedItem);
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
            var extension = index >= 0 ? name.Substring(index) : ".dat";
            if (extension.Length > 12)
            {
                return ".dat";
            }

            for (var character = 1; character < extension.Length; character++)
            {
                if (!char.IsLetterOrDigit(extension[character]))
                {
                    return ".dat";
                }
            }
            return extension.Length > 1 ? extension : ".dat";
        }
    }
}
