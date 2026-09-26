using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace HitCam.Desktop.Services;

/// <summary>Matches <c>HitCamBackground</c> in HitCamVCam.dll (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct HitCamBackground
{
    /// <summary>0 off, 1 blur, 2 replace with the picture.</summary>
    public int Mode;
    /// <summary>0..1: blur radius.</summary>
    public float Strength;
    /// <summary>0..1: softness of the person's outline.</summary>
    public float Edge;
    /// <summary>0..1: room kept around the person.</summary>
    public float Dilate;
    /// <summary>Non-zero: without a fresh mask the whole picture is blurred.</summary>
    public int FailClosed;
    public uint Reserved;
}

/// <summary>What goes behind the person.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BackgroundMode>))]
public enum BackgroundMode
{
    Blur,
    Replace,
}

/// <summary>Background blur or replacement ("background" in settings.json); off by default.</summary>
// Plain setters, not init, for the same reason as AppSettings: defaults must survive fields missing from the file.
public sealed record BackgroundSettings
{
    public const int DefaultStrength = 60;
    public const int DefaultEdge = 30;

    public bool Enabled { get; set; }

    public BackgroundMode Mode
    {
        get => _mode;
        set => _mode = Enum.IsDefined(value) ? value : BackgroundMode.Blur;
    }

    /// <summary>Blur radius, percent.</summary>
    public int Strength
    {
        get => _strength;
        set => _strength = Math.Clamp(value, 0, 100);
    }

    /// <summary>Softness of the person's outline, percent.</summary>
    public int Edge
    {
        get => _edge;
        set => _edge = Math.Clamp(value, 0, 100);
    }

    /// <summary>
    /// While the person is not found yet (the model is loading, the analysis stalls), the whole picture is blurred rather
    /// than shown with the room behind them.
    /// </summary>
    public bool BlurWhileUnknown { get; set; } = true;

    /// <summary>The picture for <see cref="BackgroundMode.Replace"/>; null: none chosen yet (the room is blurred then).</summary>
    public string? ImagePath { get; set; }

    /// <summary>What goes to the DLL; off (mode 0) unless enabled.</summary>
    public HitCamBackground ToNative() => Enabled
        ? new HitCamBackground
        {
            Mode = Mode == BackgroundMode.Replace ? 2 : 1,
            Strength = Strength / 100f,
            Edge = Edge / 100f,
            Dilate = 0.1f,
            FailClosed = BlurWhileUnknown ? 1 : 0,
        }
        : default;

    private BackgroundMode _mode = BackgroundMode.Blur;
    private int _strength = DefaultStrength;
    private int _edge = DefaultEdge;
}
