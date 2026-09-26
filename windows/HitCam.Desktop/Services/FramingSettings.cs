using System.Drawing;
using System.Runtime.InteropServices;

namespace HitCam.Desktop.Services;

/// <summary>Matches <c>HitCamFraming</c> in HitCamVCam.dll (16 bytes): the part of the frame the camera shows.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct HitCamFraming
{
    public float Left, Top, Right, Bottom;

    public static HitCamFraming From(RectangleF crop) => new() { Left = crop.Left, Top = crop.Top, Right = crop.Right, Bottom = crop.Bottom };
}

/// <summary>Auto-framing ("framing" in settings.json); off by default.</summary>
// Plain setters, not init, for the same reason as AppSettings: defaults must survive fields missing from the file.
public sealed record FramingSettings
{
    public const int DefaultMaxZoom = 180;

    public bool Enabled { get; set; }

    /// <summary>How close the camera may get, percent (150 = 1.5×); a 1080p picture gets soft past 2×.</summary>
    public int MaxZoom
    {
        get => _maxZoom;
        set => _maxZoom = Math.Clamp(value, 110, 200);
    }

    private int _maxZoom = DefaultMaxZoom;
}
