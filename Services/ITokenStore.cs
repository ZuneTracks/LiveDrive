using System.Threading.Tasks;
using LiveDrive.Models;

namespace LiveDrive.Services
{
    public interface ITokenStore
    {
        Task<AuthToken> GetAsync();
        Task SaveAsync(AuthToken token);
        Task ClearAsync();
    }
}
