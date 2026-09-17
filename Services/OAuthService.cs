using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LiveDrive.Models;
using Windows.Data.Json;
using Windows.Security.Authentication.Web;

namespace LiveDrive.Services
{
#if BACKGROUND_TASK
    internal sealed class OAuthService
#else
    public sealed class OAuthService
#endif
    {
        private readonly ITokenStore _tokenStore;

        public OAuthService(ITokenStore tokenStore)
        {
            _tokenStore = tokenStore;
        }

        public Task<AuthToken> GetStoredTokenAsync()
        {
            return _tokenStore.GetAsync();
        }

        public async Task<AuthToken> SignInAsync()
        {
            if (!ClientConfiguration.IsConfigured)
            {
                throw new InvalidOperationException("Set ClientConfiguration.ClientId before signing in.");
            }

            var verifier = CreateVerifier();
            var authorizationUri = new Uri(ClientConfiguration.Authority + "/oauth2/v2.0/authorize?" +
                "client_id=" + Uri.EscapeDataString(ClientConfiguration.ClientId) +
                "&response_type=code&redirect_uri=" + Uri.EscapeDataString(ClientConfiguration.RedirectUri) +
                "&response_mode=query&scope=" + Uri.EscapeDataString(ClientConfiguration.Scopes) +
                "&code_challenge=" + Uri.EscapeDataString(CreateChallenge(verifier)) +
                "&code_challenge_method=S256");

            var result = await WebAuthenticationBroker.AuthenticateAsync(
                WebAuthenticationOptions.None, authorizationUri, new Uri(ClientConfiguration.RedirectUri));
            if (result.ResponseStatus != WebAuthenticationStatus.Success)
            {
                throw new InvalidOperationException(result.ResponseStatus == WebAuthenticationStatus.UserCancel
                    ? "Sign-in was cancelled." : "Microsoft sign-in did not complete: " + result.ResponseErrorDetail);
            }

            var responseUri = new Uri(result.ResponseData);
            var code = GetQueryValue(responseUri.Query, "code");
            var error = GetQueryValue(responseUri.Query, "error_description");
            if (string.IsNullOrEmpty(code))
            {
                throw new InvalidOperationException(string.IsNullOrEmpty(error) ? "Authorization returned no code." : error);
            }

            return await ExchangeAsync(new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "client_id", ClientConfiguration.ClientId },
                { "code", code },
                { "redirect_uri", ClientConfiguration.RedirectUri },
                { "code_verifier", verifier },
                { "scope", ClientConfiguration.Scopes }
            });
        }

        public async Task<AuthToken> GetAccessTokenAsync()
        {
            var token = await _tokenStore.GetAsync();
            if (token == null)
            {
                throw new InvalidOperationException("Sign in to access your drive.");
            }

            if (!token.IsExpired)
            {
                return token;
            }

            if (string.IsNullOrEmpty(token.RefreshToken))
            {
                throw new InvalidOperationException("Your session expired. Please sign in again.");
            }

            return await ExchangeAsync(new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "client_id", ClientConfiguration.ClientId },
                { "refresh_token", token.RefreshToken },
                { "scope", ClientConfiguration.Scopes }
            });
        }

        public Task SignOutAsync()
        {
            return _tokenStore.ClearAsync();
        }

        private async Task<AuthToken> ExchangeAsync(Dictionary<string, string> fields)
        {
            using (var client = new HttpClient())
            using (var response = await client.PostAsync(ClientConfiguration.Authority + "/oauth2/v2.0/token",
                new FormUrlEncodedContent(fields)))
            {
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Token request failed: " + body);
                }

                var json = JsonObject.Parse(body);
                var token = new AuthToken
                {
                    AccessToken = json.GetNamedString("access_token"),
                    RefreshToken = json.GetNamedString("refresh_token", string.Empty),
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(json.GetNamedNumber("expires_in", 3600))
                };
                await _tokenStore.SaveAsync(token);
                return token;
            }
        }

        private static string CreateVerifier()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            return Base64Url(bytes);
        }

        private static string CreateChallenge(string verifier)
        {
            using (var sha256 = SHA256.Create())
            {
                return Base64Url(sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            }
        }

        private static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string GetQueryValue(string query, string name)
        {
            foreach (var segment in query.TrimStart('?').Split('&'))
            {
                var pair = segment.Split(new[] { '=' }, 2);
                if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.Ordinal))
                {
                    return Uri.UnescapeDataString(pair[1].Replace("+", " "));
                }
            }
            return null;
        }
    }
}
