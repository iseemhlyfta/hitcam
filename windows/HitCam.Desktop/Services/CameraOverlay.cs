using System.Runtime.InteropServices;
using HitCam.Vision;
using SkiaSharp;

namespace HitCam.Desktop.Services;

/// <summary>
/// Matches <c>HitCamOverlayBox</c> in HitCamVCam.dll (packed to 4 bytes: 36 bytes on x64). Coordinates are
/// normalized to the output frame; <see cref="Label"/> points to <c>LabelWidth × LabelHeight</c> bytes, the 8-bit
/// alpha of white text on the tab the DLL fills with <see cref="Rgb"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct HitCamOverlayBox
{
    public float Left;
    public float Top;
    public float Right;
    public float Bottom;
    /// <summary>0xRRGGBB.</summary>
    public uint Rgb;
    public int LabelWidth;
    public int LabelHeight;
    public IntPtr Label;
}

/// <summary>A rendered label: row-major alpha, <see cref="Width"/> bytes per row, no padding.</summary>
public sealed record LabelBitmap(int Width, int Height, byte[] Alpha);

/// <summary>
/// Renders label tabs ("person 92%") as the alpha of white text, sized for the picture: the text is about 2.4% of the
/// frame height, so labels look the same in 720p and 4K. Results are cached per text and size. Not thread-safe.
/// </summary>
public sealed class LabelRenderer : IDisposable
{
    private const int MaxCached = 512;
    private readonly Dictionary<(string Text, int TextSize), LabelBitmap> _cache = [];
    private readonly SKTypeface _typeface =
        SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
        ?? SKTypeface.Default;

    /// <summary>Text size in pixels for a frame <paramref name="frameHeight"/> pixels high.</summary>
    public static int TextSizeFor(int frameHeight) => Math.Clamp((int)MathF.Round(frameHeight * 0.024f), 10, 96);

    public int CachedCount => _cache.Count;

    public LabelBitmap Render(string text, int frameHeight)
    {
        var textSize = TextSizeFor(frameHeight);
        if (_cache.TryGetValue((text, textSize), out var cached))
            return cached;
        if (_cache.Count >= MaxCached)
            _cache.Clear();

        using var font = new SKFont(_typeface, textSize) { Edging = SKFontEdging.Antialias, Subpixel = true };
        var metrics = font.Metrics;
        var padding = Math.Max(2, (int)MathF.Round(textSize * 0.35f));
        var width = Math.Max(1, (int)MathF.Ceiling(font.MeasureText(text)) + padding * 2);
        var textHeight = (int)MathF.Ceiling(metrics.Descent - metrics.Ascent);
        var height = textHeight + Math.Max(2, (int)MathF.Round(textSize * 0.3f));

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.White, IsAntialias = true })
        {
            var baseline = (height - textHeight) / 2f - metrics.Ascent;
            canvas.DrawText(text, padding, baseline, SKTextAlign.Left, font, paint);
        }

        var alpha = new byte[width * height];
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
            pixels.Slice(y * bitmap.RowBytes, width).CopyTo(alpha.AsSpan(y * width, width));

        var label = new LabelBitmap(width, height, alpha);
        _cache[(text, textSize)] = label;
        return label;
    }

    public void Dispose()
    {
        _cache.Clear();
        if (!ReferenceEquals(_typeface, SKTypeface.Default))
            _typeface.Dispose();
    }
}

/// <summary>
/// Draws the tracked boxes into the "HitCam" camera picture (burn-in) through HitCam_BridgeSetOverlay. Results come
/// from the analysis thread, switching on and off from the UI thread; a lock keeps a late result from bringing boxes
/// back after they were cleared.
/// </summary>
public sealed class CameraOverlay : IDisposable
{
    /// <summary>Hands the boxes to the DLL; the label pointers are valid only during the call.</summary>
    public delegate void Sink(ReadOnlySpan<HitCamOverlayBox> boxes);

    private readonly Sink _sink;
    private readonly LabelRenderer _labels = new();
    private readonly Lock _lock = new();
    private bool _enabled;
    private bool _hasBoxes;
    private int _frameHeight = 1080;

    public CameraOverlay(Sink sink) => _sink = sink;

    /// <summary>Height of the camera picture in pixels; sets the label size.</summary>
    public int FrameHeight
    {
        get => Volatile.Read(ref _frameHeight);
        set => Volatile.Write(ref _frameHeight, Math.Max(value, 1));
    }

    public bool IsEnabled
    {
        get
        {
            lock (_lock)
                return _enabled;
        }
    }

    /// <summary>Turning it off removes the boxes from the picture right away.</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            if (_enabled && !enabled)
                ClearLocked();
            _enabled = enabled;
        }
    }

    /// <summary>Removes the boxes (disconnect, analysis stopped); stays enabled.</summary>
    public void Clear()
    {
        lock (_lock)
            ClearLocked();
    }

    public void Show(IReadOnlyList<Track> tracks)
    {
        lock (_lock)
        {
            if (!_enabled)
                return;
            if (tracks.Count == 0)
            {
                ClearLocked(always: false);
                return;
            }

            var boxes = new HitCamOverlayBox[tracks.Count];
            var handles = new GCHandle[tracks.Count];
            var frameHeight = FrameHeight;
            try
            {
                for (var i = 0; i < tracks.Count; i++)
                {
                    var track = tracks[i];
                    var label = _labels.Render(track.Label, frameHeight);
                    handles[i] = GCHandle.Alloc(label.Alpha, GCHandleType.Pinned);
                    boxes[i] = new HitCamOverlayBox
                    {
                        Left = track.Box.Left,
                        Top = track.Box.Top,
                        Right = track.Box.Right,
                        Bottom = track.Box.Bottom,
                        Rgb = track.Rgb,
                        LabelWidth = label.Width,
                        LabelHeight = label.Height,
                        Label = handles[i].AddrOfPinnedObject(),
                    };
                }
                _sink(boxes);
                _hasBoxes = true;
            }
            finally
            {
                foreach (var handle in handles)
                {
                    if (handle.IsAllocated)
                        handle.Free();
                }
            }
        }
    }

    private void ClearLocked(bool always = true)
    {
        // Results without objects clear only once in a row; switching off or disconnecting always does.
        if (!always && !_hasBoxes)
            return;
        _hasBoxes = false;
        _sink([]);
    }

    public void Dispose()
    {
        lock (_lock)
            _labels.Dispose();
    }
}
