using System.Collections.Concurrent;
using System.Drawing;
using HitCam.Vision.Faces;
using HitCam.Vision.Hands;
using SkiaSharp;

namespace HitCam.Vision.Tests;

public sealed class FaceDetectorTests
{
    [Fact]
    public void Decoding_places_the_face_through_its_grid_cell()
    {
        // Stride 32: a 20×20 grid. Cell row 2, column 3 (index 43), centre offset (0.5, 0.5), size e^0 × 32.
        var cells = 400;
        var cls = new float[cells];
        var obj = new float[cells];
        var bbox = new float[cells * 4];
        var kps = new float[cells * 10];
        cls[43] = 0.81f;
        obj[43] = 1f;
        bbox[43 * 4] = 0.5f;
        bbox[43 * 4 + 1] = 0.5f;
        kps[43 * 10] = 0.25f;   // first landmark x offset
        var faces = new List<DetectedFace>();

        FaceDetector.Decode(32, cls, obj, bbox, kps, 2f, 0.6f, faces);

        var face = Assert.Single(faces);
        Assert.Equal(0.9f, face.Score, 1e-5f);                 // √(0.81 × 1)
        Assert.Equal((3 + 0.5f) * 32 * 2 - 32, face.Box.X, 1e-3f);  // centre 112 in the input, ×2 to the frame, minus half of 64
        Assert.Equal(64, face.Box.Width, 1e-3f);
        Assert.Equal((3 + 0.25f) * 32 * 2, face.Landmarks[0].X, 1e-3f);
        Assert.Equal(2 * 32 * 2, face.Landmarks[0].Y, 1e-3f);
    }

    [Fact]
    public void Low_scores_are_dropped_and_overlaps_merged()
    {
        var weak = new List<DetectedFace>();
        var cls = new float[400];
        var obj = new float[400];
        cls[5] = 0.3f;
        obj[5] = 1f;   // √0.3 ≈ 0.55
        FaceDetector.Decode(32, cls, obj, new float[1600], new float[4000], 1, 0.6f, weak);
        Assert.Empty(weak);

        var a = new DetectedFace(new RectangleF(0, 0, 100, 100), new PointF[5], 0.9f);
        var b = new DetectedFace(new RectangleF(10, 5, 100, 100), new PointF[5], 0.8f);
        var c = new DetectedFace(new RectangleF(300, 0, 100, 100), new PointF[5], 0.7f);
        Assert.Equal([a, c], FaceDetector.Nms([b, c, a], 0.3f));
    }

    [Fact]
    public void The_frame_is_fitted_at_the_top_left_as_bgr_planes()
    {
        const int w = 1280, h = 720;
        var frame = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            frame[i * 4] = 10;       // B
            frame[i * 4 + 1] = 20;   // G
            frame[i * 4 + 2] = 30;   // R
        }
        var input = new float[3 * 640 * 640];
        var scale = FaceDetector.Fit(frame, w, h, w * 4, input);

        Assert.Equal(0.5f, scale);
        const int plane = 640 * 640;
        Assert.Equal((10f, 20f, 30f), (input[100], input[plane + 100], input[2 * plane + 100]));
        Assert.Equal(0, input[400 * 640 + 100]);   // below the 360 rows of picture: black
    }
}

public sealed class FaceRecognizerTests
{
    [Fact]
    public void The_template_maps_onto_itself()
    {
        var (a, b, tx, ty) = FaceRecognizer.Similarity(FaceRecognizer.Template, FaceRecognizer.Template);
        Assert.Equal(1, a, 1e-4f);
        Assert.Equal(0, b, 1e-4f);
        Assert.Equal(0, tx, 1e-3f);
        Assert.Equal(0, ty, 1e-3f);
    }

    [Fact]
    public void A_turned_scaled_face_is_brought_back_onto_the_template()
    {
        // The template scaled ×3, turned 30° and moved: the transform undoes exactly that.
        var (sin, cos) = MathF.SinCos(MathF.PI / 6);
        var face = FaceRecognizer.Template.Select(p => new PointF(
            400 + 3 * (p.X * cos - p.Y * sin), 200 + 3 * (p.X * sin + p.Y * cos))).ToArray();
        var (a, b, tx, ty) = FaceRecognizer.Similarity(face, FaceRecognizer.Template);
        for (var i = 0; i < 5; i++)
        {
            var x = a * face[i].X - b * face[i].Y + tx;
            var y = b * face[i].X + a * face[i].Y + ty;
            Assert.Equal(FaceRecognizer.Template[i].X, x, 1e-2f);
            Assert.Equal(FaceRecognizer.Template[i].Y, y, 1e-2f);
        }
        Assert.Equal(1 / 3f, MathF.Sqrt(a * a + b * b), 1e-4f);
    }

    [Fact]
    public void Alignment_reads_the_frame_through_the_inverse_transform_as_rgb()
    {
        const int w = 200, h = 200;
        var frame = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                frame[(y * w + x) * 4] = 5;                // B
                frame[(y * w + x) * 4 + 1] = (byte)y;      // G = y
                frame[(y * w + x) * 4 + 2] = (byte)x;      // R = x
            }
        }
        var input = new float[3 * 112 * 112];
        // Output = frame shifted by (-20, -30): output (0, 0) reads frame (20, 30).
        FaceRecognizer.Align(frame, w, h, w * 4, (1, 0, -20, -30), input);
        Assert.Equal(20, input[0], 1e-3f);                 // R plane first
        Assert.Equal(30, input[112 * 112], 1e-3f);          // G
        Assert.Equal(5, input[2 * 112 * 112], 1e-3f);       // B
    }
}

/// <summary>
/// Faces "in the frame" for the tracker: each detects where it is, and its fingerprint is a fixed unit vector per
/// person (the same person gives the same vector).
/// </summary>
internal sealed class FakeFaceModels : IFaceModels
{
    public List<(RectangleF Box, int Person)> Faces { get; } = [];

    public int Fingerprints { get; private set; }

    public bool IsDisposed { get; private set; }

    public string Provider => "Fake";

    public IReadOnlyList<DetectedFace> Detect(VisionFrame frame) =>
        [.. Faces.Select(f => new DetectedFace(f.Box, [.. Enumerable.Repeat(new PointF(f.Box.X, f.Box.Y), 5)], 0.9f + f.Person * 0.001f))];

    public float[] Fingerprint(VisionFrame frame, DetectedFace face)
    {
        Fingerprints++;
        var person = (int)MathF.Round((face.Score - 0.9f) * 1000);
        var vector = new float[FaceRecognizer.Length];
        vector[person % FaceRecognizer.Length] = 1;
        return vector;
    }

    public void Dispose() => IsDisposed = true;
}

public sealed class FaceTrackerTests
{
    private static readonly VisionFrame Frame = CreateFrame();

    private static VisionFrame CreateFrame()
    {
        var frame = new VisionFrame();
        frame.SetSize(1000, 500);
        return frame;
    }

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void A_new_face_is_hidden_and_a_click_uncovers_it()
    {
        var models = new FakeFaceModels();
        models.Faces.Add((new RectangleF(100, 100, 80, 100), 1));
        var tracker = new FaceTracker(models);

        var face = Assert.Single(tracker.Update(Frame, At(0)));
        Assert.True(face.Hidden);
        Assert.True(face.Seen);

        tracker.Toggle(face.Id);
        Assert.False(Assert.Single(tracker.Update(Frame, At(0.03))).Hidden);
        Assert.Equal(1, tracker.UncoveredPeople);

        tracker.Toggle(face.Id);
        Assert.True(Assert.Single(tracker.Update(Frame, At(0.06))).Hidden);
        Assert.Equal(0, tracker.UncoveredPeople);
    }

    [Fact]
    public void The_square_is_larger_than_the_face_and_normalized()
    {
        var models = new FakeFaceModels();
        models.Faces.Add((new RectangleF(100, 100, 80, 100), 1));
        var box = Assert.Single(new FaceTracker(models).Update(Frame, At(0))).Box;
        // 100 px × 1.15 = 115 px square around the centre (140, 150).
        Assert.Equal(115f / 1000, box.Width, 1e-4f);
        Assert.Equal(115f / 500, box.Height, 1e-4f);
        Assert.Equal((140 - 57.5f) / 1000, box.X, 1e-4f);
    }

    [Fact]
    public void A_lost_face_is_held_for_a_second_a_little_larger_then_dropped()
    {
        var models = new FakeFaceModels();
        models.Faces.Add((new RectangleF(100, 100, 100, 100), 1));
        var tracker = new FaceTracker(models);
        var seen = Assert.Single(tracker.Update(Frame, At(0)));

        models.Faces.Clear();
        var held = Assert.Single(tracker.Update(Frame, At(0.5)));
        Assert.False(held.Seen);
        Assert.True(held.Hidden);
        Assert.Equal(seen.Box.Width * 1.2f, held.Box.Width, 1e-4f);

        Assert.Empty(tracker.Update(Frame, At(1.2)));
    }

    [Fact]
    public void An_uncovered_person_who_comes_back_stays_uncovered_and_others_stay_hidden()
    {
        var models = new FakeFaceModels();
        models.Faces.Add((new RectangleF(100, 100, 100, 100), 1));
        var tracker = new FaceTracker(models);
        tracker.Toggle(Assert.Single(tracker.Update(Frame, At(0))).Id);

        // Gone for two seconds, then back elsewhere: a new track, recognized.
        models.Faces.Clear();
        tracker.Update(Frame, At(0.1));
        tracker.Update(Frame, At(2));
        models.Faces.Add((new RectangleF(600, 200, 100, 100), 1));
        models.Faces.Add((new RectangleF(300, 200, 100, 100), 2));
        var faces = tracker.Update(Frame, At(2.1));

        Assert.Equal(2, faces.Count);
        Assert.False(faces.Single(f => f.Box.X > 0.5f).Hidden);   // person 1
        Assert.True(faces.Single(f => f.Box.X < 0.5f).Hidden);    // a stranger
    }

    [Fact]
    public void Fingerprints_are_taken_for_new_faces_and_refreshed_once_a_second()
    {
        var models = new FakeFaceModels();
        models.Faces.Add((new RectangleF(100, 100, 100, 100), 1));
        var tracker = new FaceTracker(models);
        for (var i = 0; i < 30; i++)
            tracker.Update(Frame, At(i / 30.0));   // one second
        Assert.Equal(1, models.Fingerprints);
        tracker.Update(Frame, At(1.05));
        Assert.Equal(2, models.Fingerprints);
    }

    [Fact]
    public void Forgetting_people_hides_everyone_again()
    {
        var models = new FakeFaceModels();
        models.Faces.Add((new RectangleF(100, 100, 100, 100), 1));
        var tracker = new FaceTracker(models);
        tracker.Toggle(Assert.Single(tracker.Update(Frame, At(0))).Id);
        tracker.ForgetPeople();
        Assert.True(Assert.Single(tracker.Update(Frame, At(0.03))).Hidden);
    }
}

public sealed class FaceEngineTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class FakeCamera
    {
        private long _latest;

        public ulong Latest { set => Interlocked.Exchange(ref _latest, (long)value); }

        public bool Grab(ulong previous, VisionFrame target)
        {
            var latest = (ulong)Interlocked.Read(ref _latest);
            if (latest == 0 || latest == previous)
                return false;
            target.SetSize(1000, 500);
            target.Sequence = latest;
            return true;
        }
    }

    [Fact]
    public void Clicks_apply_and_uncovered_people_survive_a_restart()
    {
        var camera = new FakeCamera();
        FakeFaceModels NewModels()
        {
            var m = new FakeFaceModels();
            m.Faces.Add((new RectangleF(100, 100, 100, 100), 1));
            return m;
        }
        using var engine = new FaceEngine(camera.Grab, NewModels);
        var results = new BlockingCollection<FaceResult>();
        engine.ResultReady += results.Add;

        engine.Start();
        camera.Latest = 1;
        Assert.True(results.TryTake(out var first, Wait));
        var face = Assert.Single(first.Faces);
        Assert.True(face.Hidden);

        engine.Toggle(face.Id);
        camera.Latest = 2;
        Assert.True(results.TryTake(out var second, Wait));
        Assert.False(Assert.Single(second.Faces).Hidden);

        // Off and on again (models reloaded): the person is still uncovered.
        engine.Stop();
        Assert.True(SpinWait.SpinUntil(() => engine.Status.State == VisionState.Stopped, Wait));
        engine.Start();
        camera.Latest = 3;
        Assert.True(results.TryTake(out var third, Wait));
        Assert.False(Assert.Single(third.Faces).Hidden);
    }
}

/// <summary>
/// YuNet and SFace on a public group photo (NASA Group 21, 8 people) against the Python reference (OpenCV's own
/// FaceDetectorYN on the same fitted input). Models and photo are not in the repository: skipped without
/// vision/output/faces.
/// </summary>
public sealed class RealFaceModelTests
{
    // x, y, width, height in pixels of the 1280×853 photo, left to right.
    private static readonly float[][] Expected =
    [
        [163.2f, 144.4f, 87.9f, 101.8f], [362.3f, 197.3f, 73.0f, 93.0f], [387.2f, 429.1f, 89.0f, 111.1f], [538.7f, 114.2f, 80.4f, 105.8f],
        [679.3f, 420.8f, 86.0f, 106.8f], [766.4f, 176.1f, 70.6f, 96.6f], [967.9f, 383.1f, 88.3f, 116.1f], [1031.8f, 169.9f, 79.7f, 99.2f],
    ];

    private static string? FindUp(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static VisionFrame Load(SKBitmap source)
    {
        using var bitmap = source.Copy(SKColorType.Bgra8888);
        var frame = new VisionFrame();
        frame.SetSize(bitmap.Width, bitmap.Height);
        for (var y = 0; y < bitmap.Height; y++)
            bitmap.GetPixelSpan().Slice(y * bitmap.RowBytes, frame.Stride).CopyTo(frame.Pixels[(y * frame.Stride)..]);
        return frame;
    }

    [Theory]
    [InlineData(ProviderPreference.Auto)]
    [InlineData(ProviderPreference.Cpu)]
    public void Finds_the_eight_faces_and_tells_people_apart(ProviderPreference preference)
    {
        var directory = FindUp(Path.Combine("vision", "output", "faces"));
        var files = directory is null ? null : FaceModelFiles.Find([directory]);
        var photo = directory is null ? null : Path.Combine(directory, "NASAGroup21.jpg");
        if (files is null || !File.Exists(photo))
        {
            Assert.Skip("vision/output/faces with the face models and NASAGroup21.jpg not found.");
            return;
        }

        using var source = SKBitmap.Decode(photo);
        var frame = Load(source);
        using var models = FaceModels.Load(files.Value.Detector, files.Value.Recognizer, preference);

        var faces = models.Detect(frame).OrderBy(f => f.Box.X).ToList();
        Assert.Equal(Expected.Length, faces.Count);
        for (var i = 0; i < faces.Count; i++)
        {
            // Resampling differs slightly from OpenCV's: within 3 px.
            Assert.True(Math.Abs(faces[i].Box.X - Expected[i][0]) < 3 && Math.Abs(faces[i].Box.Width - Expected[i][2]) < 3,
                $"face {i}: {faces[i].Box} vs {string.Join(", ", Expected[i])}");
        }

        var prints = faces.Select(f => models.Fingerprint(frame, f)).ToList();
        var different = prints.SelectMany((a, i) => prints.Skip(i + 1).Select(b => FaceRecognizer.Similarity(a, b))).Max();
        Assert.True(different < FaceRecognizer.SameFace, $"different people: {different}");

        // The same people, the photo shrunk to 70% and moved: recognized.
        using var canvas = new SKBitmap(source.Width, source.Height);
        using (var g = new SKCanvas(canvas))
        {
            g.Clear(SKColors.Black);
            using var image = SKImage.FromBitmap(source);
            g.DrawImage(image, new SKRect(60, 40, 60 + source.Width * 0.7f, 40 + source.Height * 0.7f), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        var moved = Load(canvas);
        var again = models.Detect(moved).OrderBy(f => f.Box.X).ToList();
        Assert.Equal(faces.Count, again.Count);
        for (var i = 0; i < again.Count; i++)
        {
            var same = FaceRecognizer.Similarity(prints[i], models.Fingerprint(moved, again[i]));
            Assert.True(same > 0.6f, $"face {i} shrunk: {same}");
        }
    }
}
