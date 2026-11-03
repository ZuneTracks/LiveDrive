using System;

namespace LiveDrive.Models
{
#if BACKGROUND_TASK
    internal sealed class CameraTransferRecord
#else
    public sealed class CameraTransferRecord
#endif
    {
        public Guid TransferId { get; set; }
        public string FilePath { get; set; }
        public ulong FileSize { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
    }
}
