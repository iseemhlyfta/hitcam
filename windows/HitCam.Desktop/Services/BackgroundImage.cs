using SkiaSharp;

namespace HitCam.Desktop.Services;

/// <summary>Reads a picture for the background (JPEG, PNG, WebP, BMP, GIF) as packed BGRA, turned upright.</summary>
public static class BackgroundImage
{
    /// <summary>Larger pictures are scaled down to fit this (a camera frame is at most 1920 on a side).</summary>
    public const int MaxWidth = 3840;
    public const int MaxHeight = 2160;

    /// <summary>
    /// The picture as BGRA rows of width * 4 bytes; null if the file is missing, not a picture or too large to read.
    /// Photos from phones are turned as their EXIF orientation says. Takes a while for big photos: not on the UI thread.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height)? Load(string path)
    {
        try
        {
            // Read first, retrying briefly: a file just saved is often held for a moment (antivirus, sync).
            using var data = SKData.CreateCopy(ReadAll(path));
            using var codec = SKCodec.Create(data);
            if (codec is null)
                return null;
            var origin = codec.EncodedOrigin;
            var turned = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            var (fullWidth, fullHeight) = turned ? (codec.Info.Height, codec.Info.Width) : (codec.Info.Width, codec.Info.Height);
            if (fullWidth <= 0 || fullHeight <= 0)
                return null;
            // Decoding big JPEGs at a fraction of their size is much faster and never needs the whole photo in memory.
            var fit = Math.Min(1f, Math.Min((float)MaxWidth / fullWidth, (float)MaxHeight / fullHeight));
            var size = codec.GetScaledDimensions(fit);
            using var decoded = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                return null;
            using var upright = Orient(decoded, origin);

            var scale = Math.Min(1.0, Math.Min((double)MaxWidth / upright.Width, (double)MaxHeight / upright.Height));
            var width = Math.Max(1, (int)(upright.Width * scale));
            var height = Math.Max(1, (int)(upright.Height * scale));
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            // Transparent parts of a PNG become black: the camera has no alpha.
            bitmap.Erase(SKColors.Black);
            using (var canvas = new SKCanvas(bitmap))
            {
                using var image = SKImage.FromBitmap(upright);
                canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }
            var pixels = new byte[width * height * 4];
            var source = bitmap.GetPixelSpan();
            for (var y = 0; y < height; y++)
                source.Slice(y * bitmap.RowBytes, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
            return (pixels, width, height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static byte[] ReadAll(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (IOException) when (attempt < 5 && File.Exists(path))
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>The bitmap turned upright for its EXIF origin (a new bitmap, or a copy if nothing to turn).</summary>
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        var turned = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(new SKImageInfo(turned ? source.Height : source.Width, turned ? source.Width : source.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);
        float w = result.Width, h = result.Height;
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:     // mirrored
                canvas.Translate(w, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:  // upside down
                canvas.Translate(w, h);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:   // mirrored upside down
                canvas.Translate(0, h);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:      // mirrored and turned: x' = y, y' = x
                canvas.SetMatrix(new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1));
                break;
            case SKEncodedOrigin.RightTop:     // turned: the usual portrait photo from a phone
                canvas.Translate(w, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:  // mirrored and turned the other way: x' = w - y, y' = h - x
                canvas.SetMatrix(new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1));
                break;
            case SKEncodedOrigin.LeftBottom:   // turned the other way
                canvas.Translate(0, h);
                canvas.RotateDegrees(270);
                break;
        }
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }
}
