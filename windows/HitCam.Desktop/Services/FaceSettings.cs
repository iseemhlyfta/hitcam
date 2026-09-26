using System.Text.Json.Serialization;

namespace HitCam.Desktop.Services;

/// <summary>What covers a hidden face; the values are the DLL's (<c>HitCamFaceRegion.effect</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FaceEffectKind>))]
public enum FaceEffectKind
{
    Mosaic = 0,
    Blur = 1,
    Fill = 2,
}

/// <summary>Face hiding ("faces" in settings.json); off by default.</summary>
// Plain setters, not init, for the same reason as AppSettings: defaults must survive fields missing from the file.
public sealed record FaceSettings
{
    public const int DefaultStrength = 50;
    public const string DefaultFillColor = "#111827";

    public bool Enabled { get; set; }

    public FaceEffectKind Effect
    {
        get => _effect;
        set => _effect = Enum.IsDefined(value) ? value : FaceEffectKind.Mosaic;
    }

    /// <summary>Mosaic cell size or blur radius, percent.</summary>
    public int Strength
    {
        get => _strength;
        set => _strength = Math.Clamp(value, 0, 100);
    }

    /// <summary>Colour of <see cref="FaceEffectKind.Fill"/>, "#RRGGBB".</summary>
    public string FillColor
    {
        get => _fillColor;
        set => _fillColor = HandSettings.NormalizeColor(value, DefaultFillColor);
    }

    /// <summary>Hidden faces are hidden in the "HitCam" camera too (not only in the preview).</summary>
    public bool CameraEffect { get; set; } = true;

    /// <summary>The squares around the faces are drawn in the camera too.</summary>
    public bool CameraFrame { get; set; }

    private FaceEffectKind _effect = FaceEffectKind.Mosaic;
    private int _strength = DefaultStrength;
    private string _fillColor = DefaultFillColor;
}
