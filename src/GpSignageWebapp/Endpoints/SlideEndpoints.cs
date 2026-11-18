using GpSignageWebapp.Data.Repositories;
using GpSignageWebapp.Dtos;
using GpSignageWebapp.Models;
using GpSignageWebapp.Storage;

namespace GpSignageWebapp.Endpoints;

/// <summary>
/// Slide-related endpoints (≈ a MVC "SlidesController"):
///   GET    /api/slides          — the active slide library, ordered for playback
///   POST   /api/slides          — upload media + create a slide (multipart/form-data)
///   PATCH  /api/slides/{id}     — update title/duration/sort/isActive
///   DELETE /api/slides/{id}     — delete a slide (and its file)
/// </summary>
public static class SlideEndpoints
{
    public static void MapSlideEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/slides");

        group.MapGet("/", GetAllAsync);
        group.MapPost("/", UploadAsync)
            .DisableAntiforgery();   // API consumed by our own JS via fetch; no antiforgery token
        group.MapPatch("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<IResult> GetAllAsync(
        ISlideRepository slides,
        CancellationToken ct)
    {
        var playlist = await slides.GetPlaylistAsync(ct);
        return Results.Ok(playlist.Select(SlideDto.From));
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        ISlideRepository slides,
        IStorageService storage,
        CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { error = "Expected multipart/form-data." });
        }

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null)
        {
            return Results.BadRequest(new { error = "No 'file' field in the upload." });
        }

        var title = form["title"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            // Fall back to the file name (without extension) as the title.
            title = Path.GetFileNameWithoutExtension(file.FileName);
        }

        // Derive media type from the extension unless explicitly provided.
        var mediaType = ParseMediaType(form["mediaType"].ToString());
        if (mediaType is null)
        {
            var ext = Path.GetExtension(file.FileName);
            mediaType = IsVideoExtension(ext) ? MediaType.Video : MediaType.Image;
        }

        var duration = int.TryParse(form["durationSeconds"], out var d) && d > 0 ? d : 10;

        string mediaUrl;
        try
        {
            mediaUrl = await storage.SaveAsync(file, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var slide = new Slide
        {
            Title = title,
            MediaUrl = mediaUrl,
            MediaType = mediaType.Value,
            DurationSeconds = duration,
            SortOrder = await NextSortOrderAsync(slides, ct),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        await slides.AddAsync(slide, ct);
        await slides.SaveChangesAsync(ct);

        return Results.Created($"/api/slides/{slide.Id}", SlideDto.From(slide));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateSlideRequest body,
        ISlideRepository slides,
        CancellationToken ct)
    {
        var slide = await slides.GetByIdAsync(id, ct);
        if (slide is null)
        {
            return Results.NotFound(new { error = $"Slide '{id}' not found." });
        }

        if (body.Title is not null) slide.Title = body.Title.Trim();
        if (body.DurationSeconds is > 0) slide.DurationSeconds = body.DurationSeconds.Value;
        if (body.SortOrder is not null) slide.SortOrder = body.SortOrder.Value;
        if (body.IsActive is not null) slide.IsActive = body.IsActive.Value;
        slide.UpdatedAtUtc = DateTime.UtcNow;

        slides.Update(slide);
        await slides.SaveChangesAsync(ct);
        return Results.Ok(SlideDto.From(slide));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        ISlideRepository slides,
        IStorageService storage,
        CancellationToken ct)
    {
        var slide = await slides.GetByIdAsync(id, ct);
        if (slide is null)
        {
            return Results.NotFound(new { error = $"Slide '{id}' not found." });
        }

        slides.Remove(slide);
        await slides.SaveChangesAsync(ct);

        // Remove the backing file after the row is gone (displays referencing it were set null via FK).
        storage.Delete(slide.MediaUrl);

        return Results.NoContent();
    }

    // ---- helpers --------------------------------------------------------------

    private static async Task<int> NextSortOrderAsync(ISlideRepository slides, CancellationToken ct)
    {
        var all = await slides.GetAllAsync(ct);
        return all.Count == 0 ? 0 : all.Max(s => s.SortOrder) + 1;
    }

    private static MediaType? ParseMediaType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Enum.TryParse<MediaType>(raw, ignoreCase: true, out var mt) ? mt : null;
    }

    private static bool IsVideoExtension(string? ext) =>
        ext is not null && (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                         || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
                         || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase));
}
