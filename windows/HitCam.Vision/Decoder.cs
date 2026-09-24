using System.Drawing;

namespace HitCam.Vision;

/// <summary>Turns RF-DETR outputs into detections.</summary>
public static class Decoder
{
    /// <summary>
    /// <paramref name="boxes"/>: <c>queries × 4</c> (cx, cy, w, h in 0..1). <paramref name="logits"/>:
    /// <c>queries × columns</c>. Each query becomes at most one detection: the column with the highest logit is its
    /// class (the column is the class id), sigmoid of that logit its score. Queries whose class has no name in
    /// <paramref name="classes"/>, is excluded, or scores below the threshold are dropped; so are duplicates. Sorted
    /// by score, highest first.
    /// </summary>
    public static List<Detection> Decode(
        ReadOnlySpan<float> boxes, ReadOnlySpan<float> logits, int queries, int columns,
        IReadOnlyDictionary<int, string> classes, DetectionOptions options)
    {
        if (boxes.Length < queries * 4 || logits.Length < queries * columns)
            throw new ArgumentException("The outputs are smaller than queries × columns.");

        var result = new List<Detection>();
        for (var q = 0; q < queries; q++)
        {
            var row = logits.Slice(q * columns, columns);
            var best = 0;
            for (var c = 1; c < columns; c++)
            {
                if (row[c] > row[best])
                    best = c;
            }

            var score = Sigmoid(row[best]);
            if (!(score >= options.Threshold) || !classes.TryGetValue(best, out var name) || options.ExcludedClasses.Contains(best))
                continue;

            var cx = boxes[q * 4];
            var cy = boxes[q * 4 + 1];
            var w = boxes[q * 4 + 2];
            var h = boxes[q * 4 + 3];
            var left = Math.Clamp(cx - w / 2, 0f, 1f);
            var top = Math.Clamp(cy - h / 2, 0f, 1f);
            var right = Math.Clamp(cx + w / 2, 0f, 1f);
            var bottom = Math.Clamp(cy + h / 2, 0f, 1f);
            if (!(right > left) || !(bottom > top))
                continue;
            result.Add(new Detection(best, name, score, RectangleF.FromLTRB(left, top, right, bottom)));
        }

        return RemoveDuplicates(result, options.DuplicateIou);
    }

    /// <summary>Keeps the best of boxes of the same class overlapping more than <paramref name="iou"/>; sorts by score.</summary>
    public static List<Detection> RemoveDuplicates(List<Detection> detections, float iou)
    {
        detections.Sort((a, b) => b.Score.CompareTo(a.Score));
        var kept = new List<Detection>(detections.Count);
        foreach (var detection in detections)
        {
            var duplicate = false;
            foreach (var other in kept)
            {
                if (other.ClassId == detection.ClassId && Geometry.Iou(other.Box, detection.Box) > iou)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
                kept.Add(detection);
        }
        return kept;
    }

    public static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));
}
