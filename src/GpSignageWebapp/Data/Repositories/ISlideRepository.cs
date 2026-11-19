using GpSignageWebapp.Models;

namespace GpSignageWebapp.Data.Repositories;

public interface ISlideRepository : IGenericRepository<Slide>
{
    /// <summary>
    /// Returns active slides ordered for playback.
    /// </summary>
    Task<IReadOnlyList<Slide>> GetPlaylistAsync(CancellationToken cancellationToken = default);
}
