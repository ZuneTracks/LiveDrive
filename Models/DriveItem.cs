using System.ComponentModel;

namespace LiveDrive.Models
{
    public sealed class DriveItem : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsFolder { get; set; }
        public long Size { get; set; }
        public string LastModified { get; set; }
        public string DownloadUrl { get; set; }
        private string _thumbnailUrl;

        public string ThumbnailUrl
        {
            get { return _thumbnailUrl; }
            set
            {
                if (_thumbnailUrl != value)
                {
                    _thumbnailUrl = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailUrl)));
                }
            }
        }

        public string MimeType { get; set; }
        public string DateTaken { get; set; }
        public bool IsDeleted { get; set; }

        public string TypeLabel => IsFolder ? "Folder" : "File";
        public string Detail => IsFolder ? "Folder" : FormatFileSize(Size);

        private static string FormatFileSize(long size)
        {
            const long kilobyte = 1024;
            const long megabyte = kilobyte * 1024;
            const long gigabyte = megabyte * 1024;

            if (size < kilobyte)
            {
                return size == 1 ? "1 byte" : string.Format("{0:N0} bytes", size);
            }
            if (size < megabyte)
            {
                return string.Format("{0:0.#} KB", (double)size / kilobyte);
            }
            if (size < gigabyte)
            {
                return string.Format("{0:0.#} MB", (double)size / megabyte);
            }
            return string.Format("{0:0.#} GB", (double)size / gigabyte);
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
