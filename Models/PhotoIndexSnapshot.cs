using System.Collections.Generic;

namespace LiveDrive.Models
{
    public sealed class PhotoIndexSnapshot
    {
        public IReadOnlyList<DriveItem> Items { get; set; }
        public IReadOnlyList<PhotoSourceFolder> SourceFolders { get; set; }
    }
}
