using System.Runtime.InteropServices;

namespace HitCam.Desktop.Services;

/// <summary>
/// Picture processing on this PC, in the units of the UI sliders: color sliders -100..100 (0 is neutral),
/// strengths 0..100 (0 is off). Stored in settings.json; see <see cref="ToNative"/> for what the DLL gets.
/// </summary>
// Plain setters, not init, for the same reason as AppSettings: defaults must survive fields missing from the file.
public sealed record ProcessingSettings
{
    public const int ArtifactReductionOff = 0;
    public const int ArtifactReductionGentle = 1;
    public const int ArtifactReductionStrong = 2;

    /// <summary>
    /// Master switch of the processing on this PC (noise reduction, colour, sharpness). Off, the frames go through
    /// untouched and the GPU is not used for them; the sliders keep their values. NVIDIA artifact removal has its own
    /// switch (experiments).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Temporal noise reduction, 0 (off) to 100.</summary>
    public int TemporalStrength { get; set; }

    /// <summary>NVIDIA RTX compression artifact reduction: 0 off, 1 gentle, 2 strong.</summary>
    public int ArtifactReduction { get; set; }

    public int Brightness { get; set; }
    public int Contrast { get; set; }
    public int Saturation { get; set; }
    public int Shadows { get; set; }
    public int Highlights { get; set; }

    /// <summary>0 (off) to 100.</summary>
    public int Sharpness { get; set; }

    /// <summary>Colour and sharpness are untouched (the "Reset" button's target).</summary>
    public bool IsColorNeutral =>
        Brightness == 0 && Contrast == 0 && Saturation == 0 && Shadows == 0 && Highlights == 0 && Sharpness == 0;

    /// <summary>The same noise settings with colour and sharpness back to neutral.</summary>
    public ProcessingSettings WithNeutralColor() =>
        this with { Brightness = 0, Contrast = 0, Saturation = 0, Shadows = 0, Highlights = 0, Sharpness = 0 };

    /// <summary>
    /// The struct passed to HitCam_BridgeSetProcessing: -100..100 becomes -1..1, 0..100 becomes 0..1. Out-of-range
    /// values (a hand-edited settings file) are clamped.
    /// </summary>
    public HitCamProcessing ToNative() => !Enabled
        ? new HitCamProcessing { ArtifactReduction = Math.Clamp(ArtifactReduction, ArtifactReductionOff, ArtifactReductionStrong) }
        : new()
    {
        TemporalStrength = Math.Clamp(TemporalStrength, 0, 100),
        ArtifactReduction = Math.Clamp(ArtifactReduction, ArtifactReductionOff, ArtifactReductionStrong),
        Brightness = Signed(Brightness),
        Contrast = Signed(Contrast),
        Saturation = Signed(Saturation),
        Sharpness = Math.Clamp(Sharpness, 0, 100) / 100f,
        Shadows = Signed(Shadows),
        Highlights = Signed(Highlights),
    };

    private static float Signed(int value) => Math.Clamp(value, -100, 100) / 100f;
}

/// <summary>Matches <c>HitCamProcessing</c> in HitCamVCam.dll (packed to 4 bytes, 32 bytes in all).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct HitCamProcessing
{
    /// <summary>0..100.</summary>
    public int TemporalStrength;
    /// <summary>0 off, 1 gentle, 2 strong.</summary>
    public int ArtifactReduction;
    /// <summary>-1..1.</summary>
    public float Brightness;
    /// <summary>-1..1.</summary>
    public float Contrast;
    /// <summary>-1..1.</summary>
    public float Saturation;
    /// <summary>0..1.</summary>
    public float Sharpness;
    /// <summary>-1..1.</summary>
    public float Shadows;
    /// <summary>-1..1.</summary>
    public float Highlights;
}

/// <summary>
/// Last frame's processing times; null when the stage did not run. <see cref="ArtifactError"/> is non-zero when
/// artifact reduction could not run (e.g. the NVIDIA runtime refused the frame size).
/// </summary>
public readonly record struct ProcessingStats(double? GpuMilliseconds, double? ArtifactMilliseconds, int ArtifactError);
