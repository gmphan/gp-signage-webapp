using GpSignageWebapp.Models;

namespace GpSignageWebapp.Dtos;

/// <summary>Slide as sent to browsers (player + admin).</summary>
public record SlideDto(
    Guid Id,
    string Title,
    string MediaUrl,
    string MediaType,
    int DurationSeconds,
    int SortOrder)
{
    public static SlideDto From(Slide s) => new(
        s.Id,
        s.Title,
        s.MediaUrl,
        s.MediaType.ToString(),
        s.DurationSeconds,
        s.SortOrder);
}

/// <summary>Display as shown in the admin list, including what it's currently showing.</summary>
public record DisplayDto(
    Guid Id,
    string Name,
    string Slug,
    string Orientation,
    Guid? CurrentSlideId,
    SlideDto? CurrentSlide,
    DateTime? LastSeenUtc,
    bool IsOnline,
    IReadOnlyList<SlideDto> Playlist)
{
    /// <summary>A display is "online" if it reported a heartbeat within this window.</summary>
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);

    public static DisplayDto From(Display d) => new(
        d.Id,
        d.Name,
        d.Slug,
        d.Orientation.ToString(),
        d.CurrentSlideId,
        d.CurrentSlide is null ? null : SlideDto.From(d.CurrentSlide),
        d.LastSeenUtc,
        d.LastSeenUtc is not null && DateTime.UtcNow - d.LastSeenUtc.Value < OnlineWindow,
        d.Playlist
            .Where(p => p.Slide is not null)
            .OrderBy(p => p.SortOrder)
            .Select(p => SlideDto.From(p.Slide!))
            .ToList());
}

/// <summary>The ordered playlist for a display, as pushed to the player.</summary>
public record PlaylistDto(
    string Slug,
    IReadOnlyList<SlideDto> Slides)
{
    public static PlaylistDto From(Display d) => new(
        d.Slug,
        d.Playlist
            .Where(p => p.Slide is not null)
            .OrderBy(p => p.SortOrder)
            .Select(p => SlideDto.From(p.Slide!))
            .ToList());
}

/// <summary>Body of POST /api/displays/{slug}/show.</summary>
public record ShowSlideRequest(Guid SlideId);

/// <summary>Body of PUT /api/displays/{slug}/playlist — the full ordered list of slide ids.</summary>
public record SetPlaylistRequest(IReadOnlyList<Guid> SlideIds);

/// <summary>Body of POST /api/displays — create a display. Slug is optional (derived from Name).</summary>
public record CreateDisplayRequest(
    string Name,
    string? Slug,
    string? Orientation);

/// <summary>Body of PATCH /api/displays/{slug} — rename / change slug / change orientation. All optional.</summary>
public record UpdateDisplayRequest(
    string? Name,
    string? Slug,
    string? Orientation);

/// <summary>Body of PATCH /api/slides/{id}. All fields optional.</summary>
public record UpdateSlideRequest(
    string? Title,
    int? DurationSeconds,
    int? SortOrder,
    bool? IsActive);
