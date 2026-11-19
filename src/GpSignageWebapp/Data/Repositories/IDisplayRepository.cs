using GpSignageWebapp.Models;

namespace GpSignageWebapp.Data.Repositories;

public interface IDisplayRepository : IGenericRepository<Display>
{
    Task<Display?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Display>> GetAllWithSlideAsync(CancellationToken cancellationToken = default);

    /// <summary>Update LastSeenUtc for a display identified by slug (no-op if not found).</summary>
    Task TouchLastSeenAsync(string slug, DateTime seenUtc, CancellationToken cancellationToken = default);
}

