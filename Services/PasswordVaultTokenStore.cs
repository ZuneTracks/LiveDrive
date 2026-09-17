using System;
using System.Threading.Tasks;
using LiveDrive.Models;
using Windows.Security.Credentials;

namespace LiveDrive.Services
{
#if BACKGROUND_TASK
    internal sealed class PasswordVaultTokenStore : ITokenStore
#else
    public sealed class PasswordVaultTokenStore : ITokenStore
#endif
    {
        private const string Resource = "LiveDrive.Token";
        private const string User = "Default";

        public Task<AuthToken> GetAsync()
        {
            try
            {
                var credential = new PasswordVault().Retrieve(Resource, User);
                credential.RetrievePassword();
                var parts = credential.Password.Split('|');
                if (parts.Length != 3)
                {
                    return Task.FromResult<AuthToken>(null);
                }

                return Task.FromResult(new AuthToken
                {
                    AccessToken = parts[0],
                    RefreshToken = parts[1],
                    ExpiresAt = DateTimeOffset.Parse(parts[2])
                });
            }
            catch (Exception)
            {
                return Task.FromResult<AuthToken>(null);
            }
        }

        public async Task SaveAsync(AuthToken token)
        {
            await ClearAsync();
            new PasswordVault().Add(new PasswordCredential(
                Resource, User, string.Join("|", token.AccessToken, token.RefreshToken ?? string.Empty, token.ExpiresAt.ToString("O"))));
        }

        public Task ClearAsync()
        {
            try
            {
                var vault = new PasswordVault();
                vault.Remove(vault.Retrieve(Resource, User));
            }
            catch (Exception)
            {
                // A missing credential is already the desired state.
            }

            return Task.CompletedTask;
        }
    }
}
