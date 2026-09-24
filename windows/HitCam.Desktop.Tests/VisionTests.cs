using System.Reactive.Concurrency;
using System.Runtime.InteropServices;
using Avalonia;
using HitCam.Desktop.Services;
using HitCam.Desktop.ViewModels;
using HitCam.Desktop.Views;
using HitCam.Vision;
using RectangleF = System.Drawing.RectangleF;

namespace HitCam.Desktop.Tests;

public sealed class VisionTests : IDisposable
{
    /// <summary>The 80 classes of the bundled COCO models (rfdetr-*.labels.json), by id.</summary>
    private static readonly Dictionary<int, string> Coco = new()
    {
        [1] = "person", [2] = "bicycle", [3] = "car", [4] = "motorcycle", [5] = "airplane", [6] = "bus", [7] = "train",
        [8] = "truck", [9] = "boat", [10] = "traffic light", [11] = "fire hydrant", [13] = "stop sign", [14] = "parking meter",
        [15] = "bench", [16] = "bird", [17] = "cat", [18] = "dog", [19] = "horse", [20] = "sheep", [21] = "cow",
        [22] = "elephant", [23] = "bear", [24] = "zebra", [25] = "giraffe", [27] = "backpack", [28] = "umbrella",
        [31] = "handbag", [32] = "tie", [33] = "suitcase", [34] = "frisbee", [35] = "skis", [36] = "snowboard",
        [37] = "sports ball", [38] = "kite", [39] = "baseball bat", [40] = "baseball glove", [41] = "skateboard",
        [42] = "surfboard", [43] = "tennis racket", [44] = "bottle", [46] = "wine glass", [47] = "cup", [48] = "fork",
        [49] = "knife", [50] = "spoon", [51] = "bowl", [52] = "banana", [53] = "apple", [54] = "sandwich", [55] = "orange",
        [56] = "broccoli", [57] = "carrot", [58] = "hot dog", [59] = "pizza", [60] = "donut", [61] = "cake", [62] = "chair",
        [63] = "couch", [64] = "potted plant", [65] = "bed", [67] = "dining table", [70] = "toilet", [72] = "tv",
        [73] = "laptop", [74] = "mouse", [75] = "remote", [76] = "keyboard", [77] = "cell phone", [78] = "microwave",
        [79] = "oven", [80] = "toaster", [81] = "sink", [82] = "refrigerator", [84] = "book", [85] = "clock", [86] = "vase",
        [87] = "scissors", [88] = "teddy bear", [89] = "hair drier", [90] = "toothbrush",
    };

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HitCam.Tests." + Guid.NewGuid().ToString("N"));
    private readonly HistoricalScheduler _time = new();
    private readonly List<VisionSettings> _applied = [];
    private readonly List<VisionSettings> _saved = [];

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ModelInfo Model(string id, IReadOnlyDictionary<int, string>? classes = null) => new()
    {
        Id = id, Name = id + " model", Role = ModelCatalog.RoleOf(id), ModelPath = id + ".onnx", LabelsPath = id + ".labels.json",
        InputWidth = 384, InputHeight = 384, Mean = [0, 0, 0], Std = [1, 1, 1], BoxesOutput = "dets", LogitsOutput = "labels",
        Classes = classes ?? Coco,
    };

    private VisionViewModel CreateViewModel(VisionSettings? initial = null, params ModelInfo[] models) =>
        new(initial ?? new VisionSettings(), () => new ModelCatalogResult(models, []), _applied.Add, _saved.Add, _time);

    // Settings

    [Fact]
    public void Settings_default_to_off_with_the_fast_model_and_all_classes()
    {
        var settings = AppSettings.Parse("""{"serverId":"a"}"""u8)!;

        Assert.False(settings.Vision.Enabled);
        Assert.False(settings.Vision.BurnIn);
        Assert.Equal(ModelCatalog.FastModelId, settings.Vision.ModelId);
        Assert.Equal(50, settings.Vision.Threshold);
        Assert.Empty(settings.Vision.ExcludedClasses);
    }

    [Fact]
    public void Vision_settings_survive_a_save_and_load()
    {
        var path = Path.Combine(_directory, "settings.json");
        var vision = new VisionSettings
        {
            Enabled = true, BurnIn = true, ModelId = "rfdetr-small", Threshold = 72, ExcludedClasses = ["tie", "cat", "cat", " "],
        };

        (AppSettings.Load(path) with { Vision = vision }).Save(path);
        var loaded = AppSettings.Load(path).Vision;

        Assert.Equal(vision, loaded);
        Assert.Equal(["cat", "tie"], loaded.ExcludedClasses);
        Assert.Contains("\"vision\"", File.ReadAllText(path));
    }

    [Fact]
    public void Missing_or_null_vision_fields_keep_their_defaults()
    {
        var nulled = AppSettings.Parse("""{"vision":null}"""u8)!.Vision;
        var partial = AppSettings.Parse("""{"vision":{"enabled":true,"modelId":null,"excludedClasses":null}}"""u8)!.Vision;

        Assert.Equal(new VisionSettings(), nulled);
        Assert.True(partial.Enabled);
        Assert.Equal(ModelCatalog.FastModelId, partial.ModelId);
        Assert.Empty(partial.ExcludedClasses);
    }

    [Fact]
    public void Options_carry_the_threshold_and_the_excluded_classes_as_ids()
    {
        var options = new VisionSettings { Threshold = 65, ExcludedClasses = ["Person", "remote", "unicorn"] }.ToOptions(Model("rfdetr-nano"));

        Assert.Equal(0.65f, options.Threshold);
        Assert.Equal([1, 75], options.ExcludedClasses.Order());
        Assert.Equal(0.95f, new VisionSettings { Threshold = 400 }.ToOptions(Model("a")).Threshold);
    }

    // Class groups

    [Fact]
    public void Every_coco_class_is_in_exactly_one_group()
    {
        var grouped = ClassGroups.CocoGroups.SelectMany(g => g.Classes).ToList();

        Assert.Equal(80, grouped.Count);
        Assert.Equal(Coco.Values.Order(), grouped.Order());
        Assert.Equal(grouped.Count, grouped.Distinct().Count());
        Assert.All(ClassGroups.CocoGroups, g => Assert.False(string.IsNullOrWhiteSpace(g.Title)));
        Assert.DoesNotContain(ClassGroups.For(Coco.Values), g => g.Key == ClassGroups.OtherKey);
    }

    [Fact]
    public void Unknown_classes_of_a_custom_model_go_to_other()
    {
        var groups = ClassGroups.For(["zebra", "hand", "person", "Glasses"]);

        Assert.Equal(["people", "animals", ClassGroups.OtherKey], groups.Select(g => g.Key));
        Assert.Equal(["Glasses", "hand"], groups[^1].Classes);
    }

    // Panel

    [Fact]
    public void The_panel_starts_off_and_picks_the_saved_model()
    {
        var vision = CreateViewModel(new VisionSettings { ModelId = "rfdetr-small" }, Model("rfdetr-nano"), Model("rfdetr-small"), Model("hands"));

        Assert.False(vision.IsEnabled);
        Assert.Equal(["Fast", "Accurate", "hands model"], vision.Models.Select(m => m.Label).Select(English));
        Assert.Equal("rfdetr-small", vision.SelectedModel?.Model.Id);
        Assert.True(vision.UsesModelSegments);
        Assert.Empty(_applied);
    }

    [Fact]
    public void A_missing_saved_model_falls_back_to_the_first()
    {
        var vision = CreateViewModel(new VisionSettings { ModelId = "gone" }, Model("rfdetr-nano"));

        Assert.Equal("rfdetr-nano", vision.SelectedModel?.Model.Id);
    }

    [Fact]
    public void Without_models_the_panel_says_where_to_put_them()
    {
        var vision = CreateViewModel();

        Assert.True(vision.HasNoModels);
        Assert.Null(vision.SelectedModel);
        Assert.Contains(ModelCatalog.UserModelsDirectory, vision.NoModelsText);
    }

    [Fact]
    public void Changes_are_applied_at_once_and_saved_once_they_stop()
    {
        var vision = CreateViewModel(null, Model("rfdetr-nano"));

        vision.IsEnabled = true;
        vision.Threshold = 61.6;
        vision.Threshold = 3;
        vision.BurnIn = true;

        Assert.Equal(4, _applied.Count);
        Assert.Equal(VisionSettings.MinThreshold, _applied[^1].Threshold);
        Assert.Equal("10%", vision.ThresholdText);
        Assert.Empty(_saved);
        _time.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.Equal(_applied[^1], Assert.Single(_saved));
    }

    [Fact]
    public void The_class_filter_excludes_by_name_and_counts()
    {
        var vision = CreateViewModel(null, Model("rfdetr-nano"));
        Assert.Equal(80, vision.Groups.Sum(g => g.Items.Count));
        Assert.Equal(Loc.VisionAllClasses, vision.ClassFilterText);

        vision.Groups.Single(g => g.Key == "animals").IsChecked = false;
        vision.Groups.SelectMany(g => g.Items).Single(i => i.Name == "person").IsChecked = false;

        Assert.Equal(11, vision.Settings.ExcludedClasses.Count);
        Assert.Contains("cat", vision.Settings.ExcludedClasses);
        Assert.Equal(Loc.VisionSomeClasses(69, 80), vision.ClassFilterText);
        Assert.False(vision.Groups.Single(g => g.Key == "animals").IsChecked);
        Assert.Contains(1, vision.CurrentOptions!.ExcludedClasses);

        vision.SelectNoneCommand.Execute().Subscribe();
        Assert.Equal(80, vision.Settings.ExcludedClasses.Count);
        Assert.Equal(Loc.VisionNoClasses, vision.ClassFilterText);

        vision.SelectAllCommand.Execute().Subscribe();
        Assert.Empty(vision.Settings.ExcludedClasses);
    }

    [Fact]
    public void A_partly_checked_group_shows_as_undecided()
    {
        var vision = CreateViewModel(new VisionSettings { ExcludedClasses = ["car"] }, Model("rfdetr-nano"));

        var vehicles = vision.Groups.Single(g => g.Key == "vehicles");

        Assert.Null(vehicles.IsChecked);
        Assert.False(vehicles.Items.Single(i => i.Name == "car").IsChecked);
    }

    [Fact]
    public void Search_hides_classes_and_empty_groups()
    {
        var vision = CreateViewModel(null, Model("rfdetr-nano"));

        vision.ClassSearch = "ca";

        var visible = vision.Groups.Where(g => g.IsVisible).SelectMany(g => g.Items.Where(i => i.IsVisible)).Select(i => i.Name);
        Assert.Equal(["car", "cat", "suitcase", "carrot", "cake"], visible);
        Assert.False(vision.Groups.Single(g => g.Key == "people").IsVisible);

        vision.ClassSearch = "";
        Assert.All(vision.Groups, g => Assert.True(g.IsVisible));
    }

    [Fact]
    public void Exclusions_unknown_to_the_model_are_kept_for_other_models()
    {
        var hands = Model("hands", new Dictionary<int, string> { [0] = "hand", [1] = "person" });
        var vision = CreateViewModel(new VisionSettings { ModelId = "hands", ExcludedClasses = ["cat"] }, Model("rfdetr-nano"), hands);

        vision.Groups.SelectMany(g => g.Items).Single(i => i.Name == "hand").IsChecked = false;

        Assert.Equal(["cat", "hand"], vision.Settings.ExcludedClasses);
    }

    [Fact]
    public void Results_only_show_while_active()
    {
        var vision = CreateViewModel(null, Model("rfdetr-nano"));
        var model = vision.SelectedModel!.Model;
        var track = new Track(1, 1, "person", 0.92f, new RectangleF(0.1f, 0.1f, 0.2f, 0.4f), Tracker.Palette[0]);
        var result = new VisionResult([track], new VisionStats(12.4, 24.2, "DirectML"), model, 1, 960, 540);

        vision.ShowResult(result);
        Assert.Empty(vision.Tracks);

        vision.IsActive = true;
        vision.ShowResult(result);
        Assert.Equal([track], vision.Tracks);
        Assert.Equal(Loc.VisionStats(24.2, 12.4, "DirectML"), vision.StatsText);
        Assert.Equal("rfdetr-nano model", vision.ModelText);

        vision.IsActive = false;
        Assert.Empty(vision.Tracks);
        Assert.False(vision.HasStats);
    }

    [Fact]
    public void A_model_that_fails_shows_why()
    {
        var vision = CreateViewModel(null, Model("rfdetr-nano"));
        vision.IsActive = true;

        vision.ShowStatus(new VisionStatus(VisionState.Failed, vision.SelectedModel!.Model, null, "file missing"));

        Assert.True(vision.HasError);
        Assert.Contains("file missing", vision.ErrorText);
    }

    // Preview overlay

    [Fact]
    public void A_wide_picture_in_a_tall_box_is_letterboxed_top_and_bottom()
    {
        var picture = OverlayGeometry.Fit(new Size(400, 400), new Size(960, 540));

        Assert.Equal(new Rect(0, 87.5, 400, 225), picture);
    }

    [Fact]
    public void A_tall_picture_in_a_wide_box_is_pillarboxed()
    {
        var picture = OverlayGeometry.Fit(new Size(1000, 500), new Size(540, 960));

        Assert.Equal(281.25, picture.Width, 6);
        Assert.Equal(500, picture.Height, 6);
        Assert.Equal((1000 - 281.25) / 2, picture.X, 6);
        Assert.Equal(0, picture.Y, 6);
    }

    [Fact]
    public void Normalized_boxes_map_into_the_picture()
    {
        var picture = new Rect(100, 50, 800, 450);

        var box = OverlayGeometry.Map(new RectangleF(0.25f, 0.5f, 0.5f, 0.25f), picture);

        Assert.Equal(new Rect(300, 275, 400, 112.5), box);
        Assert.Equal(picture, OverlayGeometry.Map(new RectangleF(0, 0, 1, 1), picture));
        Assert.Equal(default, OverlayGeometry.Fit(new Size(100, 100), new Size(0, 0)));
    }

    [Fact]
    public void Label_tabs_sit_above_the_box_and_stay_in_the_picture()
    {
        var picture = new Rect(0, 0, 800, 450);
        var tab = new Size(80, 18);

        Assert.Equal(new Rect(100, 82, 80, 18), OverlayGeometry.LabelTab(new Rect(100, 100, 200, 200), tab, picture));
        // No room above: inside the box.
        Assert.Equal(new Rect(100, 5, 80, 18), OverlayGeometry.LabelTab(new Rect(100, 5, 200, 200), tab, picture));
        // At the right edge: pushed left.
        Assert.Equal(new Rect(720, 82, 80, 18), OverlayGeometry.LabelTab(new Rect(760, 100, 40, 40), tab, picture));
    }

    // Burn-in

    [Fact]
    public void The_overlay_struct_matches_the_native_layout()
    {
        Assert.Equal(36, Marshal.SizeOf<HitCamOverlayBox>());
        Assert.Equal(0, (int)Marshal.OffsetOf<HitCamOverlayBox>(nameof(HitCamOverlayBox.Left)));
        Assert.Equal(12, (int)Marshal.OffsetOf<HitCamOverlayBox>(nameof(HitCamOverlayBox.Bottom)));
        Assert.Equal(16, (int)Marshal.OffsetOf<HitCamOverlayBox>(nameof(HitCamOverlayBox.Rgb)));
        Assert.Equal(20, (int)Marshal.OffsetOf<HitCamOverlayBox>(nameof(HitCamOverlayBox.LabelWidth)));
        Assert.Equal(24, (int)Marshal.OffsetOf<HitCamOverlayBox>(nameof(HitCamOverlayBox.LabelHeight)));
        Assert.Equal(28, (int)Marshal.OffsetOf<HitCamOverlayBox>(nameof(HitCamOverlayBox.Label)));
    }

    [Fact]
    public void Labels_are_white_text_alpha_sized_for_the_picture()
    {
        using var labels = new LabelRenderer();

        var small = labels.Render("person 92%", 720);
        var large = labels.Render("person 92%", 2160);

        Assert.Equal(17, LabelRenderer.TextSizeFor(720));
        Assert.Equal(52, LabelRenderer.TextSizeFor(2160));
        Assert.Equal(small.Width * small.Height, small.Alpha.Length);
        Assert.InRange(small.Height, 17, 30);
        Assert.InRange(large.Height / (double)small.Height, 2.6, 3.4);
        Assert.InRange(small.Width, small.Height * 3, small.Height * 8);
        // Text in the middle, clear margins.
        Assert.Contains(small.Alpha, a => a > 200);
        Assert.All(Enumerable.Range(0, small.Height), y => Assert.Equal(0, small.Alpha[y * small.Width]));
        Assert.All(Enumerable.Range(0, small.Width), x => Assert.Equal(0, small.Alpha[x]));
        // Longer text, wider tab; the same text again comes from the cache.
        Assert.True(labels.Render("refrigerator 100%", 720).Width > small.Width);
        Assert.Same(small, labels.Render("person 92%", 720));
        Assert.Equal(3, labels.CachedCount);
    }

    [Fact]
    public void Burn_in_sends_boxes_with_readable_labels_and_clears_when_off()
    {
        var sent = new List<(HitCamOverlayBox Box, byte[] Label)[]>();
        using var overlay = new CameraOverlay(boxes =>
            // The label memory is only valid during the call: copy it here, as the DLL does.
            sent.Add([.. boxes.ToArray().Select(b => (b, Copy(b)))]));
        overlay.FrameHeight = 1080;
        var track = new Track(3, 1, "person", 0.92f, new RectangleF(0.1f, 0.2f, 0.3f, 0.4f), 0x2563EB);

        overlay.Show([track]);
        Assert.Empty(sent); // not enabled yet

        overlay.SetEnabled(true);
        overlay.Show([track]);
        var (box, label) = Assert.Single(Assert.Single(sent));
        Assert.Equal((0.1f, 0.2f, 0.4f, 0.6f, 0x2563EBu), (box.Left, box.Top, MathF.Round(box.Right, 5), MathF.Round(box.Bottom, 5), box.Rgb));
        Assert.Equal(box.LabelWidth * box.LabelHeight, label.Length);
        Assert.Contains(label, a => a > 200);

        overlay.Show([]);
        overlay.Show([]);
        Assert.Equal(2, sent.Count); // one clear for several empty results
        Assert.Empty(sent[^1]);

        overlay.Show([track]);
        overlay.SetEnabled(false);
        Assert.Empty(sent[^1]);
        overlay.Show([track]);
        Assert.Empty(sent[^1]);
    }

    private static byte[] Copy(HitCamOverlayBox box)
    {
        var bytes = new byte[box.LabelWidth * box.LabelHeight];
        Marshal.Copy(box.Label, bytes, 0, bytes.Length);
        return bytes;
    }

    private static string English(string label) => label switch
    {
        "Быстрая" => "Fast",
        "Точная" => "Accurate",
        _ => label,
    };
}
