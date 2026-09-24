using System.Drawing;

namespace HitCam.Vision.Hands;

/// <summary>
/// A square part of the frame, turned so that the fingers point up: what the landmark model looks at. Pixels of the
/// frame (continuous: pixel <c>i</c> covers <c>i..i+1</c>). <see cref="Rotation"/> is clockwise in radians, as in
/// MediaPipe.
/// </summary>
public readonly record struct HandRoi(float CenterX, float CenterY, float Size, float Rotation)
{
    /// <summary>
    /// A point of the square (<paramref name="u"/>, <paramref name="v"/> from -0.5 to 0.5, <c>v</c> down) in the frame.
    /// </summary>
    public PointF ToFrame(float u, float v)
    {
        var (sin, cos) = MathF.SinCos(Rotation);
        var x = u * Size;
        var y = v * Size;
        return new PointF(CenterX + x * cos - y * sin, CenterY + x * sin + y * cos);
    }

    /// <summary>The upright box around the turned square.</summary>
    public RectangleF Bounds
    {
        get
        {
            var (sin, cos) = MathF.SinCos(Rotation);
            var half = Size / 2 * (MathF.Abs(cos) + MathF.Abs(sin));
            return new RectangleF(CenterX - half, CenterY - half, half * 2, half * 2);
        }
    }
}

/// <summary>
/// A palm found by the palm detector, in pixels of the frame. <see cref="Keypoints"/>: 0 the wrist, 1 the index
/// finger's base, 2 the middle finger's base, 3 the ring finger's base, 4 the little finger's base, 5 and 6 the thumb.
/// </summary>
public sealed record Palm(RectangleF Box, IReadOnlyList<PointF> Keypoints, float Score);

/// <summary>
/// The geometry of MediaPipe Hands: palm detector anchors, the crop for the landmark model from a palm or from the
/// last landmarks, and weighted NMS.
/// </summary>
public static class HandGeometry
{
    /// <summary>Landmarks of the palm and finger bases, which barely move when the fingers bend.</summary>
    private static readonly int[] RoiLandmarks = [0, 1, 2, 3, 5, 6, 9, 10, 13, 14, 17, 18];

    public static float NormalizeRadians(float angle) =>
        angle - 2 * MathF.PI * MathF.Floor((angle + MathF.PI) / (2 * MathF.PI));

    /// <summary>How far to turn the picture clockwise so that <paramref name="to"/> is straight above <paramref name="from"/>.</summary>
    public static float Rotation(PointF from, PointF to) =>
        NormalizeRadians(MathF.PI / 2 - MathF.Atan2(-(to.Y - from.Y), to.X - from.X));

    /// <summary>
    /// The crop for a new hand: the palm box made square, 2.6 times larger and shifted towards the fingers, turned by
    /// the wrist → middle finger direction.
    /// </summary>
    public static HandRoi FromPalm(Palm palm)
    {
        var box = palm.Box;
        var rotation = Rotation(palm.Keypoints[0], palm.Keypoints[2]);
        return Transform(box.X + box.Width / 2, box.Y + box.Height / 2, box.Width, box.Height, rotation, 2.6f, -0.5f);
    }

    /// <summary>
    /// The crop for the next frame from this frame's landmarks (21 points in pixels): the box around the palm and the
    /// finger bases in the hand's own orientation, twice as large, so the fingers fit however they bend.
    /// </summary>
    public static HandRoi FromLandmarks(ReadOnlySpan<PointF> landmarks)
    {
        if (landmarks.Length < 21)
            throw new ArgumentException("21 landmarks expected.", nameof(landmarks));
        var rotation = Rotation(landmarks[0], landmarks[9]);

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var i in RoiLandmarks)
        {
            minX = Math.Min(minX, landmarks[i].X);
            maxX = Math.Max(maxX, landmarks[i].X);
            minY = Math.Min(minY, landmarks[i].Y);
            maxY = Math.Max(maxY, landmarks[i].Y);
        }
        var axisX = (minX + maxX) / 2;
        var axisY = (minY + maxY) / 2;

        // The same points turned upright around that center.
        var (sin, cos) = MathF.SinCos(rotation);
        minX = minY = float.MaxValue;
        maxX = maxY = float.MinValue;
        foreach (var i in RoiLandmarks)
        {
            var dx = landmarks[i].X - axisX;
            var dy = landmarks[i].Y - axisY;
            var x = dx * cos + dy * sin;
            var y = -dx * sin + dy * cos;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }
        var localX = (minX + maxX) / 2;
        var localY = (minY + maxY) / 2;
        var centerX = axisX + localX * cos - localY * sin;
        var centerY = axisY + localX * sin + localY * cos;
        return Transform(centerX, centerY, maxX - minX, maxY - minY, rotation, 2f, -0.1f);
    }

    /// <summary>MediaPipe's RectTransformation: shift along the turned axes, make square (long side), scale.</summary>
    private static HandRoi Transform(float centerX, float centerY, float width, float height, float rotation, float scale, float shiftY)
    {
        var (sin, cos) = MathF.SinCos(rotation);
        centerX += -height * shiftY * sin;
        centerY += height * shiftY * cos;
        return new HandRoi(centerX, centerY, Math.Max(width, height) * scale, rotation);
    }

    /// <summary>
    /// Anchor centers of the 192×192 palm detector (x, y in 0..1), in the model's output order: a 24×24 grid with 2
    /// anchors per cell, then a 12×12 grid with 6. 2016 anchors.
    /// </summary>
    public static PointF[] PalmAnchors()
    {
        var anchors = new List<PointF>(2016);
        foreach (var (grid, perCell) in new[] { (24, 2), (12, 6) })
        {
            for (var y = 0; y < grid; y++)
            {
                for (var x = 0; x < grid; x++)
                {
                    for (var a = 0; a < perCell; a++)
                        anchors.Add(new PointF((x + 0.5f) / grid, (y + 0.5f) / grid));
                }
            }
        }
        return [.. anchors];
    }

    /// <summary>
    /// Weighted NMS, as MediaPipe does for palms: overlapping detections (IoU above <paramref name="iou"/>) are merged
    /// into one, their boxes and keypoints averaged by score. Keeps the best score. Highest score first.
    /// </summary>
    public static List<Palm> WeightedNms(IEnumerable<Palm> palms, float iou = 0.3f)
    {
        var remaining = palms.OrderByDescending(p => p.Score).ToList();
        var result = new List<Palm>();
        while (remaining.Count > 0)
        {
            var best = remaining[0];
            var group = remaining.Where(p => Geometry.Iou(best.Box, p.Box) > iou).ToList();
            if (group.Count == 0)
                group.Add(best);
            remaining.RemoveAll(group.Contains);

            var total = group.Sum(p => p.Score);
            float x = 0, y = 0, w = 0, h = 0;
            var keypoints = new PointF[best.Keypoints.Count];
            foreach (var p in group)
            {
                var weight = p.Score / total;
                x += p.Box.X * weight;
                y += p.Box.Y * weight;
                w += p.Box.Width * weight;
                h += p.Box.Height * weight;
                for (var k = 0; k < keypoints.Length; k++)
                    keypoints[k] = new PointF(keypoints[k].X + p.Keypoints[k].X * weight, keypoints[k].Y + p.Keypoints[k].Y * weight);
            }
            result.Add(new Palm(new RectangleF(x, y, w, h), keypoints, best.Score));
        }
        return result;
    }
}
