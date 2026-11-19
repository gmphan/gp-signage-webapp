using GpSignageWebapp.Models;
using Microsoft.EntityFrameworkCore;

namespace GpSignageWebapp.Data.Repositories;

public class SlideRepository : GenericRepository<Slide>, ISlideRepository
{
    public SlideRepository(AppDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<Slide>> GetPlaylistAsync(CancellationToken cancellationToken = default)
    {
        return await DbSet
            .AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Title)
            .ToListAsync(cancellationToken);
    }
}
