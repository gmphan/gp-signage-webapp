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

        // Library listing is public (the player/admin both read it); mutations require auth.
        group.MapGet("/", GetAllAsync).AllowAnonymous();
        group.MapPost("/", UploadAsync)
            .DisableAntiforgery()    // API consumed by our own JS via fetch; no antiforgery token
            .RequireAuthorization();
        group.MapPatch("/{id:guid}", UpdateAsync).RequireAuthorization();
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization();
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

        // Accept one OR many files under the same "file" field. The admin UI can
        // select multiple at once; each becomes its own slide. (A single file keeps
        // the original single-object response shape for compatibility.)
        var files = form.Files.GetFiles("file");
        if (files.Count == 0)
        {
            return Results.BadRequest(new { error = "No 'file' field in the upload." });
        }

        // Fields shared by all files in this request (the UI applies one title/fit/
        // duration to the whole batch). Per-file title defaults to the file name.
        var sharedTitle = form["title"].ToString().Trim();
        var sharedFit = ParseFit(form["fit"].ToString());
        var sharedDuration = int.TryParse(form["durationSeconds"], out var d) && d > 0 ? d : (int?)null;

        var created = new List<Slide>();
        var errors = new List<string>();
        var sortOrder = await NextSortOrderAsync(slides, ct);

        foreach (var file in files)
        {
            if (file is null || file.Length == 0)
            {
                errors.Add($"{file?.FileName ?? "?"}: empty file.");
                continue;
            }

            var title = sharedTitle;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = Path.GetFileNameWithoutExtension(file.FileName);
            }

            // Derive media type from the extension unless explicitly provided.
            var mediaType = ParseMediaType(form["mediaType"].ToString());
            if (mediaType is null)
            {
                var ext = Path.GetExtension(file.FileName);
                mediaType = IsVideoExtension(ext) ? MediaType.Video : MediaType.Image;
            }

            string mediaUrl;
            try
            {
                mediaUrl = await storage.SaveAsync(file, ct);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"{file.FileName}: {ex.Message}");
                continue;
            }

            var slide = new Slide
            {
                Title = title,
                MediaUrl = mediaUrl,
                MediaType = mediaType.Value,
                Fit = sharedFit ?? MediaFit.Contain,
                DurationSeconds = sharedDuration ?? 10,
                SortOrder = sortOrder++,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };

            await slides.AddAsync(slide, ct);
            created.Add(slide);
        }

        // Persist everything in one round-trip.
        if (created.Count > 0)
        {
            await slides.SaveChangesAsync(ct);
        }

        if (created.Count == 0)
        {
            return Results.BadRequest(new { error = "No files could be uploaded.", errors });
        }

        // Backward-compatible shape: a single uploaded file returns the slide object;
        // multiple files return an array. Partial failures ride along as `errors`.
        return files.Count == 1 && errors.Count == 0
            ? Results.Created($"/api/slides/{created[0].Id}", SlideDto.From(created[0]))
            : Results.Created("/api/slides", new
            {
                slides = created.Select(SlideDto.From).ToArray(),
                errors,
            });
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
        var fit = ParseFit(body.Fit);
        if (fit is not null) slide.Fit = fit.Value;
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

    private static MediaFit? ParseFit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Enum.TryParse<MediaFit>(raw, ignoreCase: true, out var fit) ? fit : null;
    }

    private static bool IsVideoExtension(string? ext) =>
        ext is not null && (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                         || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
                         || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase));
}
