using System.Collections.Generic;

namespace LiveDrive.Models
{
    public sealed class DriveItemPage
    {
        public IReadOnlyList<DriveItem> Items { get; set; }
        public string NextLink { get; set; }
        public string DeltaLink { get; set; }
    }
}
