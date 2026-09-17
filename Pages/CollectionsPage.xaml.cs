using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using LiveDrive.Helpers;
using LiveDrive.Models;
using LiveDrive.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LiveDrive.Pages
{
    public sealed partial class CollectionsPage : Page
    {
        private readonly AppServices _services;
        public ObservableCollection<PhotoCollection> Collections { get; } = new ObservableCollection<PhotoCollection>();

        public CollectionsPage()
        {
            InitializeComponent();
            _services = ((App)Application.Current).Services;
            Loaded += async (sender, args) => await LoadCollectionsAsync();
        }

        private async Task LoadCollectionsAsync()
        {
            try
            {
                var snapshot = await _services.PhotoIndex.LoadAsync();
                Collections.Clear();
                var today = DateTimeOffset.Now;
                var memories = snapshot.Items.Where(item => IsImageFile(item) && IsOnThisDay(item, today)).ToList();
                if (memories.Count > 0)
                {
                    Collections.Add(new PhotoCollection
                    {
                        Title = "On this day",
                        IsOnThisDay = true,
                        Count = memories.Count
                    });
                }
                var groups = snapshot.Items
                    .Where(IsImageFile)
                    .GroupBy(GetCollectionMonth)
                    .OrderByDescending(group => group.Key);
                foreach (var group in groups)
                {
                    Collections.Add(new PhotoCollection
                    {
                        Title = group.Key == DateTimeOffset.MinValue ? "Other photos" : group.Key.ToString("MMMM yyyy"),
                        MonthKey = group.Key == DateTimeOffset.MinValue ? string.Empty : group.Key.ToString("yyyy-MM"),
                        Count = group.Count()
                    });
                }
                EmptyPanel.Visibility = Collections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception exception)
            {
                await PageFeedback.ShowErrorAsync(exception);
            }
        }

        private static DateTimeOffset GetCollectionMonth(DriveItem item)
        {
            DateTimeOffset date;
            if (!DateTimeOffset.TryParse(item.DateTaken, out date) &&
                !DateTimeOffset.TryParse(item.LastModified, out date))
            {
                return DateTimeOffset.MinValue;
            }
            return new DateTimeOffset(date.Year, date.Month, 1, 0, 0, 0, date.Offset);
        }

        private static bool IsOnThisDay(DriveItem item, DateTimeOffset today)
        {
            DateTimeOffset date;
            if (!DateTimeOffset.TryParse(item.DateTaken, out date) &&
                !DateTimeOffset.TryParse(item.LastModified, out date))
            {
                return false;
            }
            return date.Year < today.Year && date.Month == today.Month && date.Day == today.Day;
        }

        private static bool IsImageFile(DriveItem item)
        {
            return item.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        private void CollectionsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            Frame.Navigate(typeof(PhotosPage), (PhotoCollection)e.ClickedItem);
        }
    }
}
