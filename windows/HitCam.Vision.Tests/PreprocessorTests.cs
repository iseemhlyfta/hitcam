namespace HitCam.Vision.Tests;

public sealed class PreprocessorTests
{
    private static readonly float[] Zero = [0, 0, 0];
    private static readonly float[] One = [1, 1, 1];

    /// <summary>A BGRA frame from RGB pixels, row by row.</summary>
    private static byte[] Bgra(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel, int stride = 0)
    {
        stride = Math.Max(stride, width * 4);
        var data = new byte[stride * height];
        Array.Fill(data, (byte)0xEE); // row padding must be ignored
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                var p = y * stride + x * 4;
                data[p] = b;
                data[p + 1] = g;
                data[p + 2] = r;
                data[p + 3] = 255;
            }
        }
        return data;
    }

    private static float[] Run(byte[] bgra, int width, int height, int inputWidth, int inputHeight,
        float[]? mean = null, float[]? std = null, int stride = 0)
    {
        var output = new float[3 * inputWidth * inputHeight];
        new Preprocessor().Run(bgra, width, height, stride == 0 ? width * 4 : stride, output, inputWidth, inputHeight, mean ?? Zero, std ?? One);
        return output;
    }

    [Fact]
    public void Bgra_becomes_planar_rgb_in_row_order()
    {
        // 2×2 at the same size: every pixel different.
        (byte, byte, byte)[] pixels = [(10, 20, 30), (40, 50, 60), (70, 80, 90), (100, 110, 120)];
        var frame = Bgra(2, 2, (x, y) => pixels[y * 2 + x]);

        var input = Run(frame, 2, 2, 2, 2);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(pixels[i].Item1 / 255f, input[i], 5);      // R plane
            Assert.Equal(pixels[i].Item2 / 255f, input[4 + i], 5);  // G plane
            Assert.Equal(pixels[i].Item3 / 255f, input[8 + i], 5);  // B plane
        }
    }

    [Fact]
    public void Mean_and_std_are_applied_per_channel()
    {
        var frame = Bgra(1, 1, (_, _) => (255, 0, 51));

        var input = Run(frame, 1, 1, 1, 1, mean: [0.485f, 0.456f, 0.406f], std: [0.229f, 0.224f, 0.225f]);

        Assert.Equal((1 - 0.485f) / 0.229f, input[0], 4);
        Assert.Equal((0 - 0.456f) / 0.224f, input[1], 4);
        Assert.Equal((0.2f - 0.406f) / 0.225f, input[2], 4);
    }

    [Fact]
    public void The_whole_frame_is_stretched_without_letterbox()
    {
        // 16:9 into a square: a uniform frame stays uniform everywhere, no padding rows.
        var frame = Bgra(32, 18, (_, _) => (200, 100, 50));

        var input = Run(frame, 32, 18, 8, 8);

        Assert.All(input[..64], v => Assert.Equal(200 / 255f, v, 4));
        Assert.All(input[64..128], v => Assert.Equal(100 / 255f, v, 4));
        Assert.All(input[128..], v => Assert.Equal(50 / 255f, v, 4));
    }

    [Fact]
    public void Stretching_keeps_left_right_and_top_bottom()
    {
        // Left half red, right half blue; top rows bright, bottom rows dark (green channel).
        var frame = Bgra(40, 20, (x, y) => (x < 20 ? (byte)255 : (byte)0, y < 10 ? (byte)255 : (byte)0, x < 20 ? (byte)0 : (byte)255));

        var input = Run(frame, 40, 20, 4, 4);
        float R(int x, int y) => input[y * 4 + x];
        float G(int x, int y) => input[16 + y * 4 + x];
        float B(int x, int y) => input[32 + y * 4 + x];

        for (var y = 0; y < 4; y++)
        {
            Assert.Equal(1f, R(0, y), 3);
            Assert.Equal(0f, B(0, y), 3);
            Assert.Equal(0f, R(3, y), 3);
            Assert.Equal(1f, B(3, y), 3);
        }
        for (var x = 0; x < 4; x++)
        {
            Assert.Equal(1f, G(x, 0), 3);
            Assert.Equal(0f, G(x, 3), 3);
        }
    }

    [Fact]
    public void Shrinking_averages_instead_of_skipping_pixels()
    {
        // A fine checkerboard shrunk 8 times is mid grey, not black or white (no aliasing).
        var frame = Bgra(64, 64, (x, y) => (x + y) % 2 == 0 ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0));

        var input = Run(frame, 64, 64, 8, 8);

        Assert.All(input, v => Assert.InRange(v, 0.45f, 0.55f));
    }

    [Fact]
    public void Row_padding_is_ignored()
    {
        var frame = Bgra(3, 2, (_, _) => (0, 0, 0), stride: 20);

        var input = Run(frame, 3, 2, 3, 2, stride: 20);

        Assert.All(input, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void A_frame_buffer_too_small_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Run(new byte[10], 4, 4, 2, 2));
    }
}
