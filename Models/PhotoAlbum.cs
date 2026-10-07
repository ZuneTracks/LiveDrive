using System.Collections.Generic;
using System.ComponentModel;

namespace LiveDrive.Models
{
    public sealed class PhotoAlbum : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<string> ItemIds { get; set; } = new List<string>();
        public int? DisplayItemCount { get; set; }
        private string _coverThumbnailUrl;

        public string CoverThumbnailUrl
        {
            get { return _coverThumbnailUrl; }
            set
            {
                if (_coverThumbnailUrl != value)
                {
                    _coverThumbnailUrl = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverThumbnailUrl)));
                }
            }
        }

        public string ItemCountLabel
        {
            get
            {
                var count = DisplayItemCount ?? ItemIds.Count;
                return count == 1 ? "1 photo" : count + " photos";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
