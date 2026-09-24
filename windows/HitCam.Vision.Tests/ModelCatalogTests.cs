namespace HitCam.Vision.Tests;

public sealed class ModelCatalogTests : IDisposable
{
    private const string ValidJson = """
        {
          "name": "RF-DETR Nano (COCO)",
          "format": "rfdetr",
          "input": [384, 384],
          "mean": [0.485, 0.456, 0.406],
          "std": [0.229, 0.224, 0.225],
          "resize": "stretch",
          "outputs": { "boxes": "dets", "logits": "labels" },
          "classes": { "1": "person", "17": "cat", "90": "toothbrush" },
          "license": "Apache-2.0"
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "HitCam.Vision.Tests." + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static ModelInfo Parse(string json, string id = "rfdetr-nano") => ModelCatalog.Parse(json, id, id + ".onnx", id + ".labels.json");

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void AddModel(string folder, string id, string? json = null)
    {
        File.WriteAllBytes(Path.Combine(folder, id + ".onnx"), [0]);
        if (json is not null)
            File.WriteAllText(Path.Combine(folder, id + ".labels.json"), json);
    }

    [Fact]
    public void A_valid_description_is_read()
    {
        var model = Parse(ValidJson);

        Assert.Equal("rfdetr-nano", model.Id);
        Assert.Equal("RF-DETR Nano (COCO)", model.Name);
        Assert.Equal(ModelRole.Fast, model.Role);
        Assert.Equal((384, 384), (model.InputWidth, model.InputHeight));
        Assert.Equal([0.485f, 0.456f, 0.406f], model.Mean);
        Assert.Equal([0.229f, 0.224f, 0.225f], model.Std);
        Assert.Equal(("dets", "labels"), (model.BoxesOutput, model.LogitsOutput));
        Assert.Equal("cat", model.Classes[17]);
        Assert.Equal(3, model.Classes.Count);
        Assert.Equal("Apache-2.0", model.License);
    }

    [Theory]
    [InlineData("rfdetr-nano", ModelRole.Fast)]
    [InlineData("RFDETR-Small", ModelRole.Accurate)]
    [InlineData("my-hands", ModelRole.Custom)]
    public void Roles_come_from_the_file_name(string id, ModelRole role) => Assert.Equal(role, Parse(ValidJson, id).Role);

    [Theory]
    [InlineData("not json", "JSON")]
    [InlineData("[]", "object")]
    [InlineData("""{"format":"yolo"}""", "format")]
    [InlineData("""{"format":"rfdetr","resize":"letterbox"}""", "resize")]
    [InlineData("""{"format":"rfdetr","input":[384]}""", "input")]
    [InlineData("""{"format":"rfdetr","input":[16,16]}""", "input")]
    [InlineData("""{"format":"rfdetr","input":[384,384],"mean":[0.5,0.5]}""", "mean")]
    [InlineData("""{"format":"rfdetr","input":[384,384],"mean":[0,0,0],"std":[1,0,1]}""", "std")]
    [InlineData("""{"format":"rfdetr","input":[384,384],"mean":[0,0,0],"std":[1,1,1]}""", "outputs")]
    [InlineData("""{"format":"rfdetr","input":[384,384],"mean":[0,0,0],"std":[1,1,1],"outputs":{"boxes":"dets"},"classes":{"1":"a"}}""", "logits")]
    [InlineData("""{"format":"rfdetr","input":[384,384],"mean":[0,0,0],"std":[1,1,1],"outputs":{"boxes":"b","logits":"l"}}""", "classes")]
    [InlineData("""{"format":"rfdetr","input":[384,384],"mean":[0,0,0],"std":[1,1,1],"outputs":{"boxes":"b","logits":"l"},"classes":{}}""", "empty")]
    [InlineData("""{"format":"rfdetr","input":[384,384],"mean":[0,0,0],"std":[1,1,1],"outputs":{"boxes":"b","logits":"l"},"classes":{"cat":"cat"}}""", "not a number")]
    [InlineData("""{"format":"rfdetr","input":[384,384],"mean":[0,0,0],"std":[1,1,1],"outputs":{"boxes":"b","logits":"l"},"classes":{"1":""}}""", "no name")]
    public void Invalid_descriptions_say_what_is_wrong(string json, string reason)
    {
        var error = Assert.Throws<ModelFormatException>(() => Parse(json));
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void Scanning_finds_models_in_order_and_explains_skipped_files()
    {
        var app = Folder("app");
        var user = Folder("user");
        AddModel(app, "rfdetr-nano", ValidJson);
        AddModel(user, "rfdetr-nano", ValidJson.Replace("RF-DETR Nano (COCO)", "Shadowed"));
        AddModel(user, "zebra-finder", ValidJson.Replace("RF-DETR Nano (COCO)", "Zebras"));
        AddModel(user, "rfdetr-small", ValidJson.Replace("RF-DETR Nano (COCO)", "RF-DETR Small (COCO)"));
        AddModel(user, "no-labels");
        AddModel(user, "broken", "{");

        var catalog = ModelCatalog.Scan([app, user, Path.Combine(_root, "missing")]);

        Assert.Equal(["rfdetr-nano", "rfdetr-small", "zebra-finder"], catalog.Models.Select(m => m.Id));
        Assert.Equal("RF-DETR Nano (COCO)", catalog.Find("RFDETR-NANO")?.Name);
        Assert.Equal(Path.Combine(app, "rfdetr-nano.onnx"), catalog.Find("rfdetr-nano")?.ModelPath);
        Assert.Null(catalog.Find("unknown"));
        Assert.Equal(2, catalog.Problems.Count);
        Assert.Contains(catalog.Problems, p => p.Contains("no-labels.labels.json is missing"));
        Assert.Contains(catalog.Problems, p => p.StartsWith("broken.labels.json"));
    }

    [Fact]
    public void No_folders_means_no_models()
    {
        var catalog = ModelCatalog.Scan([Path.Combine(_root, "nothing")]);

        Assert.Empty(catalog.Models);
        Assert.Empty(catalog.Problems);
    }
}
