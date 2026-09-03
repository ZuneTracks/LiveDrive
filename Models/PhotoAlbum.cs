using System.Collections.Generic;

namespace LiveDrive.Models
{
    public sealed class PhotoAlbum
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<string> ItemIds { get; set; } = new List<string>();

        public string ItemCountLabel => ItemIds.Count == 1 ? "1 photo" : ItemIds.Count + " photos";
    }
}
