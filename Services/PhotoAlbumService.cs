using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiveDrive.Models;
using Windows.Data.Json;

namespace LiveDrive.Services
{
    public sealed class PhotoAlbumService
    {
        private const string AlbumFileName = "livedrive-albums.json";
        private readonly IGraphClient _graph;

        public PhotoAlbumService(IGraphClient graph)
        {
            _graph = graph;
        }

        public async Task<IReadOnlyList<PhotoAlbum>> GetAlbumsAsync()
        {
            var content = await _graph.ReadAppFolderFileAsync(AlbumFileName);
            if (string.IsNullOrEmpty(content))
            {
                return new List<PhotoAlbum>();
            }

            var root = JsonObject.Parse(content);
            var albums = new List<PhotoAlbum>();
            var values = root.GetNamedArray("albums", new JsonArray());
            foreach (var value in values)
            {
                var entry = value.GetObject();
                var itemIds = new List<string>();
                var ids = entry.GetNamedArray("itemIds", new JsonArray());
                foreach (var id in ids)
                {
                    itemIds.Add(id.GetString());
                }
                albums.Add(new PhotoAlbum
                {
                    Id = entry.GetNamedString("id"),
                    Name = entry.GetNamedString("name"),
                    ItemIds = itemIds
                });
            }
            return albums.OrderBy(album => album.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task CreateAlbumAsync(string name)
        {
            var trimmedName = name == null ? string.Empty : name.Trim();
            if (string.IsNullOrEmpty(trimmedName))
            {
                throw new InvalidOperationException("Enter an album name.");
            }

            var albums = (await GetAlbumsAsync()).ToList();
            if (albums.Any(album => string.Equals(album.Name, trimmedName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("An album with this name already exists.");
            }

            albums.Add(new PhotoAlbum { Id = Guid.NewGuid().ToString("N"), Name = trimmedName });
            await SaveAlbumsAsync(albums);
        }

        public async Task AddPhotosAsync(string albumId, IEnumerable<string> itemIds)
        {
            var albums = (await GetAlbumsAsync()).ToList();
            var album = albums.FirstOrDefault(candidate => candidate.Id == albumId);
            if (album == null)
            {
                throw new InvalidOperationException("The selected album no longer exists.");
            }

            foreach (var itemId in itemIds)
            {
                if (!album.ItemIds.Contains(itemId))
                {
                    album.ItemIds.Add(itemId);
                }
            }
            await SaveAlbumsAsync(albums);
        }

        private async Task SaveAlbumsAsync(IEnumerable<PhotoAlbum> albums)
        {
            var root = new JsonObject();
            var values = new JsonArray();
            foreach (var album in albums)
            {
                var entry = new JsonObject();
                entry.SetNamedValue("id", JsonValue.CreateStringValue(album.Id));
                entry.SetNamedValue("name", JsonValue.CreateStringValue(album.Name));
                var ids = new JsonArray();
                foreach (var itemId in album.ItemIds)
                {
                    ids.Add(JsonValue.CreateStringValue(itemId));
                }
                entry.SetNamedValue("itemIds", ids);
                values.Add(entry);
            }
            root.SetNamedValue("albums", values);
            await _graph.WriteAppFolderFileAsync(AlbumFileName, root.Stringify());
        }
    }
}
