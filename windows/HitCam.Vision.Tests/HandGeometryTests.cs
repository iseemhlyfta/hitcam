using System.Drawing;
using HitCam.Vision.Hands;

namespace HitCam.Vision.Tests;

public sealed class HandGeometryTests
{
    private const float Tolerance = 1e-3f;

    [Fact]
    public void Palm_anchors_follow_the_model_grids()
    {
        var anchors = HandGeometry.PalmAnchors();

        Assert.Equal(2016, anchors.Length);
        Assert.Equal(new PointF(0.5f / 24, 0.5f / 24), anchors[0]);
        Assert.Equal(anchors[0], anchors[1]);
        Assert.Equal(new PointF(1.5f / 24, 0.5f / 24), anchors[2]);
        Assert.Equal(new PointF(0.5f / 24, 1.5f / 24), anchors[48]);
        Assert.Equal(new PointF(0.5f / 12, 0.5f / 12), anchors[1152]);
        Assert.Equal(new PointF(0.5f / 12, 0.5f / 12), anchors[1157]);
        Assert.Equal(new PointF(1.5f / 12, 0.5f / 12), anchors[1158]);
        Assert.Equal(new PointF(11.5f / 12, 11.5f / 12), anchors[^1]);
    }

    [Theory]
    [InlineData(0, -1, 0)]              // fingers up: nothing to turn
    [InlineData(1, 0, MathF.PI / 2)]    // fingers right: turn 90° clockwise
    [InlineData(-1, 0, -MathF.PI / 2)]
    [InlineData(1, -1, MathF.PI / 4)]
    public void Rotation_turns_the_direction_upright(float dx, float dy, float expected)
    {
        Assert.Equal(expected, HandGeometry.Rotation(new PointF(10, 10), new PointF(10 + dx, 10 + dy)), Tolerance);
    }

    [Fact]
    public void Rotation_of_a_hand_pointing_down_is_half_a_turn()
    {
        Assert.Equal(MathF.PI, MathF.Abs(HandGeometry.Rotation(new PointF(0, 0), new PointF(0, 5))), Tolerance);
    }

    [Fact]
    public void Roi_maps_its_corners_and_bounds()
    {
        var upright = new HandRoi(100, 50, 20, 0);
        Assert.Equal(new PointF(90, 40), upright.ToFrame(-0.5f, -0.5f));
        Assert.Equal(new RectangleF(90, 40, 20, 20), upright.Bounds);

        // Turned 90° clockwise: the square's "up" points right in the frame.
        var turned = new HandRoi(100, 50, 20, MathF.PI / 2);
        var up = turned.ToFrame(0, -0.5f);
        Assert.Equal(110, up.X, Tolerance);
        Assert.Equal(50, up.Y, Tolerance);

        var diagonal = new HandRoi(0, 0, 10, MathF.PI / 4).Bounds;
        Assert.Equal(10 * MathF.Sqrt(2), diagonal.Width, Tolerance);
    }

    [Fact]
    public void Palm_roi_is_larger_and_shifted_towards_the_fingers()
    {
        // Upright palm 50 px wide, fingers up.
        var palm = PalmAt(new RectangleF(100, 100, 50, 40), wrist: new PointF(125, 140), middleBase: new PointF(125, 100));
        var roi = HandGeometry.FromPalm(palm);
        Assert.Equal(0, roi.Rotation, Tolerance);
        Assert.Equal(125, roi.CenterX, Tolerance);
        Assert.Equal(120 - 20, roi.CenterY, Tolerance); // half the height up
        Assert.Equal(50 * 2.6f, roi.Size, Tolerance);

        // The same palm with the fingers pointing right: shifted right instead.
        var right = PalmAt(new RectangleF(100, 100, 40, 50), wrist: new PointF(100, 125), middleBase: new PointF(140, 125));
        var turned = HandGeometry.FromPalm(right);
        Assert.Equal(MathF.PI / 2, turned.Rotation, Tolerance);
        Assert.Equal(120 + 25, turned.CenterX, Tolerance);
        Assert.Equal(125, turned.CenterY, Tolerance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.7f)]
    [InlineData(-2.1f)]
    [InlineData(3f)]
    public void Landmark_roi_turns_with_the_hand(float angle)
    {
        var upright = HandGeometry.FromLandmarks(SyntheticHand.Points(new PointF(300, 200), 100, 0));
        var turned = HandGeometry.FromLandmarks(SyntheticHand.Points(new PointF(300, 200), 100, angle));

        Assert.Equal(HandGeometry.NormalizeRadians(upright.Rotation + angle), turned.Rotation, Tolerance);
        Assert.Equal(upright.Size, turned.Size, 0.01f);
        // The center moves with the hand: turned around the synthetic hand's origin.
        var (sin, cos) = MathF.SinCos(angle);
        var dx = upright.CenterX - 300;
        var dy = upright.CenterY - 200;
        Assert.Equal(300 + dx * cos - dy * sin, turned.CenterX, 0.01f);
        Assert.Equal(200 + dx * sin + dy * cos, turned.CenterY, 0.01f);
    }

    [Fact]
    public void Landmark_roi_covers_the_whole_hand()
    {
        var points = SyntheticHand.Points(new PointF(300, 200), 100, 0.4f);
        var roi = HandGeometry.FromLandmarks(points);
        var (sin, cos) = MathF.SinCos(-roi.Rotation);
        foreach (var p in points)
        {
            // In the square's own coordinates, every point is inside with a margin.
            var x = (p.X - roi.CenterX) * cos - (p.Y - roi.CenterY) * sin;
            var y = (p.X - roi.CenterX) * sin + (p.Y - roi.CenterY) * cos;
            Assert.InRange(x, -roi.Size * 0.45f, roi.Size * 0.45f);
            Assert.InRange(y, -roi.Size * 0.45f, roi.Size * 0.45f);
        }
    }

    [Fact]
    public void Weighted_nms_merges_overlapping_palms()
    {
        var a = new Palm(new RectangleF(0, 0, 10, 10), [new PointF(0, 0)], 0.9f);
        var b = new Palm(new RectangleF(1, 0, 10, 10), [new PointF(3, 0)], 0.3f);
        var far = new Palm(new RectangleF(50, 50, 10, 10), [new PointF(55, 55)], 0.6f);

        var merged = HandGeometry.WeightedNms([b, far, a]);

        Assert.Equal(2, merged.Count);
        Assert.Equal(0.9f, merged[0].Score);
        Assert.Equal(0.25f, merged[0].Box.X, Tolerance); // (0 * 0.9 + 1 * 0.3) / 1.2
        Assert.Equal(0.75f, merged[0].Keypoints[0].X, Tolerance);
        Assert.Equal(far.Box, merged[1].Box);
    }

    [Fact]
    public void Palm_sampler_letterboxes_the_frame()
    {
        var sampler = new PalmDetector.Sampler(384, 192);
        Assert.Equal(0.5f, sampler.Scale);
        Assert.Equal(0, sampler.PadX);
        Assert.Equal(48, sampler.PadY);
        Assert.Equal(new PointF(0, 0), sampler.ToFrame(0, 48f / 192));
        Assert.Equal(new PointF(384, 192), sampler.ToFrame(1, 144f / 192));

        // White frame: black bars above and below, white in between.
        var frame = Enumerable.Repeat((byte)255, 384 * 192 * 4).ToArray();
        var input = new float[192 * 192 * 3];
        sampler.Run(frame, 384 * 4, input);
        Assert.Equal(0, input[(10 * 192 + 5) * 3]);
        Assert.Equal(1, input[(96 * 192 + 5) * 3], Tolerance);
        Assert.Equal(0, input[(150 * 192 + 5) * 3]);
    }

    [Fact]
    public void Palm_sampler_keeps_colours_in_rgb_order()
    {
        var frame = new byte[4 * 4 * 4];
        for (var i = 0; i < 16; i++)
        {
            frame[i * 4] = 30;       // blue
            frame[i * 4 + 1] = 60;   // green
            frame[i * 4 + 2] = 90;   // red
        }
        var input = new float[192 * 192 * 3];
        new PalmDetector.Sampler(4, 4).Run(frame, 16, input);
        Assert.Equal(90 / 255f, input[0], Tolerance);
        Assert.Equal(60 / 255f, input[1], Tolerance);
        Assert.Equal(30 / 255f, input[2], Tolerance);
    }

    [Fact]
    public void Palm_decode_places_the_box_through_the_anchor()
    {
        var raw = new float[2016 * 18];
        var scores = Enumerable.Repeat(-10f, 2016).ToArray();
        // Anchor 1152: the first 12×12 cell, center (8, 8) in input pixels.
        scores[1152] = 5;
        var v = raw.AsSpan(1152 * 18, 18);
        v[0] = 8;   // center 16 px
        v[1] = 0;
        v[2] = 20;  // 20 × 10 px
        v[3] = 10;
        v[8] = 4;   // keypoint 2 at (12, 8)
        var palms = PalmDetector.Decode(raw, scores, new PalmDetector.Sampler(192, 192), 0.5f);

        var palm = Assert.Single(palms);
        Assert.Equal(1 / (1 + MathF.Exp(-5)), palm.Score, Tolerance);
        Assert.Equal(6, palm.Box.X, Tolerance);
        Assert.Equal(3, palm.Box.Y, Tolerance);
        Assert.Equal(20, palm.Box.Width, Tolerance);
        Assert.Equal(10, palm.Box.Height, Tolerance);
        Assert.Equal(12, palm.Keypoints[2].X, Tolerance);
        Assert.Equal(8, palm.Keypoints[2].Y, Tolerance);
    }

    [Fact]
    public void Crop_of_an_upright_roi_copies_the_frame()
    {
        const int size = HandLandmarker.InputSize;
        var frame = Gradient(size + 40, size + 20, out var stride);
        var input = new float[size * size * 3];
        // The square exactly over pixels 20..243 × 10..233.
        HandLandmarker.Crop(frame, size + 40, size + 20, stride, new HandRoi(20 + size / 2f, 10 + size / 2f, size, 0), input);

        foreach (var (u, v) in new[] { (0, 0), (100, 7), (223, 223) })
        {
            var o = (v * size + u) * 3;
            Assert.Equal((u + 20) % 256 / 255f, input[o], Tolerance);     // red = x
            Assert.Equal((v + 10) % 256 / 255f, input[o + 1], Tolerance); // green = y
        }
    }

    [Fact]
    public void Crop_turns_with_the_roi_and_is_black_outside_the_frame()
    {
        const int size = HandLandmarker.InputSize;
        var frame = Gradient(size, size, out var stride);
        var input = new float[size * size * 3];
        // Turned 90° clockwise: the input's top row is the frame's right column, read top to bottom.
        HandLandmarker.Crop(frame, size, size, stride, new HandRoi(size / 2f, size / 2f, size, MathF.PI / 2), input);
        var topLeft = 0;
        Assert.Equal((size - 1) / 255f, input[topLeft], 0.01f);   // red = x = 223
        Assert.Equal(0, input[topLeft + 1], 0.01f);               // green = y = 0

        // Far outside the frame: all black.
        HandLandmarker.Crop(frame, size, size, stride, new HandRoi(-1000, -1000, 100, 0), input);
        Assert.All(input, value => Assert.Equal(0, value));
    }

    [Fact]
    public void One_euro_filter_holds_still_and_follows_fast_moves()
    {
        var filter = new OneEuroFilter(0.05f, 80f, 1f);
        Assert.Equal(100, filter.Apply(100, 0));
        // Jitter of ±1 px on a still point at 30 fps: the output moves less than half as much.
        var t = 0.0;
        var output = 0f;
        var previous = 0f;
        for (var i = 1; i <= 60; i++)
        {
            t += 1 / 30.0;
            previous = output;
            output = filter.Apply(100 + (i % 2 == 0 ? 1 : -1), t, 1 / 100f);
        }
        Assert.InRange(output, 99f, 101f);
        Assert.True(MathF.Abs(output - previous) < 1, $"{previous} → {output}");

        // A fast move is followed within a few frames.
        for (var i = 0; i < 5; i++)
        {
            t += 1 / 30.0;
            output = filter.Apply(300, t, 1 / 100f);
        }
        Assert.InRange(output, 280f, 300f);
    }

    private static Palm PalmAt(RectangleF box, PointF wrist, PointF middleBase) =>
        new(box, [wrist, PointF.Empty, middleBase, PointF.Empty, PointF.Empty, PointF.Empty, PointF.Empty], 0.9f);

    /// <summary>BGRA: red = x, green = y (mod 256).</summary>
    private static byte[] Gradient(int width, int height, out int stride)
    {
        stride = width * 4;
        var frame = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                frame[y * stride + x * 4 + 2] = (byte)(x % 256);
                frame[y * stride + x * 4 + 1] = (byte)(y % 256);
                frame[y * stride + x * 4 + 3] = 255;
            }
        }
        return frame;
    }
}

/// <summary>A plausible open hand for tests: 21 points around <c>origin</c>, <c>size</c> px tall, turned clockwise.</summary>
internal static class SyntheticHand
{
    private static readonly (float X, float Y)[] Upright =
    [
        (0, 0.45f),
        (-0.2f, 0.3f), (-0.3f, 0.15f), (-0.38f, 0), (-0.45f, -0.1f),
        (-0.18f, 0), (-0.18f, -0.15f), (-0.18f, -0.28f), (-0.18f, -0.4f),
        (-0.05f, -0.02f), (-0.05f, -0.19f), (-0.05f, -0.33f), (-0.05f, -0.47f),
        (0.08f, 0), (0.08f, -0.15f), (0.08f, -0.28f), (0.08f, -0.4f),
        (0.2f, 0.05f), (0.2f, -0.07f), (0.2f, -0.17f), (0.2f, -0.27f),
    ];

    public static PointF[] Points(PointF origin, float size, float rotation)
    {
        var (sin, cos) = MathF.SinCos(rotation);
        return [.. Upright.Select(p => new PointF(
            origin.X + (p.X * cos - p.Y * sin) * size,
            origin.Y + (p.X * sin + p.Y * cos) * size))];
    }
}
