using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveDrive.Models;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;

namespace LiveDrive.Services
{
    public sealed class LiveTileService
    {
        public const string Off = "Off";
        public const string StorageUsage = "StorageUsage";
        public const string RecentFile = "RecentFile";
        public const string SelectedPhoto = "SelectedPhoto";
        public const string SelectedAlbum = "SelectedAlbum";
        private const string ModeSettingName = "LiveTileMode";
        private const string SelectedItemIdSettingName = "LiveTileSelectedItemId";
        private const string SelectedNameSettingName = "LiveTileSelectedName";
        private const string AlbumTileOffsetSettingName = "LiveTileAlbumOffset";
        private const int MaximumAlbumTileImages = 5;
        private readonly IGraphClient _graph;
        private readonly PhotoIndexStore _photoIndex;
        private readonly PhotoAlbumService _albums;

        public LiveTileService(IGraphClient graph, PhotoIndexStore photoIndex, PhotoAlbumService albums)
        {
            _graph = graph;
            _photoIndex = photoIndex;
            _albums = albums;
        }

        public string Mode
        {
            get
            {
                object value;
                return ApplicationData.Current.LocalSettings.Values.TryGetValue(ModeSettingName, out value)
                    ? value as string ?? Off
                    : Off;
            }
        }

        public string SelectedItemId => GetSetting(SelectedItemIdSettingName);
        public string SelectedName => GetSetting(SelectedNameSettingName);

        public void SetMode(string mode)
        {
            ApplicationData.Current.LocalSettings.Values[ModeSettingName] = mode ?? Off;
            ApplicationData.Current.LocalSettings.Values.Remove(AlbumTileOffsetSettingName);
        }

        public void SetSelection(string itemId, string name)
        {
            ApplicationData.Current.LocalSettings.Values[SelectedItemIdSettingName] = itemId;
            ApplicationData.Current.LocalSettings.Values[SelectedNameSettingName] = name;
            ApplicationData.Current.LocalSettings.Values.Remove(AlbumTileOffsetSettingName);
        }

        public void Clear()
        {
            SetMode(Off);
            ApplicationData.Current.LocalSettings.Values.Remove(SelectedItemIdSettingName);
            ApplicationData.Current.LocalSettings.Values.Remove(SelectedNameSettingName);
            ApplicationData.Current.LocalSettings.Values.Remove(AlbumTileOffsetSettingName);
            TileUpdateManager.CreateTileUpdaterForApplication().Clear();
        }

        public async Task UpdateAsync()
        {
            if (Mode == Off)
            {
                var updater = TileUpdateManager.CreateTileUpdaterForApplication();
                updater.Clear();
                updater.EnableNotificationQueue(false);
                return;
            }

            string title;
            string detail;
            string imageUri = null;
            if (Mode == StorageUsage)
            {
                var quota = await _graph.GetQuotaAsync();
                title = "OneDrive storage";
                detail = FormatSize(quota.Used) + " of " + FormatSize(quota.Total) + " used";
            }
            else if (Mode == RecentFile)
            {
                var recent = (await _graph.GetRecentAsync()).FirstOrDefault();
                title = "Recent file";
                detail = recent == null ? "No recent files found." : recent.Name;
            }
            else
            {
                var snapshot = await _photoIndex.LoadAsync();
                var selectedId = SelectedItemId;
                if (Mode == SelectedAlbum)
                {
                    var album = (await _albums.GetAlbumsAsync()).FirstOrDefault(candidate => candidate.Id == selectedId);
                    title = "LiveDrive album";
                    detail = album == null ? "Choose an album in settings." : album.Name;
                    if (album != null)
                    {
                        await UpdateAlbumTileAsync(album, snapshot.Items);
                        return;
                    }
                }
                else
                {
                    var photo = snapshot.Items.FirstOrDefault(item => item.Id == selectedId);
                    title = "LiveDrive photo";
                    detail = photo == null ? "Choose a photo in settings." : photo.Name;
                    imageUri = photo == null ? null : await GetTileImageAsync(photo);
                }
            }

            var standardUpdater = TileUpdateManager.CreateTileUpdaterForApplication();
            standardUpdater.Clear();
            standardUpdater.EnableNotificationQueue(false);
            UpdateTile(title, detail, imageUri, standardUpdater);
        }

        private async Task UpdateAlbumTileAsync(PhotoAlbum album, IReadOnlyList<DriveItem> photos)
        {
            var albumPhotos = album.ItemIds
                .Select(id => photos.FirstOrDefault(photo => photo.Id == id))
                .Where(photo => photo != null)
                .ToList();
            if (albumPhotos.Count == 0)
            {
                UpdateTile("LiveDrive album", album.Name, null);
                return;
            }

            var offset = GetAlbumTileOffset() % albumPhotos.Count;
            var updater = TileUpdateManager.CreateTileUpdaterForApplication();
            updater.Clear();
            updater.EnableNotificationQueue(true);
            var queuedImages = 0;
            for (var index = 0; index < albumPhotos.Count && queuedImages < MaximumAlbumTileImages; index++)
            {
                var photo = albumPhotos[(offset + index) % albumPhotos.Count];
                var imageUri = await GetTileImageAsync(photo);
                if (!string.IsNullOrEmpty(imageUri))
                {
                    UpdateTile("LiveDrive album", album.Name, imageUri, updater);
                    queuedImages++;
                }
            }

            if (queuedImages == 0)
            {
                UpdateTile("LiveDrive album", album.Name, null, updater);
            }
            ApplicationData.Current.LocalSettings.Values[AlbumTileOffsetSettingName] =
                (offset + MaximumAlbumTileImages) % albumPhotos.Count;
        }

        private static int GetAlbumTileOffset()
        {
            object value;
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(AlbumTileOffsetSettingName, out value) &&
                   value is int ? (int)value : 0;
        }

        private static void UpdateTile(string title, string detail, string imageUri,
            TileUpdater updater = null)
        {
            var xml = new XmlDocument();
            var image = string.IsNullOrEmpty(imageUri)
                ? string.Empty
                : "<image placement=\"background\" src=\"" + Escape(imageUri) + "\"/>";
            var text = string.IsNullOrEmpty(imageUri)
                ? "<text hint-style=\"caption\">" + Escape(title) +
                  "</text><text hint-style=\"captionsubtle\">" + Escape(detail) + "</text>"
                : string.Empty;
            xml.LoadXml("<tile><visual displayName=\"LiveDrive\">" +
                "<binding template=\"TileMedium\">" + image + text + "</binding>" +
                "<binding template=\"TileWide\">" + image + text + "</binding>" +
                "</visual></tile>");
            (updater ?? TileUpdateManager.CreateTileUpdaterForApplication()).Update(new TileNotification(xml));
        }

        private static string GetSetting(string name)
        {
            object value;
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(name, out value) ? value as string : null;
        }

        private async Task<string> GetTileImageAsync(DriveItem photo)
        {
            await _photoIndex.CacheThumbnailAsync(photo, _graph, true, CancellationToken.None);
            return photo.ThumbnailUrl;
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private static string FormatSize(long size)
        {
            const long gigabyte = 1024L * 1024 * 1024;
            const long megabyte = 1024L * 1024;
            return size >= gigabyte ? string.Format("{0:0.#} GB", (double)size / gigabyte) :
                size >= megabyte ? string.Format("{0:0.#} MB", (double)size / megabyte) :
                string.Format("{0:N0} bytes", size);
        }
    }
}
