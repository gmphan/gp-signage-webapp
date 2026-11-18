namespace GpSignageWebapp.Storage;

/// <summary>
/// Saves uploaded media to a directory under wwwroot and returns its relative URL.
/// Kept deliberately small; swap for blob storage later if needed.
/// </summary>
public interface IStorageService
{
    /// <summary>
    /// Save an uploaded file under the uploads folder with a generated safe name.
    /// Returns the public relative URL, e.g. "/uploads/2026111822_ab12cd34ef56.jpg".
    /// </summary>
    Task<string> SaveAsync(IFormFile file, CancellationToken ct = default);

    /// <summary>Delete a previously saved file given its relative URL. No-op if missing.</summary>
    void Delete(string relativeUrl);
}

public class LocalStorageService : IStorageService
{
    private const long MaxBytes = 50 * 1024 * 1024; // 50 MB cap per file

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp",   // images
        ".mp4", ".webm", ".mov",                    // video
    };

    private readonly string _uploadDir;
    private readonly string _publicPrefix;
    private readonly ILogger<LocalStorageService> _logger;

    public LocalStorageService(IWebHostEnvironment env, IConfiguration configuration, ILogger<LocalStorageService> logger)
    {
        _logger = logger;

        // Prefer an absolute path outside wwwroot (e.g. a mounted volume) when configured,
        // otherwise default to wwwroot/uploads (served by UseStaticFiles).
        var configured = configuration["Storage:UploadsPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _uploadDir = Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(env.ContentRootPath, configured);
            _publicPrefix = "/uploads"; // must be served separately when outside wwwroot
        }
        else
        {
            _uploadDir = Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "uploads");
            _publicPrefix = "/uploads";
        }
    }

    public async Task<string> SaveAsync(IFormFile file, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            throw new InvalidOperationException("No file was uploaded.");
        }

        if (file.Length > MaxBytes)
        {
            throw new InvalidOperationException($"File exceeds the {MaxBytes / (1024 * 1024)} MB limit.");
        }

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext) || !AllowedExtensions.Contains(ext))
        {
            throw new InvalidOperationException(
                $"Unsupported file type '{ext}'. Allowed: {string.Join(", ", AllowedExtensions)}.");
        }

        Directory.CreateDirectory(_uploadDir);

        // Generate a collision-resistant, extension-preserving name (never trust the client name).
        var safeName = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var fullPath = Path.Combine(_uploadDir, safeName);

        await using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await file.CopyToAsync(stream, ct);
        }

        var url = $"{_publicPrefix}/{safeName}";
        _logger.LogInformation("Saved upload {Bytes} bytes -> {Url}", file.Length, url);
        return url;
    }

    public void Delete(string relativeUrl)
    {
        if (string.IsNullOrWhiteSpace(relativeUrl))
        {
            return;
        }

        var name = Path.GetFileName(relativeUrl);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var fullPath = Path.Combine(_uploadDir, name);
        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete upload {Path}", fullPath);
        }
    }
}
