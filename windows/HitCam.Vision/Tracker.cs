using System.Drawing;

namespace HitCam.Vision;

/// <summary>
/// An object followed across frames. <see cref="Box"/> is smoothed and normalized to the frame; <see cref="Id"/> and
/// <see cref="Rgb"/> stay the same for as long as the object is tracked.
/// </summary>
public readonly record struct Track(int Id, int ClassId, string Name, float Score, RectangleF Box, uint Rgb)
{
    /// <summary>"person 92%": the class name and the confidence.</summary>
    public string Label => $"{Name} {(int)MathF.Round(Score * 100)}%";
}

public sealed record TrackerOptions
{
    /// <summary>A detection continues a track of the same class if they overlap at least this much.</summary>
    public float MatchIou { get; init; } = 0.2f;

    /// <summary>Weight of the new box in the moving average (1 = no smoothing).</summary>
    public float Smoothing { get; init; } = 0.55f;

    /// <summary>Hits before a track is shown: one-frame false positives never appear.</summary>
    public int ConfirmHits { get; init; } = 2;

    /// <summary>How long a track survives without being seen (analysis runs at a varying rate, so this is time).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(0.5);
}

/// <summary>
/// Follows detections across frames: greedy matching by overlap within a class, boxes smoothed with a moving
/// average. Not thread-safe.
/// </summary>
public sealed class Tracker(TrackerOptions? options = null)
{
    /// <summary>
    /// Distinct colours that keep white text readable (contrast about 4:1 or more), in the order new tracks get them.
    /// 0xRRGGBB.
    /// </summary>
    public static IReadOnlyList<uint> Palette { get; } =
    [
        0x2563EB, // blue
        0xDC2626, // red
        0x059669, // green
        0x9333EA, // purple
        0xC2410C, // orange
        0x0E7490, // teal
        0xDB2777, // pink
        0x4D7C0F, // olive
        0x4F46E5, // indigo
        0xA16207, // amber
        0x7C3AED, // violet
        0x475569, // slate
    ];

    private readonly TrackerOptions _options = options ?? new TrackerOptions();
    private readonly List<State> _tracks = [];
    private int _nextId = 1;

    public TrackerOptions Options => _options;

    /// <summary>Tracks alive now, confirmed or not.</summary>
    public int Count => _tracks.Count;

    /// <summary>Adds one analysed frame taken at <paramref name="now"/>; returns the tracks to show.</summary>
    public IReadOnlyList<Track> Update(IReadOnlyList<Detection> detections, TimeSpan now)
    {
        // Tracks not seen for too long are gone.
        _tracks.RemoveAll(t => now - t.LastSeen > _options.Timeout);

        // Every same-class pair that overlaps enough, best first; each track and detection is used once.
        var pairs = new List<(float Iou, State Track, int Detection)>();
        foreach (var track in _tracks)
        {
            for (var d = 0; d < detections.Count; d++)
            {
                if (detections[d].ClassId != track.ClassId)
                    continue;
                var iou = Geometry.Iou(track.Box, detections[d].Box);
                if (iou >= _options.MatchIou)
                    pairs.Add((iou, track, d));
            }
        }
        pairs.Sort((a, b) => b.Iou.CompareTo(a.Iou));

        var usedDetections = new bool[detections.Count];
        var usedTracks = new HashSet<State>();
        foreach (var (_, track, d) in pairs)
        {
            if (usedDetections[d] || !usedTracks.Add(track))
                continue;
            usedDetections[d] = true;
            var detection = detections[d];
            var a = _options.Smoothing;
            track.Box = RectangleF.FromLTRB(
                Mix(track.Box.Left, detection.Box.Left, a), Mix(track.Box.Top, detection.Box.Top, a),
                Mix(track.Box.Right, detection.Box.Right, a), Mix(track.Box.Bottom, detection.Box.Bottom, a));
            track.Score = Mix(track.Score, detection.Score, a);
            track.Name = detection.Name;
            track.Hits++;
            track.LastSeen = now;
        }

        for (var d = 0; d < detections.Count; d++)
        {
            if (usedDetections[d])
                continue;
            var detection = detections[d];
            var id = _nextId++;
            _tracks.Add(new State
            {
                Id = id,
                ClassId = detection.ClassId,
                Name = detection.Name,
                Score = detection.Score,
                Box = detection.Box,
                Hits = 1,
                LastSeen = now,
                Color = FreeColor(id),
            });
        }

        var visible = new List<Track>();
        foreach (var track in _tracks)
        {
            if (track.Hits >= _options.ConfirmHits)
                visible.Add(new Track(track.Id, track.ClassId, track.Name, track.Score, track.Box, Palette[track.Color]));
        }
        return visible;
    }

    /// <summary>Forgets every track (another camera, another picture size, a new connection).</summary>
    public void Reset()
    {
        _tracks.Clear();
    }

    // The next colour in turn that no live track has, so neighbours differ; ids keep counting across resets.
    private int FreeColor(int id)
    {
        var start = (id - 1) % Palette.Count;
        for (var i = 0; i < Palette.Count; i++)
        {
            var color = (start + i) % Palette.Count;
            if (!_tracks.Any(t => t.Color == color))
                return color;
        }
        return start;
    }

    private static float Mix(float old, float @new, float weight) => old + (@new - old) * weight;

    private sealed class State
    {
        public int Id;
        public int ClassId;
        public string Name = "";
        public float Score;
        public RectangleF Box;
        public int Hits;
        public TimeSpan LastSeen;
        public int Color;
    }
}
