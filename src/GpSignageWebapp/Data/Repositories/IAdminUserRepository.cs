using GpSignageWebapp.Models;

namespace GpSignageWebapp.Data.Repositories;

public interface IAdminUserRepository : IGenericRepository<AdminUser>
{
    /// <summary>Look up by normalized (lowercase) username.</summary>
    Task<AdminUser?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default);

    Task<bool> AnyAsync(CancellationToken cancellationToken = default);
}
