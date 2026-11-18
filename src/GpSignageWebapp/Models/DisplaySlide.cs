namespace GpSignageWebapp.Models;

/// <summary>
/// Join entity: a slide's position within a display's playlist.
/// A display cycles through its playlist in <see cref="SortOrder"/> order.
/// </summary>
public class DisplaySlide
{
    public Guid DisplayId { get; set; }
    public Display? Display { get; set; }

    public Guid SlideId { get; set; }
    public Slide? Slide { get; set; }

    /// <summary>Position within this display's playlist (0-based, ascending).</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
