using System.Collections.Generic;

namespace LiveDrive.Models
{
    public sealed class PhotoAlbum
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<string> ItemIds { get; set; } = new List<string>();
        public int? DisplayItemCount { get; set; }

        public string ItemCountLabel
        {
            get
            {
                var count = DisplayItemCount ?? ItemIds.Count;
                return count == 1 ? "1 photo" : count + " photos";
            }
        }
    }
}
