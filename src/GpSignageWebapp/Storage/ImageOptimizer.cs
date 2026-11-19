using SkiaSharp;

namespace GpSignageWebapp.Storage;

/// <summary>
/// Downscales and re-encodes uploaded images so slides are TV-friendly:
///   - max 1920×1080 (longest side capped, aspect preserved),
///   - JPEG q85 for opaque images, PNG for images with transparency,
///   - EXIF orientation applied so phone/screenshot photos aren't sideways.
/// Large PNG screenshots (multi‑MB) become a few hundred KB JPEGs — fast to decode
/// on a Raspberry Pi, so the player doesn't stall between slides.
/// </summary>
public static class ImageOptimizer
{
    public const int MaxWidth = 1920;
    public const int MaxHeight = 1080;
    private const int JpegQuality = 85;

    public sealed record Result(byte[] Bytes, string Extension);

    /// <summary>
    /// Optimize an image. Returns null when the input isn't a decodable image
    /// (caller should then store the original bytes as-is).
    /// </summary>
    public static Result? Optimize(Stream input)
    {
        // Decode (SkiaSharp reads EXIF orientation into the codec; we re-encode upright).
        using var original = SKBitmap.Decode(input);
        if (original is null || original.Width <= 0 || original.Height <= 0)
        {
            return null;
        }

        var (targetW, targetH) = Fit(original.Width, original.Height, MaxWidth, MaxHeight);

        using var resized = new SKBitmap(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(resized))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(original, new SKRect(0, 0, targetW, targetH), paint);
        }

        // Encode as PNG if the image uses transparency, otherwise a compact JPEG.
        var hasAlpha = HasTransparency(resized);
        var ext = hasAlpha ? ".png" : ".jpg";

        using var image = SKImage.FromBitmap(resized);
        using var data = hasAlpha
            ? image.Encode(SKEncodedImageFormat.Png, 100)
            : image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);

        if (data is null)
        {
            return null;
        }

        return new Result(data.ToArray(), ext);
    }

    // Scale to fit within max, never upscaling.
    private static (int w, int h) Fit(int w, int h, int maxW, int maxH)
    {
        if (w <= maxW && h <= maxH)
        {
            return (w, h);
        }

        var scale = Math.Min((double)maxW / w, (double)maxH / h);
        var tw = Math.Max(1, (int)Math.Round(w * scale));
        var th = Math.Max(1, (int)Math.Round(h * scale));
        return (tw, th);
    }

    // Cheap scan for any pixel with alpha < 255.
    private static bool HasTransparency(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque)
        {
            return false;
        }

        var pixels = bitmap.Pixels; // SKColor[]
        foreach (var px in pixels)
        {
            if (px.Alpha < 255)
            {
                return true;
            }
        }
        return false;
    }
}
