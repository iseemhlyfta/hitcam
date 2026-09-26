namespace HitCam.Desktop.Services;

/// <summary>
/// Keeps faces hidden in the camera while face analysis cannot say where they are. The DLL drops face regions that are
/// not refreshed within a second, so faces used to show whenever analysis fell behind: models still loading after
/// connecting, a new stream, a failure, or another model holding ONNX Runtime for seconds. Until the first result the
/// whole picture is covered; after that the last regions are sent again until a newer result comes. Thread-safe.
/// </summary>
public sealed class FaceGuard(TimeProvider? time = null)
{
    /// <summary>The last regions are sent again when no newer result came for this long (the DLL keeps them a second).</summary>
    public static readonly TimeSpan Refresh = TimeSpan.FromMilliseconds(400);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    // Null: no result since the last Reset.
    private HitCamFaceRegion[]? _last;
    private long _sentAt;

    /// <summary>No result yet: the whole picture is covered (in the preview too).</summary>
    public bool CoversAll
    {
        get
        {
            lock (_lock)
                return _last is null;
        }
    }

    /// <summary>Analysis starts over (connected, a new stream, models loading or failed): cover everything until it catches up.</summary>
    public void Reset()
    {
        lock (_lock)
            _last = null;
    }

    /// <summary>A result's regions, about to go to the camera.</summary>
    public void Sent(HitCamFaceRegion[] regions)
    {
        lock (_lock)
        {
            _last = regions;
            _sentAt = _time.GetTimestamp();
        }
    }

    /// <summary>
    /// What to send to the camera now, or null for nothing: the whole picture before the first result, the last regions
    /// again once they are <see cref="Refresh"/> old.
    /// </summary>
    public HitCamFaceRegion[]? Due(FaceSettings settings)
    {
        lock (_lock)
        {
            if (_last is null)
                return [CoverAll(settings)];
            if (_last.Length == 0 || _time.GetElapsedTime(_sentAt) < Refresh)
                return null;
            _sentAt = _time.GetTimestamp();
            return _last;
        }
    }

    /// <summary>The effect over the whole picture.</summary>
    public static HitCamFaceRegion CoverAll(FaceSettings settings) => new()
    {
        Left = 0,
        Top = 0,
        Right = 1,
        Bottom = 1,
        Effect = (int)settings.Effect,
        Strength = settings.Strength / 100f,
        Rgb = HandSettings.ParseColor(settings.FillColor),
    };
}
