using System.Drawing;

namespace HitCam.Vision.Framing;

public sealed record FramingOptions
{
    /// <summary>The picture is never magnified more than this (a 1080p source gets soft beyond 2×).</summary>
    public float MaxZoom { get; init; } = 2f;

    /// <summary>The crop is this many face heights tall ("head and shoulders").</summary>
    public float FaceHeights { get; init; } = 3.5f;

    /// <summary>The face centre sits this far down the crop (0 top, 1 bottom): room above the head.</summary>
    public float FaceLine { get; init; } = 0.38f;

    /// <summary>The target moves only when its centre moved this much (share of the crop) ...</summary>
    public float DeadZoneCentre { get; init; } = 0.06f;

    /// <summary>... or its size changed this much: small movements do not make the picture swim.</summary>
    public float DeadZoneSize { get; init; } = 0.12f;

    /// <summary>Speed of the critically damped spring towards the target, rad/s (about 1.5 s to settle at 3).</summary>
    public float Stiffness { get; init; } = 3f;

    /// <summary>Without faces for this long, the picture goes back to the whole frame.</summary>
    public TimeSpan LostTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Auto-framing (like Center Stage): a crop of the frame that follows the faces smoothly. The crop keeps the frame's
/// proportions (the same share of width and height), so the camera picture is never stretched. The target is the
/// head-and-shoulders box of the faces, moved only past a dead zone; the crop follows it with a critically damped
/// spring, so it never overshoots or swings. Coordinates are normalized to the frame. Not thread-safe.
/// </summary>
public sealed class FramingController(FramingOptions? options = null)
{
    private readonly FramingOptions _options = options ?? new FramingOptions();
    // Centre x, centre y and size (share of the frame, the same for width and height): current, velocity, target.
    private Vector3 _crop = Vector3.Full;
    private Vector3 _velocity;
    private Vector3 _target = Vector3.Full;
    private TimeSpan? _last;
    private TimeSpan? _lastFaces;

    /// <summary>The whole frame.</summary>
    public static RectangleF Full { get; } = new(0, 0, 1, 1);

    /// <summary>Back to the whole frame at once (a new stream).</summary>
    public void Reset()
    {
        _crop = _target = Vector3.Full;
        _velocity = default;
        _last = _lastFaces = null;
    }

    /// <summary>The crop for this moment, given the faces found in it (normalized boxes; empty if none).</summary>
    public RectangleF Update(IReadOnlyList<RectangleF> faces, TimeSpan now)
    {
        var dt = _last is { } last ? (float)Math.Clamp((now - last).TotalSeconds, 0, 0.1) : 0f;
        _last = now;

        var valid = faces.Where(f => f.Width > 0 && f.Height > 0 && float.IsFinite(f.X + f.Y + f.Width + f.Height)).ToList();
        if (valid.Count > 0)
        {
            _lastFaces = now;
            var wanted = TargetFor(valid);
            // A dead zone: the face moving a little (talking, breathing) does not move the picture.
            if (MathF.Abs(wanted.X - _target.X) > _options.DeadZoneCentre * _target.Size
                || MathF.Abs(wanted.Y - _target.Y) > _options.DeadZoneCentre * _target.Size
                || MathF.Abs(wanted.Size - _target.Size) > _options.DeadZoneSize * _target.Size)
            {
                _target = wanted;
            }
        }
        else if (_lastFaces is null || now - _lastFaces.Value > _options.LostTimeout)
        {
            _target = Vector3.Full;
        }

        // Critically damped spring: x'' = w²(target - x) - 2w x'.
        var w = _options.Stiffness;
        for (var step = 0; step < 4; step++)
        {
            var h = dt / 4;
            var acceleration = (_target - _crop) * (w * w) - _velocity * (2 * w);
            _velocity += acceleration * h;
            _crop += _velocity * h;
        }
        _crop = Clamp(_crop);
        return new RectangleF(_crop.X - _crop.Size / 2, _crop.Y - _crop.Size / 2, _crop.Size, _crop.Size);
    }

    private Vector3 TargetFor(List<RectangleF> faces)
    {
        var left = faces.Min(f => f.Left);
        var right = faces.Max(f => f.Right);
        var top = faces.Min(f => f.Top);
        var bottom = faces.Max(f => f.Bottom);
        var faceHeight = faces.Max(f => f.Height);
        // Tall enough for head and shoulders of the largest face, wide and tall enough for all of them.
        var size = Math.Max(faceHeight * _options.FaceHeights, Math.Max((right - left) * 1.4f, bottom - top + faceHeight * 2.5f));
        size = Math.Clamp(size, 1 / _options.MaxZoom, 1);
        var faceCentreY = (top + bottom) / 2;
        return Clamp(new Vector3((left + right) / 2, faceCentreY + (0.5f - _options.FaceLine) * size, size));
    }

    /// <summary>Inside the frame: a crop larger than the frame is the frame, one past an edge slides back in.</summary>
    private Vector3 Clamp(Vector3 crop)
    {
        var size = Math.Clamp(crop.Size, 1 / _options.MaxZoom, 1);
        var half = size / 2;
        return new Vector3(Math.Clamp(crop.X, half, 1 - half), Math.Clamp(crop.Y, half, 1 - half), size);
    }

    private readonly record struct Vector3(float X, float Y, float Size)
    {
        public static Vector3 Full => new(0.5f, 0.5f, 1);

        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Size + b.Size);

        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Size - b.Size);

        public static Vector3 operator *(Vector3 a, float k) => new(a.X * k, a.Y * k, a.Size * k);
    }
}
