#if BACKGROUND_TASK
namespace LiveDrive.BackgroundTasks
#else
namespace LiveDrive.Models
#endif
{
#if BACKGROUND_TASK
    public sealed class CameraUploadSessionState
#else
    public sealed class CameraUploadSessionState
#endif
    {
        public string FilePath { get; set; }
        public string DestinationFolderId { get; set; }
        public ulong FileSize { get; set; }
        public string FileModifiedUtc { get; set; }
        public string UploadUrl { get; set; }
        public long NextOffset { get; set; }
    }

#if BACKGROUND_TASK
    public sealed class CameraUploadChunkResult
#else
    public sealed class CameraUploadChunkResult
#endif
    {
        public CameraUploadSessionState Session { get; set; }
        public bool IsComplete { get; set; }
    }
}
