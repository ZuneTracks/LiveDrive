namespace LiveDrive.Services
{
#if BACKGROUND_TASK
    internal static class ClientConfiguration
#else
    public static class ClientConfiguration
#endif
    {
        // Register these values in a Microsoft Entra public-client app before building.
        public const string ClientId = "7e31d58c-0e88-4ece-9515-11f40ff9db26";
        public const string RedirectUri = "https://login.microsoftonline.com/common/oauth2/nativeclient";
        public const string Authority = "https://login.microsoftonline.com/common";
        public const string Scopes = "offline_access User.Read Files.ReadWrite.All Files.ReadWrite.AppFolder";

        public static bool IsConfigured => !ClientId.StartsWith("REPLACE-", System.StringComparison.Ordinal);
    }
}
