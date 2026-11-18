using GpSignageWebapp.Data.Repositories;
using GpSignageWebapp.Dtos;
using GpSignageWebapp.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace GpSignageWebapp.Endpoints;

/// <summary>
/// Display-related endpoints (≈ a MVC "DisplaysController"):
///   GET    /api/displays                          — list all displays (+ playlist)
///   GET    /api/displays/{slug}                   — one display (with playlist)
///   POST   /api/displays                          — create a display
///   PATCH  /api/displays/{slug}                   — rename / change slug / change orientation
///   DELETE /api/displays/{slug}                   — delete a display (its playlist cascades)
///   POST   /api/displays/{slug}/show              — replace playlist with a single slide + push
///   GET    /api/displays/{slug}/playlist          — the display's ordered playlist
///   PUT    /api/displays/{slug}/playlist          — replace the whole playlist (ordered ids) + push
///   POST   /api/displays/{slug}/playlist/{slideId} — append a slide + push
///   DELETE /api/displays/{slug}/playlist/{slideId} — remove a slide + push
/// </summary>
public static class DisplayEndpoints
{
    public static void MapDisplayEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/displays");

        group.MapGet("/", GetAllAsync);
        group.MapGet("/{slug}", GetBySlugAsync);
        group.MapPost("/", CreateAsync);
        group.MapPatch("/{slug}", UpdateAsync);
        group.MapDelete("/{slug}", DeleteAsync);
        group.MapPost("/{slug}/show", ShowSlideAsync);

        group.MapGet("/{slug}/playlist", GetPlaylistAsync);
        group.MapPut("/{slug}/playlist", SetPlaylistAsync);
        group.MapPost("/{slug}/playlist/{slideId:guid}", AddToPlaylistAsync);
        group.MapDelete("/{slug}/playlist/{slideId:guid}", RemoveFromPlaylistAsync);
    }

    private static async Task<IResult> GetAllAsync(
        IDisplayRepository displays,
        CancellationToken ct)
    {
        var all = await displays.GetAllWithSlideAsync(ct);
        return Results.Ok(all.Select(DisplayDto.From));
    }

    private static async Task<IResult> GetBySlugAsync(
        string slug,
        IDisplayRepository displays,
        CancellationToken ct)
    {
        var display = await displays.GetBySlugAsync(slug.Trim().ToLowerInvariant(), ct);
        return display is null
            ? Results.NotFound(new { error = $"Display '{slug}' not found." })
            : Results.Ok(DisplayDto.From(display));
    }

    private static async Task<IResult> GetPlaylistAsync(
        string slug,
        IDisplayRepository displays,
        CancellationToken ct)
    {
        var display = await displays.GetBySlugAsync(slug.Trim().ToLowerInvariant(), ct);
        return display is null
            ? Results.NotFound(new { error = $"Display '{slug}' not found." })
            : Results.Ok(PlaylistDto.From(display));
    }

    /// <summary>Create a new display. Slug is derived from the name if not supplied.</summary>
    private static async Task<IResult> CreateAsync(
        CreateDisplayRequest request,
        IDisplayRepository displays,
        CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new { error = "Name is required." });
        }

        var slug = Slugify(string.IsNullOrWhiteSpace(request.Slug) ? name : request.Slug);
        if (string.IsNullOrWhiteSpace(slug))
        {
            return Results.BadRequest(new { error = "Could not derive a valid slug; please provide one." });
        }
        if (await displays.GetBySlugAsync(slug, ct) is not null)
        {
            return Results.Conflict(new { error = $"A display with slug '{slug}' already exists." });
        }

        var orientation = ParseOrientation(request.Orientation) ?? Models.DisplayOrientation.Portrait;

        var display = new Models.Display
        {
            Name = name,
            Slug = slug,
            Orientation = orientation,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        await displays.AddAsync(display, ct);
        await displays.SaveChangesAsync(ct);

        var created = await displays.GetBySlugAsync(slug, ct);
        return Results.Created($"/api/displays/{slug}", DisplayDto.From(created!));
    }

    /// <summary>Rename / re-slug / change orientation. All fields optional.</summary>
    private static async Task<IResult> UpdateAsync(
        string slug,
        UpdateDisplayRequest request,
        IDisplayRepository displays,
        IHubContext<SignageHub> hub,
        CancellationToken ct)
    {
        slug = slug.Trim().ToLowerInvariant();

        var display = await displays.GetBySlugAsync(slug, ct);
        if (display is null)
        {
            return Results.NotFound(new { error = $"Display '{slug}' not found." });
        }

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest(new { error = "Name cannot be empty." });
            }
            display.Name = name;
        }

        string? newSlug = null;
        if (request.Slug is not null)
        {
            newSlug = Slugify(request.Slug);
            if (string.IsNullOrWhiteSpace(newSlug))
            {
                return Results.BadRequest(new { error = "Invalid slug." });
            }
            if (!string.Equals(newSlug, slug, StringComparison.Ordinal)
                && await displays.GetBySlugAsync(newSlug, ct) is not null)
            {
                return Results.Conflict(new { error = $"A display with slug '{newSlug}' already exists." });
            }
            display.Slug = newSlug;
        }

        if (request.Orientation is not null)
        {
            var orientation = ParseOrientation(request.Orientation);
            if (orientation is null)
            {
                return Results.BadRequest(new { error = "Orientation must be 'Portrait' or 'Landscape'." });
            }
            display.Orientation = orientation.Value;
        }

        display.UpdatedAtUtc = DateTime.UtcNow;
        displays.Update(display);
        await displays.SaveChangesAsync(ct);

        var updated = await displays.GetBySlugAsync(display.Slug, ct);

        // If the slug changed, nudge any already-connected player to rejoin with the new slug.
        if (newSlug is not null && !string.Equals(newSlug, slug, StringComparison.Ordinal))
        {
            await hub.Clients
                .Group(SignageHub.GroupFor(slug))
                .SendAsync("SlugChanged", newSlug);
        }
        else
        {
            // Name/orientation changed — re-push so the player can pick up orientation if it uses it.
            await PushPlaylistAsync(hub, updated!);
        }

        return Results.Ok(DisplayDto.From(updated!));
    }

    /// <summary>Delete a display. Its playlist rows cascade; connected players are told to go idle.</summary>
    private static async Task<IResult> DeleteAsync(
        string slug,
        IDisplayRepository displays,
        IHubContext<SignageHub> hub,
        CancellationToken ct)
    {
        slug = slug.Trim().ToLowerInvariant();

        var display = await displays.GetBySlugAsync(slug, ct);
        if (display is null)
        {
            return Results.NotFound(new { error = $"Display '{slug}' not found." });
        }

        displays.Remove(display);
        await displays.SaveChangesAsync(ct);

        // Signal any connected player that this display is gone.
        await hub.Clients
            .Group(SignageHub.GroupFor(slug))
            .SendAsync("DisplayDeleted");

        return Results.NoContent();
    }

    /// <summary>Turn a name/slug into a URL-safe slug: lowercase a-z, 0-9 and dashes.</summary>
    private static string Slugify(string input)
    {
        var s = (input ?? string.Empty).Trim().ToLowerInvariant();
        var chars = s.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var joined = new string(chars);
        // Collapse runs of dashes and trim them from the ends.
        while (joined.Contains("--")) joined = joined.Replace("--", "-");
        return joined.Trim('-');
    }

    private static Models.DisplayOrientation? ParseOrientation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (Enum.TryParse<Models.DisplayOrientation>(raw, ignoreCase: true, out var o)) return o;
        return null;
    }

    /// <summary>
    /// "Show this one slide" — replaces the playlist with just that slide and pushes it.
    /// Keeps the old single-slide UX; it's just a 1-item playlist.
    /// </summary>
    private static async Task<IResult> ShowSlideAsync(
        string slug,
        ShowSlideRequest request,
        IDisplayRepository displays,
        ISlideRepository slides,
        IHubContext<SignageHub> hub,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("DisplayEndpoints");
        slug = slug.Trim().ToLowerInvariant();

        var display = await displays.GetBySlugAsync(slug, ct);
        if (display is null)
        {
            return Results.NotFound(new { error = $"Display '{slug}' not found." });
        }

        var slide = await slides.GetByIdAsync(request.SlideId, ct);
        if (slide is null)
        {
            return Results.BadRequest(new { error = $"Slide '{request.SlideId}' not found." });
        }

        // Replace playlist with a single slide.
        display.CurrentSlideId = slide.Id;
        display.UpdatedAtUtc = DateTime.UtcNow;
        display.Playlist.Clear();
        display.Playlist.Add(new Models.DisplaySlide { DisplayId = display.Id, SlideId = slide.Id, SortOrder = 0 });
        displays.Update(display);
        await displays.SaveChangesAsync(ct);

        logger.LogInformation("Display '{Slug}' now showing single slide '{SlideTitle}' ({SlideId})",
            slug, slide.Title, slide.Id);

        // Reload with the new playlist (slides included), push it, and return.
        var updated = await displays.GetBySlugAsync(slug, ct);
        await PushPlaylistAsync(hub, updated!);
        return Results.Ok(DisplayDto.From(updated!));
    }

    /// <summary>Replace the whole playlist with an ordered list of slide ids.</summary>
    private static async Task<IResult> SetPlaylistAsync(
        string slug,
        SetPlaylistRequest request,
        IDisplayRepository displays,
        ISlideRepository slides,
        IHubContext<SignageHub> hub,
        CancellationToken ct)
    {
        slug = slug.Trim().ToLowerInvariant();

        var display = await displays.GetBySlugAsync(slug, ct);
        if (display is null)
        {
            return Results.NotFound(new { error = $"Display '{slug}' not found." });
        }

        // Validate every requested slide exists.
        var ids = (request.SlideIds ?? Array.Empty<Guid>()).Distinct().ToList();
        foreach (var id in ids)
        {
            if (await slides.GetByIdAsync(id, ct) is null)
            {
                return Results.BadRequest(new { error = $"Slide '{id}' not found." });
            }
        }

        // Rebuild the playlist in the requested order.
        display.Playlist.Clear();
        for (var i = 0; i < ids.Count; i++)
        {
            display.Playlist.Add(new Models.DisplaySlide { DisplayId = display.Id, SlideId = ids[i], SortOrder = i });
        }
        display.CurrentSlideId = ids.Count > 0 ? ids[0] : null;
        display.UpdatedAtUtc = DateTime.UtcNow;

        displays.Update(display);
        await displays.SaveChangesAsync(ct);

        var updated = await displays.GetBySlugAsync(slug, ct);
        await PushPlaylistAsync(hub, updated!);
        return Results.Ok(DisplayDto.From(updated!));
    }

    /// <summary>Append a slide to the end of the playlist (no-op if already present).</summary>
    private static async Task<IResult> AddToPlaylistAsync(
        string slug,
        Guid slideId,
        IDisplayRepository displays,
        ISlideRepository slides,
        IHubContext<SignageHub> hub,
        CancellationToken ct)
    {
        slug = slug.Trim().ToLowerInvariant();

        var display = await displays.GetBySlugAsync(slug, ct);
        if (display is null)
        {
            return Results.NotFound(new { error = $"Display '{slug}' not found." });
        }
        if (await slides.GetByIdAsync(slideId, ct) is null)
        {
            return Results.BadRequest(new { error = $"Slide '{slideId}' not found." });
        }

        if (display.Playlist.All(p => p.SlideId != slideId))
        {
            var nextOrder = display.Playlist.Count == 0 ? 0 : display.Playlist.Max(p => p.SortOrder) + 1;
            display.Playlist.Add(new Models.DisplaySlide { DisplayId = display.Id, SlideId = slideId, SortOrder = nextOrder });
            display.CurrentSlideId ??= slideId;
            display.UpdatedAtUtc = DateTime.UtcNow;
            displays.Update(display);
            await displays.SaveChangesAsync(ct);
        }

        var updated = await displays.GetBySlugAsync(slug, ct);
        await PushPlaylistAsync(hub, updated!);
        return Results.Ok(DisplayDto.From(updated!));
    }

    /// <summary>Remove a slide from the playlist.</summary>
    private static async Task<IResult> RemoveFromPlaylistAsync(
        string slug,
        Guid slideId,
        IDisplayRepository displays,
        IHubContext<SignageHub> hub,
        CancellationToken ct)
    {
        slug = slug.Trim().ToLowerInvariant();

        var display = await displays.GetBySlugAsync(slug, ct);
        if (display is null)
        {
            return Results.NotFound(new { error = $"Display '{slug}' not found." });
        }

        var entry = display.Playlist.FirstOrDefault(p => p.SlideId == slideId);
        if (entry is not null)
        {
            display.Playlist.Remove(entry);
            // Re-number remaining entries.
            var ordered = display.Playlist.OrderBy(p => p.SortOrder).ToList();
            for (var i = 0; i < ordered.Count; i++) ordered[i].SortOrder = i;

            if (display.CurrentSlideId == slideId)
            {
                display.CurrentSlideId = ordered.FirstOrDefault()?.SlideId;
            }
            display.UpdatedAtUtc = DateTime.UtcNow;
            displays.Update(display);
            await displays.SaveChangesAsync(ct);
        }

        var updated = await displays.GetBySlugAsync(slug, ct);
        await PushPlaylistAsync(hub, updated!);
        return Results.Ok(DisplayDto.From(updated!));
    }

    /// <summary>Push the given display's (already-loaded, slide-included) playlist to its players.</summary>
    private static async Task PushPlaylistAsync(IHubContext<SignageHub> hub, Models.Display display)
    {
        // Not passing a CancellationToken: the push shouldn't depend on the HTTP request lifetime.
        await hub.Clients
            .Group(SignageHub.GroupFor(display.Slug))
            .SendAsync(SignageHub.ShowPlaylistMethod, PlaylistDto.From(display));
    }
}
