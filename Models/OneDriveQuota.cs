namespace LiveDrive.Models
{
#if BACKGROUND_TASK
    internal sealed class OneDriveQuota
#else
    public sealed class OneDriveQuota
#endif
    {
        public long Used { get; set; }
        public long Total { get; set; }
    }
}
