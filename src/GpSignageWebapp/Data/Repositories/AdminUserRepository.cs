using GpSignageWebapp.Models;
using Microsoft.EntityFrameworkCore;

namespace GpSignageWebapp.Data.Repositories;

public class AdminUserRepository : GenericRepository<AdminUser>, IAdminUserRepository
{
    public AdminUserRepository(AppDbContext context) : base(context)
    {
    }

    public Task<AdminUser?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        var normalized = (username ?? string.Empty).Trim().ToLowerInvariant();
        return DbSet.FirstOrDefaultAsync(u => u.Username == normalized, cancellationToken);
    }

    public Task<bool> AnyAsync(CancellationToken cancellationToken = default)
    {
        return DbSet.AnyAsync(cancellationToken);
    }
}
