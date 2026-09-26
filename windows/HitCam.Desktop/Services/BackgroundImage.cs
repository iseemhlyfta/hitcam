using SkiaSharp;

namespace HitCam.Desktop.Services;

/// <summary>Reads a picture for the background (JPEG, PNG, WebP, BMP, GIF) as packed BGRA.</summary>
public static class BackgroundImage
{
    /// <summary>Larger pictures are scaled down to fit this (a camera frame is at most 1920 on a side).</summary>
    public const int MaxWidth = 3840;
    public const int MaxHeight = 2160;

    /// <summary>The picture as BGRA rows of width * 4 bytes; null if the file is missing or not a picture.</summary>
    public static (byte[] Pixels, int Width, int Height)? Load(string path)
    {
        try
        {
            using var decoded = SKBitmap.Decode(path);
            if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
                return null;
            var scale = Math.Min(1.0, Math.Min((double)MaxWidth / decoded.Width, (double)MaxHeight / decoded.Height));
            var width = Math.Max(1, (int)(decoded.Width * scale));
            var height = Math.Max(1, (int)(decoded.Height * scale));
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var bitmap = new SKBitmap(info);
            if (!decoded.ScalePixels(bitmap, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
                return null;
            var pixels = new byte[width * height * 4];
            var source = bitmap.GetPixelSpan();
            for (var y = 0; y < height; y++)
                source.Slice(y * bitmap.RowBytes, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
            return (pixels, width, height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
