namespace LiveDrive.Services
{
    public sealed class AppServices
    {
        public AppServices()
        {
            TokenStore = new PasswordVaultTokenStore();
            Auth = new OAuthService(TokenStore);
            Graph = new GraphClient(Auth);
            PhotoIndex = new PhotoIndexStore();
            Albums = new PhotoAlbumService(Graph);
            LiveTile = new LiveTileService(Graph, PhotoIndex, Albums);
            Clipboard = new DriveClipboard();
            CameraUploadHistory = new CameraUploadHistoryStore();
            CameraBackupState = new CameraBackupStateStore();
            CameraBackup = new CameraBackupService(Graph, CameraUploadHistory, CameraBackupState);
            CameraBackupScheduler = new CameraBackupScheduler();
        }

        public ITokenStore TokenStore { get; }
        public OAuthService Auth { get; }
        public IGraphClient Graph { get; }
        public PhotoIndexStore PhotoIndex { get; }
        public PhotoAlbumService Albums { get; }
        public LiveTileService LiveTile { get; }
        public DriveClipboard Clipboard { get; }
        public CameraUploadHistoryStore CameraUploadHistory { get; }
        public CameraBackupStateStore CameraBackupState { get; }
        public CameraBackupService CameraBackup { get; }
        public CameraBackupScheduler CameraBackupScheduler { get; }
    }
}
