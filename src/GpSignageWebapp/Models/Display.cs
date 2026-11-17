namespace GpSignageWebapp.Models;

public class Display
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human-friendly name, e.g. "Reception", "Window".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>URL key used by the player and SignalR group, e.g. "reception".</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>The slide currently being shown. Null when nothing is assigned.</summary>
    public Guid? CurrentSlideId { get; set; }

    public Slide? CurrentSlide { get; set; }

    /// <summary>Last time the player reported in (heartbeat).</summary>
    public DateTime? LastSeenUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
