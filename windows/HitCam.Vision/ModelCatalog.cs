using System.Globalization;
using System.Text.Json;

namespace HitCam.Vision;

/// <summary>What a model is for in the UI: the two bundled ones get fixed names, others show their own.</summary>
public enum ModelRole
{
    Custom,
    /// <summary>RF-DETR Nano, the bundled default ("Fast").</summary>
    Fast,
    /// <summary>RF-DETR Small ("Accurate").</summary>
    Accurate,
}

/// <summary>A detection model: <c>name.onnx</c> plus its description <c>name.labels.json</c>.</summary>
public sealed class ModelInfo
{
    /// <summary>The file name without extension, e.g. <c>rfdetr-nano</c>; stored in the settings.</summary>
    public required string Id { get; init; }

    /// <summary>"name" from the JSON, e.g. "RF-DETR Nano (COCO)".</summary>
    public required string Name { get; init; }

    public required ModelRole Role { get; init; }

    public required string ModelPath { get; init; }

    public required string LabelsPath { get; init; }

    /// <summary>Network input size; the whole frame is stretched to it.</summary>
    public required int InputWidth { get; init; }

    public required int InputHeight { get; init; }

    /// <summary>Per RGB channel, applied to values in 0..1.</summary>
    public required IReadOnlyList<float> Mean { get; init; }

    public required IReadOnlyList<float> Std { get; init; }

    /// <summary>Output with the boxes, [1, queries, 4] as cx, cy, w, h in 0..1.</summary>
    public required string BoxesOutput { get; init; }

    /// <summary>Output with the class logits, [1, queries, classes]; the column is the class id.</summary>
    public required string LogitsOutput { get; init; }

    /// <summary>Class id (the logits column) to name; columns without a name are never reported.</summary>
    public required IReadOnlyDictionary<int, string> Classes { get; init; }

    public string? License { get; init; }

    public override string ToString() => $"{Id} ({Name})";
}

public sealed class ModelFormatException(string message) : Exception(message);

/// <summary>Models found in the model folders, and why other files there were skipped.</summary>
public sealed record ModelCatalogResult(IReadOnlyList<ModelInfo> Models, IReadOnlyList<string> Problems)
{
    public ModelInfo? Find(string? id) =>
        id is null ? null : Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Finds models (<c>*.onnx</c> next to <c>*.labels.json</c>) in <c>&lt;app dir&gt;/models</c> and
/// <c>%LOCALAPPDATA%/HitCam/models</c>. The first folder wins when both have a model with the same id.
/// </summary>
public static class ModelCatalog
{
    public const string FastModelId = "rfdetr-nano";
    public const string AccurateModelId = "rfdetr-small";
    public const string LabelsSuffix = ".labels.json";

    /// <summary>Models shipped with the app.</summary>
    public static string AppModelsDirectory => Path.Combine(AppContext.BaseDirectory, "models");

    /// <summary>Where users put their own models (and the "Accurate" one, which is not bundled).</summary>
    public static string UserModelsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HitCam", "models");

    public static IReadOnlyList<string> DefaultDirectories => [AppModelsDirectory, UserModelsDirectory];

    public static ModelCatalogResult Scan() => Scan(DefaultDirectories);

    public static ModelCatalogResult Scan(IEnumerable<string> directories)
    {
        var models = new List<ModelInfo>();
        var problems = new List<string>();
        foreach (var directory in directories)
        {
            string[] files;
            try
            {
                if (!Directory.Exists(directory))
                    continue;
                files = Directory.GetFiles(directory, "*.onnx");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{directory}: {ex.Message}");
                continue;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var modelPath in files)
            {
                var id = Path.GetFileNameWithoutExtension(modelPath);
                if (models.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var labelsPath = Path.Combine(directory, id + LabelsSuffix);
                if (!File.Exists(labelsPath))
                {
                    problems.Add($"{Path.GetFileName(modelPath)}: {id}{LabelsSuffix} is missing");
                    continue;
                }
                try
                {
                    models.Add(Parse(File.ReadAllText(labelsPath), id, modelPath, labelsPath));
                }
                catch (Exception ex) when (ex is ModelFormatException or IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{Path.GetFileName(labelsPath)}: {ex.Message}");
                }
            }
        }

        models.Sort((a, b) => a.Role != b.Role
            ? RoleOrder(a.Role).CompareTo(RoleOrder(b.Role))
            : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        return new ModelCatalogResult(models, problems);
    }

    private static int RoleOrder(ModelRole role) => role switch
    {
        ModelRole.Fast => 0,
        ModelRole.Accurate => 1,
        _ => 2,
    };

    public static ModelRole RoleOf(string id) =>
        string.Equals(id, FastModelId, StringComparison.OrdinalIgnoreCase) ? ModelRole.Fast
        : string.Equals(id, AccurateModelId, StringComparison.OrdinalIgnoreCase) ? ModelRole.Accurate
        : ModelRole.Custom;

    /// <summary>Reads and validates a <c>.labels.json</c>; throws <see cref="ModelFormatException"/> with the reason.</summary>
    public static ModelInfo Parse(string json, string id, string modelPath, string labelsPath)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new ModelFormatException($"not valid JSON ({ex.Message})");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ModelFormatException("not a JSON object");

            var format = String(root, "format");
            if (!string.Equals(format, "rfdetr", StringComparison.OrdinalIgnoreCase))
                throw new ModelFormatException($"unsupported format \"{format}\" (only \"rfdetr\")");

            var resize = Optional(root, "resize") ?? "stretch";
            if (!string.Equals(resize, "stretch", StringComparison.OrdinalIgnoreCase))
                throw new ModelFormatException($"unsupported resize \"{resize}\" (only \"stretch\")");

            var input = Numbers(root, "input", 2);
            if (input.Any(v => v != Math.Floor(v) || v < 32 || v > 4096))
                throw new ModelFormatException("\"input\" must be [width, height] between 32 and 4096");

            var mean = Numbers(root, "mean", 3);
            var std = Numbers(root, "std", 3);
            if (std.Any(v => v <= 0))
                throw new ModelFormatException("\"std\" must be positive");

            if (!root.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object)
                throw new ModelFormatException("\"outputs\" is missing");

            if (!root.TryGetProperty("classes", out var classesElement) || classesElement.ValueKind != JsonValueKind.Object)
                throw new ModelFormatException("\"classes\" is missing");
            var classes = new Dictionary<int, string>();
            foreach (var property in classesElement.EnumerateObject())
            {
                if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var classId) || classId > 10_000)
                    throw new ModelFormatException($"class id \"{property.Name}\" is not a number");
                if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                    throw new ModelFormatException($"class {classId} has no name");
                classes[classId] = property.Value.GetString()!.Trim();
            }
            if (classes.Count == 0)
                throw new ModelFormatException("\"classes\" is empty");

            var name = Optional(root, "name");
            return new ModelInfo
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
                Role = RoleOf(id),
                ModelPath = modelPath,
                LabelsPath = labelsPath,
                InputWidth = (int)input[0],
                InputHeight = (int)input[1],
                Mean = [.. mean.Select(v => (float)v)],
                Std = [.. std.Select(v => (float)v)],
                BoxesOutput = String(outputs, "boxes"),
                LogitsOutput = String(outputs, "logits"),
                Classes = classes,
                License = Optional(root, "license"),
            };
        }
    }

    private static string String(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new ModelFormatException($"\"{property}\" is missing");

    private static string? Optional(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double[] Numbers(JsonElement parent, string property, int count)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != count)
            throw new ModelFormatException($"\"{property}\" must be an array of {count} numbers");
        var result = new double[count];
        var i = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !double.IsFinite(item.GetDouble()))
                throw new ModelFormatException($"\"{property}\" must be an array of {count} numbers");
            result[i++] = item.GetDouble();
        }
        return result;
    }
}
