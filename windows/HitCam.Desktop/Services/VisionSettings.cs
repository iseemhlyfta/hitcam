using HitCam.Vision;

namespace HitCam.Desktop.Services;

/// <summary>Object analysis ("vision" in settings.json); off by default.</summary>
// Plain setters, not init, for the same reason as AppSettings: defaults must survive fields missing from the file.
public sealed record VisionSettings
{
    public const int DefaultThreshold = 50;
    public const int MinThreshold = 10;
    public const int MaxThreshold = 95;

    public bool Enabled { get; set; }

    /// <summary>Boxes are also drawn into the picture of the "HitCam" camera.</summary>
    public bool BurnIn { get; set; }

    /// <summary>File name of the model without extension (<see cref="ModelCatalog"/>).</summary>
    public string ModelId
    {
        get => _modelId;
        set => _modelId = string.IsNullOrWhiteSpace(value) ? ModelCatalog.FastModelId : value;
    }

    /// <summary>Minimum confidence in percent.</summary>
    public int Threshold { get; set; } = DefaultThreshold;

    /// <summary>
    /// Class names (not ids) the user turned off, so the choice carries over between models that know the same
    /// classes. Sorted, without duplicates.
    /// </summary>
    public IReadOnlyList<string> ExcludedClasses
    {
        get => _excludedClasses;
        set => _excludedClasses = Normalize(value);
    }

    private string _modelId = ModelCatalog.FastModelId;
    private IReadOnlyList<string> _excludedClasses = [];

    /// <summary>What the detector gets for <paramref name="model"/>: the threshold clamped, excluded names as its ids.</summary>
    public DetectionOptions ToOptions(ModelInfo model)
    {
        var excluded = new HashSet<string>(ExcludedClasses, StringComparer.OrdinalIgnoreCase);
        return new DetectionOptions
        {
            Threshold = Math.Clamp(Threshold, MinThreshold, MaxThreshold) / 100f,
            ExcludedClasses = model.Classes.Where(c => excluded.Contains(c.Value)).Select(c => c.Key).ToHashSet(),
        };
    }

    public bool Equals(VisionSettings? other) =>
        other is not null && Enabled == other.Enabled && BurnIn == other.BurnIn && ModelId == other.ModelId
        && Threshold == other.Threshold && ExcludedClasses.SequenceEqual(other.ExcludedClasses);

    public override int GetHashCode() => HashCode.Combine(Enabled, BurnIn, ModelId, Threshold, ExcludedClasses.Count);

    private static IReadOnlyList<string> Normalize(IEnumerable<string?>? names) =>
        names is null
            ? []
            : [.. names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>A group of classes in the class filter.</summary>
/// <param name="Title">Localized group name; class names stay English, as the model reports them.</param>
public sealed record ClassGroup(string Key, string Title, IReadOnlyList<string> Classes);

/// <summary>The 80 COCO classes in groups (after COCO's super-categories), for the class filter.</summary>
public static class ClassGroups
{
    public const string OtherKey = "other";

    private static readonly (string Key, string[] Classes)[] Coco =
    [
        ("people", ["person"]),
        ("vehicles", ["bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat"]),
        ("street", ["traffic light", "fire hydrant", "stop sign", "parking meter", "bench"]),
        ("animals", ["bird", "cat", "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe"]),
        ("accessories", ["backpack", "umbrella", "handbag", "tie", "suitcase"]),
        ("sports", ["frisbee", "skis", "snowboard", "sports ball", "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket"]),
        ("kitchen", ["bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl"]),
        ("food", ["banana", "apple", "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake"]),
        ("furniture", ["chair", "couch", "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse", "remote", "keyboard", "cell phone", "microwave", "oven", "toaster", "sink", "refrigerator"]),
        ("things", ["book", "clock", "vase", "scissors", "teddy bear", "hair drier", "toothbrush"]),
    ];

    /// <summary>All 80 COCO class names, grouped.</summary>
    public static IReadOnlyList<ClassGroup> CocoGroups { get; } = [.. Coco.Select(g => new ClassGroup(g.Key, Title(g.Key), g.Classes))];

    /// <summary>
    /// Groups <paramref name="classNames"/> (a model's classes): COCO names go to their group in COCO order, other
    /// names to "Other" in alphabetical order. Empty groups are left out.
    /// </summary>
    public static IReadOnlyList<ClassGroup> For(IEnumerable<string> classNames)
    {
        var names = new HashSet<string>(classNames, StringComparer.OrdinalIgnoreCase);
        var groups = new List<ClassGroup>();
        foreach (var (key, classes) in Coco)
        {
            var present = classes.Where(names.Contains).ToList();
            if (present.Count > 0)
                groups.Add(new ClassGroup(key, Title(key), present));
            names.ExceptWith(present);
        }
        if (names.Count > 0)
            groups.Add(new ClassGroup(OtherKey, Title(OtherKey), [.. names.Order(StringComparer.OrdinalIgnoreCase)]));
        return groups;
    }

    private static string Title(string key) => key switch
    {
        "people" => Loc.ClassGroupPeople,
        "vehicles" => Loc.ClassGroupVehicles,
        "street" => Loc.ClassGroupStreet,
        "animals" => Loc.ClassGroupAnimals,
        "accessories" => Loc.ClassGroupAccessories,
        "sports" => Loc.ClassGroupSports,
        "kitchen" => Loc.ClassGroupKitchen,
        "food" => Loc.ClassGroupFood,
        "furniture" => Loc.ClassGroupFurniture,
        "things" => Loc.ClassGroupThings,
        _ => Loc.ClassGroupOther,
    };
}
