namespace LiveDrive.Models
{
    public sealed class ScheduledBackupQueueItem
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
        public bool IsLargeUploadInProgress { get; set; }
        public bool IsAwaitingRetry { get; set; }
    }
}
