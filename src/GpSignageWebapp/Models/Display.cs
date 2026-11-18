namespace GpSignageWebapp.Models;

/// <summary>Physical orientation of the TV showing this display.</summary>
public enum DisplayOrientation
{
    Landscape = 0,
    Portrait = 1,
}

public class Display
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human-friendly name, e.g. "Reception", "Window".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>URL key used by the player and SignalR group, e.g. "reception".</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// How the TV is mounted. Portrait TVs (vertical menus/ads) get orientation-aware
    /// rendering in the player. Defaults to Landscape for backward compatibility.
    /// </summary>
    public DisplayOrientation Orientation { get; set; } = DisplayOrientation.Landscape;

    /// <summary>The slide currently being shown. Null when nothing is assigned.</summary>
    public Guid? CurrentSlideId { get; set; }

    public Slide? CurrentSlide { get; set; }

    /// <summary>
    /// Ordered playlist this display cycles through. Empty means "show nothing".
    /// A single-entry playlist is equivalent to the old "show one slide" behavior.
    /// </summary>
    public List<DisplaySlide> Playlist { get; set; } = new();

    /// <summary>Last time the player reported in (heartbeat).</summary>
    public DateTime? LastSeenUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

