#if BACKGROUND_TASK
namespace LiveDrive.BackgroundTasks
#else
namespace LiveDrive.Models
#endif
{
    public sealed class CameraBackupRetryState
    {
        public string FilePath { get; set; }
        public int UnavailableCount { get; set; }
        public System.DateTimeOffset NextRetryUtc { get; set; }
    }
}
