using GpSignageWebapp.Models;
using Microsoft.EntityFrameworkCore;

namespace GpSignageWebapp.Data.Repositories;

public class DisplayRepository : GenericRepository<Display>, IDisplayRepository
{
    public DisplayRepository(AppDbContext context) : base(context)
    {
    }

    public async Task<Display?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Include(d => d.CurrentSlide)
            .Include(d => d.Playlist.OrderBy(p => p.SortOrder))
                .ThenInclude(p => p.Slide)
            .FirstOrDefaultAsync(d => d.Slug == slug, cancellationToken);
    }

    public async Task<IReadOnlyList<Display>> GetAllWithSlideAsync(CancellationToken cancellationToken = default)
    {
        return await DbSet
            .AsNoTracking()
            .Include(d => d.CurrentSlide)
            .Include(d => d.Playlist.OrderBy(p => p.SortOrder))
                .ThenInclude(p => p.Slide)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task TouchLastSeenAsync(string slug, DateTime seenUtc, CancellationToken cancellationToken = default)
    {
        var display = await DbSet.FirstOrDefaultAsync(d => d.Slug == slug, cancellationToken);
        if (display is null)
        {
            return;
        }

        display.LastSeenUtc = seenUtc;
        await Context.SaveChangesAsync(cancellationToken);
    }
}

