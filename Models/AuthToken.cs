using System;

namespace LiveDrive.Models
{
    public sealed class AuthToken
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }

        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt.Subtract(TimeSpan.FromMinutes(2));
    }
}
