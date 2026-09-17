using System.Collections.Generic;

namespace LiveDrive.Models
{
#if BACKGROUND_TASK
    internal sealed class DriveItemPage
#else
    public sealed class DriveItemPage
#endif
    {
        public IReadOnlyList<DriveItem> Items { get; set; }
        public string NextLink { get; set; }
        public string DeltaLink { get; set; }
    }
}
