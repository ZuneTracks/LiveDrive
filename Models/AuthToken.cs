using System;

namespace LiveDrive.Models
{
#if BACKGROUND_TASK
    internal sealed class AuthToken
#else
    public sealed class AuthToken
#endif
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }

        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt.Subtract(TimeSpan.FromMinutes(2));
    }
}
