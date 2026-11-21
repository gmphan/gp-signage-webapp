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
        // Buffer the input so we can (a) read EXIF via SKCodec and (b) decode.
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        buffer.Position = 0;

        // Read the encoded origin (EXIF orientation) WITHOUT auto-rotating, then decode
        // the raw pixels. We apply the orientation ourselves below so the result is
        // deterministic across SkiaSharp versions (Decode sometimes does/doesn't apply it,
        // which caused uploads to come out rotated to the right).
        SKEncodedOrigin origin = SKEncodedOrigin.TopLeft;
        SKBitmap? decoded = null;

        using (var codec = SKCodec.Create(buffer))
        {
            if (codec is not null)
            {
                origin = codec.EncodedOrigin;
            }

            buffer.Position = 0;
            decoded = SKBitmap.Decode(buffer);
        }

        using var original = decoded;
        if (original is null || original.Width <= 0 || original.Height <= 0)
        {
            return null;
        }

        // Apply EXIF orientation (rotate/flip) to an upright bitmap. When the image is
        // already TopLeft (upright), ApplyOrientation returns the SAME instance — so only
        // dispose the upright bitmap when it is distinct, to avoid double-disposing.
        var upright = ApplyOrientation(original, origin);
        var uprightIsDistinct = !ReferenceEquals(upright, original);
        try
        {
            if (upright.Width <= 0 || upright.Height <= 0)
            {
                return null;
            }

        var (targetW, targetH) = Fit(upright.Width, upright.Height, MaxWidth, MaxHeight);

            using var resized = new SKBitmap(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(resized))
            {
                canvas.Clear(SKColors.Transparent);
                using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
                canvas.DrawBitmap(upright, new SKRect(0, 0, targetW, targetH), paint);
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
        finally
        {
            if (uprightIsDistinct)
            {
                upright.Dispose();
            }
        }
    }

    // Produce an upright bitmap by applying the EXIF orientation. For TopLeft (the
    // common, already-upright case) this is a no-op and we return the input as-is.
    // Uses canvas Rotate/Scale/Translate (the canonical, well-tested EXIF transform) —
    // a hand-built SKMatrix skew mapping proved unreliable (produced blank pixels).
    private static SKBitmap ApplyOrientation(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return src;
        }

        int w = src.Width;
        int h = src.Height;

        // Quarter-turn orientations swap the output width/height.
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                           or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int dstW = swap ? h : w;
        int dstH = swap ? w : h;

        var dst = new SKBitmap(dstW, dstH, src.ColorType, src.AlphaType);
        using (var canvas = new SKCanvas(dst))
        {
            canvas.Clear(SKColors.Transparent);

            switch (origin)
            {
                case SKEncodedOrigin.TopRight:      // 2: horizontal flip
                    canvas.Translate(dstW, 0); canvas.Scale(-1, 1); break;
                case SKEncodedOrigin.BottomRight:   // 3: rotate 180°
                    canvas.Translate(dstW, dstH); canvas.Scale(-1, -1); break;
                case SKEncodedOrigin.BottomLeft:    // 4: vertical flip
                    canvas.Translate(0, dstH); canvas.Scale(1, -1); break;
                case SKEncodedOrigin.LeftTop:       // 5: transpose (flip + rotate 270° CW)
                    canvas.RotateDegrees(-90); canvas.Scale(-1, 1); canvas.Translate(-w, 0); break;
                case SKEncodedOrigin.RightTop:      // 6: rotate 90° CW
                    canvas.Translate(dstW, 0); canvas.RotateDegrees(90); break;
                case SKEncodedOrigin.RightBottom:   // 7: transverse (flip + rotate 90° CW)
                    canvas.Translate(dstW, dstH); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
                case SKEncodedOrigin.LeftBottom:    // 8: rotate 270° CW (90° CCW)
                    canvas.Translate(0, dstH); canvas.RotateDegrees(-90); break;
                default:
                    break; // identity
            }

            canvas.DrawBitmap(src, 0, 0);
        }

        return dst;
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
