using System.Collections.Generic;

namespace LiveDrive.Models
{
    public sealed class PhotoViewerRequest
    {
        public IReadOnlyList<DriveItem> Items { get; set; }
        public int SelectedIndex { get; set; }
    }
}
