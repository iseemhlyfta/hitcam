using System.Drawing;

namespace HitCam.Vision.Tests;

public sealed class DecoderTests
{
    private const int Columns = 91;

    private static readonly Dictionary<int, string> Coco = new()
    {
        [1] = "person", [3] = "car", [17] = "cat", [18] = "dog", [75] = "remote",
    };

    /// <summary>Builds outputs for a few queries; every logit not given is very negative (score ≈ 0).</summary>
    private sealed class Outputs
    {
        private readonly List<float[]> _boxes = [];
        private readonly List<float[]> _logits = [];

        public Outputs Query(float cx, float cy, float w, float h, params (int Column, float Logit)[] logits)
        {
            _boxes.Add([cx, cy, w, h]);
            var row = Enumerable.Repeat(-10f, Columns).ToArray();
            foreach (var (column, logit) in logits)
                row[column] = logit;
            _logits.Add(row);
            return this;
        }

        public List<Detection> Decode(DetectionOptions? options = null) =>
            Decoder.Decode(
                _boxes.SelectMany(b => b).ToArray(), _logits.SelectMany(l => l).ToArray(), _boxes.Count, Columns,
                Coco, options ?? DetectionOptions.Default);
    }

    private static float Logit(float score) => MathF.Log(score / (1 - score));

    [Fact]
    public void Scores_are_the_sigmoid_of_the_logits()
    {
        Assert.Equal(0.5f, Decoder.Sigmoid(0));
        Assert.Equal(0.9f, Decoder.Sigmoid(Logit(0.9f)), 5);

        var detection = Assert.Single(new Outputs().Query(0.5f, 0.5f, 0.2f, 0.2f, (17, Logit(0.8f))).Decode());

        Assert.Equal(0.8f, detection.Score, 5);
    }

    [Fact]
    public void The_class_is_the_column_with_the_highest_logit()
    {
        var detection = Assert.Single(new Outputs().Query(0.5f, 0.5f, 0.2f, 0.2f, (17, 1f), (18, 3f), (1, 2f)).Decode());

        Assert.Equal(18, detection.ClassId);
        Assert.Equal("dog", detection.Name);
    }

    [Fact]
    public void Boxes_are_converted_from_center_size_and_clamped_to_the_frame()
    {
        var detections = new Outputs()
            .Query(0.5f, 0.4f, 0.2f, 0.4f, (1, 5f))
            .Query(0.05f, 0.95f, 0.3f, 0.3f, (3, 4f))
            .Decode();

        Assert.Equal(RectangleF.FromLTRB(0.4f, 0.2f, 0.6f, 0.6f), Round(detections[0].Box));
        Assert.Equal(RectangleF.FromLTRB(0f, 0.8f, 0.2f, 1f), Round(detections[1].Box));
    }

    [Fact]
    public void Detections_below_the_threshold_are_dropped()
    {
        var outputs = new Outputs()
            .Query(0.2f, 0.2f, 0.1f, 0.1f, (17, Logit(0.49f)))
            .Query(0.6f, 0.6f, 0.1f, 0.1f, (18, Logit(0.51f)))
            .Query(0.8f, 0.8f, 0.1f, 0.1f, (1, Logit(0.9f)));

        Assert.Equal(["person", "dog"], outputs.Decode().Select(d => d.Name));
        Assert.Equal(["person"], outputs.Decode(new DetectionOptions { Threshold = 0.8f }).Select(d => d.Name));
    }

    [Fact]
    public void Excluded_classes_are_dropped()
    {
        var outputs = new Outputs()
            .Query(0.2f, 0.2f, 0.1f, 0.1f, (17, 3f))
            .Query(0.6f, 0.6f, 0.1f, 0.1f, (1, 3f));

        var detections = outputs.Decode(new DetectionOptions { ExcludedClasses = new HashSet<int> { 1 } });

        Assert.Equal(["cat"], detections.Select(d => d.Name));
    }

    [Fact]
    public void Columns_without_a_class_name_are_ignored()
    {
        // Column 12 is one of COCO's unused ids and 0 is not a class; a confident query there is not reported,
        // even if its second-best column is a real class.
        var detections = new Outputs()
            .Query(0.2f, 0.2f, 0.1f, 0.1f, (12, 5f), (17, 4f))
            .Query(0.6f, 0.6f, 0.1f, 0.1f, (0, 5f))
            .Decode();

        Assert.Empty(detections);
    }

    [Fact]
    public void Overlapping_boxes_of_the_same_class_keep_only_the_best()
    {
        var detections = new Outputs()
            .Query(0.50f, 0.5f, 0.4f, 0.4f, (17, Logit(0.7f)))
            .Query(0.51f, 0.5f, 0.4f, 0.4f, (17, Logit(0.9f)))  // IoU ≈ 0.95 with the first: a duplicate
            .Query(0.50f, 0.5f, 0.4f, 0.4f, (18, Logit(0.6f)))  // same place, other class: kept
            .Query(0.70f, 0.5f, 0.4f, 0.4f, (17, Logit(0.8f)))  // IoU ≈ 0.33: a second cat
            .Decode();

        Assert.Equal([(17, 0.9f), (17, 0.8f), (18, 0.6f)], detections.Select(d => (d.ClassId, MathF.Round(d.Score, 3))));
    }

    [Fact]
    public void Iou_of_known_boxes()
    {
        var a = RectangleF.FromLTRB(0, 0, 2, 2);
        Assert.Equal(1f, Geometry.Iou(a, a));
        Assert.Equal(1 / 7f, Geometry.Iou(a, RectangleF.FromLTRB(1, 1, 3, 3)), 5);
        Assert.Equal(0f, Geometry.Iou(a, RectangleF.FromLTRB(2, 0, 4, 2)));
    }

    private static RectangleF Round(RectangleF r) =>
        RectangleF.FromLTRB(MathF.Round(r.Left, 4), MathF.Round(r.Top, 4), MathF.Round(r.Right, 4), MathF.Round(r.Bottom, 4));
}
