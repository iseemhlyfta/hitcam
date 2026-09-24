using System.Drawing;
using SkiaSharp;

namespace HitCam.Vision.Tests;

/// <summary>
/// RF-DETR Nano on COCO val2017 #39769 (two cats, two remotes) against the PyTorch reference. Models are not in the
/// repository: the test looks for vision/output (made by the vision/ scripts) above the test's folder, and is skipped
/// when there is none.
/// </summary>
public sealed class RealModelTests
{
    // rfdetr's own prediction at threshold 0.5 (plain bilinear resize), xyxy in pixels of the 640×480 photo.
    private static readonly (string Name, float Score, int[] Box)[] Expected =
    [
        ("cat", 0.96f, [14, 54, 316, 474]),
        ("cat", 0.89f, [347, 26, 639, 375]),
        ("remote", 0.91f, [334, 77, 371, 188]),
        ("remote", 0.91f, [40, 74, 176, 118]),
    ];

    private static string? FindUp(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    [Theory]
    [InlineData(ProviderPreference.Auto)]
    [InlineData(ProviderPreference.Cpu)]
    public void Nano_finds_the_cats_and_remotes(ProviderPreference preference)
    {
        var labels = FindUp(Path.Combine("vision", "output", "models", "rfdetr-nano.labels.json"));
        var photo = FindUp(Path.Combine("vision", "output", "spike", "cats.jpg"));
        if (labels is null || photo is null)
        {
            Assert.Skip("vision/output with rfdetr-nano and cats.jpg not found (run the vision/ export).");
            return;
        }

        var catalog = ModelCatalog.Scan([Path.GetDirectoryName(labels)!]);
        var model = catalog.Find(ModelCatalog.FastModelId);
        Assert.NotNull(model);

        using var bitmap = SKBitmap.Decode(photo).Copy(SKColorType.Bgra8888);
        Assert.Equal((640, 480), (bitmap.Width, bitmap.Height));
        using var detector = Detector.Load(model, preference);
        if (preference == ProviderPreference.Cpu)
            Assert.Equal(Detector.Cpu, detector.Provider);

        var detections = detector.Detect(bitmap.GetPixelSpan(), bitmap.Width, bitmap.Height, bitmap.RowBytes, DetectionOptions.Default);

        Assert.Equal(Expected.Length, detections.Count);
        foreach (var (name, score, box) in Expected)
        {
            var expected = RectangleF.FromLTRB(box[0] / 640f, box[1] / 480f, box[2] / 640f, box[3] / 480f);
            var match = detections.Where(d => d.Name == name).MaxBy(d => Geometry.Iou(d.Box, expected));
            Assert.True(Geometry.Iou(match.Box, expected) >= 0.95f, $"{name} at {match.Box}: IoU {Geometry.Iou(match.Box, expected):0.000}");
            Assert.Equal(score, match.Score, 0.03f);
        }
        Assert.InRange(detector.LastMilliseconds, 0.1, 5000);
    }
}
