namespace GpSignageWebapp.Models;

public enum MediaType
{
    Image = 0,
    Video = 1,
}

public class Slide
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Relative URL to the media file under wwwroot (e.g. "/uploads/slide1.jpg").
    /// </summary>
    public string MediaUrl { get; set; } = string.Empty;

    public MediaType MediaType { get; set; } = MediaType.Image;

    /// <summary>
    /// How long the slide is shown, in seconds. Ignored for video (plays to end).
    /// </summary>
    public int DurationSeconds { get; set; } = 10;

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
