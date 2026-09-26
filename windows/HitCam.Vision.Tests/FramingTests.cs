using System.Drawing;
using HitCam.Vision.Framing;

namespace HitCam.Vision.Tests;

public sealed class FramingControllerTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    // A face 0.16 of the frame tall, a little left of centre and high up.
    private static readonly RectangleF Face = new(0.35f, 0.25f, 0.09f, 0.16f);

    /// <summary>Runs the controller at 30 fps from <paramref name="from"/> to <paramref name="to"/> seconds; the crops.</summary>
    private static List<RectangleF> Run(FramingController controller, Func<double, IReadOnlyList<RectangleF>> faces, double from, double to)
    {
        var crops = new List<RectangleF>();
        for (var t = from; t <= to + 1e-9; t += 1.0 / 30)
            crops.Add(controller.Update(faces(t), At(t)));
        return crops;
    }

    [Fact]
    public void Without_faces_it_is_the_whole_frame()
    {
        var crop = Run(new FramingController(), _ => [], 0, 3)[^1];
        Assert.Equal(FramingController.Full, crop);
    }

    [Fact]
    public void A_face_is_framed_head_and_shoulders_and_the_crop_keeps_the_proportions()
    {
        var crop = Run(new FramingController(), _ => [Face], 0, 4)[^1];

        // 3.5 face heights tall, the same share of the width, the face centre 38% down.
        Assert.Equal(0.56f, crop.Height, 0.01f);
        Assert.Equal(crop.Width, crop.Height, 1e-5f);
        Assert.Equal(Face.X + Face.Width / 2, crop.X + crop.Width / 2, 0.01f);
        Assert.Equal(0.38f, (Face.Y + Face.Height / 2 - crop.Y) / crop.Height, 0.02f);
    }

    [Fact]
    public void The_crop_never_overshoots_on_its_way()
    {
        var sizes = Run(new FramingController(), _ => [Face], 0, 4).Select(c => c.Height).ToList();
        for (var i = 1; i < sizes.Count; i++)
            Assert.True(sizes[i] <= sizes[i - 1] + 1e-6f, $"grew at frame {i}: {sizes[i - 1]} -> {sizes[i]}");
        Assert.True(sizes[^1] >= 0.56f - 0.01f);
    }

    [Fact]
    public void A_face_that_moves_a_little_does_not_move_the_picture()
    {
        var controller = new FramingController();
        Run(controller, _ => [Face], 0, 4);
        var settled = controller.Update([Face], At(4.05));

        // Talking, breathing: ±1% of the frame.
        var jitter = Run(controller, t => [Face with { X = Face.X + 0.01f * MathF.Sin((float)t * 7), Y = Face.Y + 0.008f * MathF.Cos((float)t * 5) }], 4.1, 6);

        Assert.All(jitter, c => Assert.Equal(settled.X, c.X, 1e-3f));
    }

    [Fact]
    public void A_face_that_moves_far_is_followed()
    {
        var controller = new FramingController();
        Run(controller, _ => [Face], 0, 4);
        var moved = Face with { X = 0.6f };
        var crop = Run(controller, _ => [moved], 4, 8)[^1];
        Assert.Equal(moved.X + moved.Width / 2, crop.X + crop.Width / 2, 0.01f);
    }

    [Fact]
    public void A_small_face_is_magnified_no_more_than_the_limit()
    {
        var tiny = new RectangleF(0.5f, 0.4f, 0.02f, 0.03f);
        var crop = Run(new FramingController(new FramingOptions { MaxZoom = 2 }), _ => [tiny], 0, 4)[^1];
        Assert.Equal(0.5f, crop.Height, 1e-3f);
    }

    [Fact]
    public void A_face_at_the_edge_keeps_the_crop_inside_the_frame()
    {
        var edge = new RectangleF(0.0f, 0.02f, 0.07f, 0.125f);
        var crop = Run(new FramingController(), _ => [edge], 0, 4)[^1];
        Assert.Equal(0f, crop.X, 1e-4f);
        Assert.Equal(0f, crop.Y, 1e-4f);
        Assert.True(crop.Right <= 1 && crop.Bottom <= 1);
    }

    [Fact]
    public void Everyone_fits_when_there_are_several_faces()
    {
        RectangleF left = new(0.15f, 0.3f, 0.07f, 0.12f), right = new(0.7f, 0.35f, 0.07f, 0.12f);
        var crop = Run(new FramingController(), _ => [left, right], 0, 4)[^1];
        Assert.True(crop.Contains(left) && crop.Contains(right), crop.ToString());
    }

    [Fact]
    public void When_the_faces_are_gone_it_waits_then_goes_back_to_the_whole_frame()
    {
        var controller = new FramingController();
        Run(controller, _ => [Face], 0, 4);

        var brief = Run(controller, _ => [], 4, 5)[^1];      // turned away for a second: stays
        Assert.True(brief.Height < 0.7f);
        var gone = Run(controller, _ => [], 5, 9)[^1];
        Assert.Equal(1f, gone.Height, 1e-3f);
    }

    [Fact]
    public void Garbage_boxes_are_ignored()
    {
        var crop = Run(new FramingController(), _ => [new RectangleF(float.NaN, 0.2f, 0.1f, 0.1f), new RectangleF(0.3f, 0.3f, 0, 0)], 0, 3)[^1];
        Assert.Equal(FramingController.Full, crop);
    }
}
