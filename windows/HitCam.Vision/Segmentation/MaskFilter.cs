using System.Drawing;

namespace HitCam.Vision.Segmentation;

/// <summary>
/// Steadies the raw masks of a segmentation model over time: where a pixel barely changes (model noise at the edges),
/// it moves only part of the way to the new value; where it changes a lot (someone moved), it follows at once, so the
/// person is never cut off. Gives the mask as bytes 0..255 (a new array each time: it goes to another thread) and the
/// box around the person. Not thread-safe.
/// </summary>
public sealed class MaskFilter(int width, int height)
{
    private readonly float[] _state = new float[width * height];
    private bool _primed;

    /// <summary>Share of the new value taken where the mask changed no more than <see cref="NoiseChange"/> (0..1).</summary>
    public float MinFollow { get; init; } = 0.3f;

    /// <summary>Changes up to this (0..1) are taken for model noise.</summary>
    public float NoiseChange { get; init; } = 0.15f;

    /// <summary>A change this large (0..1) is followed completely.</summary>
    public float FullFollowChange { get; init; } = 0.5f;

    public int Width => width;

    public int Height => height;

    /// <summary>Forgets the history (a new stream: the old picture means nothing).</summary>
    public void Reset() => _primed = false;

    /// <param name="raw">Person probability per pixel, 0..1, row by row.</param>
    /// <returns>The steadied mask and the box around the person (normalized; null if nobody is in the frame).</returns>
    public (byte[] Mask, RectangleF? Person) Apply(ReadOnlySpan<float> raw)
    {
        if (raw.Length < _state.Length)
            throw new ArgumentException("The mask is too small.", nameof(raw));
        var mask = new byte[_state.Length];
        int left = width, top = height, right = -1, bottom = -1, count = 0;
        for (var i = 0; i < _state.Length; i++)
        {
            var value = raw[i];
            value = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
            if (_primed)
            {
                var change = MathF.Abs(value - _state[i]);
                var follow = MinFollow + (1 - MinFollow) * Math.Clamp((change - NoiseChange) / (FullFollowChange - NoiseChange), 0f, 1f);
                value = _state[i] + (value - _state[i]) * follow;
            }
            _state[i] = value;
            mask[i] = (byte)(value * 255 + 0.5f);
            if (value >= 0.5f)
            {
                var x = i % width;
                var y = i / width;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
                count++;
            }
        }
        _primed = true;
        // A few stray pixels are not a person.
        RectangleF? person = count * 200 >= _state.Length
            ? new RectangleF((float)left / width, (float)top / height, (float)(right + 1 - left) / width, (float)(bottom + 1 - top) / height)
            : null;
        return (mask, person);
    }
}
