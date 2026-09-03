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
            Clipboard = new DriveClipboard();
            CameraUploadHistory = new CameraUploadHistoryStore();
        }

        public ITokenStore TokenStore { get; }
        public OAuthService Auth { get; }
        public IGraphClient Graph { get; }
        public PhotoIndexStore PhotoIndex { get; }
        public PhotoAlbumService Albums { get; }
        public DriveClipboard Clipboard { get; }
        public CameraUploadHistoryStore CameraUploadHistory { get; }
    }
}
