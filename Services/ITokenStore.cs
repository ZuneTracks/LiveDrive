using System.Threading.Tasks;
using LiveDrive.Models;

namespace LiveDrive.Services
{
#if BACKGROUND_TASK
    internal interface ITokenStore
#else
    public interface ITokenStore
#endif
    {
        Task<AuthToken> GetAsync();
        Task SaveAsync(AuthToken token);
        Task ClearAsync();
    }
}
