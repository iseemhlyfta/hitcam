namespace HitCam.Desktop.Services;

/// <summary>Hand tracking ("hands" in settings.json); off by default.</summary>
// Plain setters, not init, for the same reason as AppSettings: defaults must survive fields missing from the file.
public sealed record HandSettings
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Points on the fingers in the preview. Off: the hands are still tracked (for shots) but nothing is drawn on them.
    /// </summary>
    public bool ShowPoints { get; set; } = true;

    /// <summary>Lines between the points (the hand's "skeleton"); off: only the points.</summary>
    public bool ShowSkeleton { get; set; }

    /// <summary>
    /// The finger gun: a sharp upward jerk fires a shot, shown in the preview and in the "HitCam" camera. On by
    /// default (it needs tracking on anyway).
    /// </summary>
    public bool Shots { get; set; } = true;

    public const string DefaultLeftColor = "#22D3EE";
    public const string DefaultRightColor = "#F472B6";
    public const int DefaultFillOpacity = 50;

    /// <summary>Threads between the same fingertips of the two hands, with fills between them.</summary>
    public bool Threads { get; set; } = true;

    /// <summary>Fill colour at the hand further left, "#RRGGBB".</summary>
    public string LeftColor
    {
        get => _leftColor;
        set => _leftColor = NormalizeColor(value, DefaultLeftColor);
    }

    /// <summary>Fill colour at the other hand, "#RRGGBB".</summary>
    public string RightColor
    {
        get => _rightColor;
        set => _rightColor = NormalizeColor(value, DefaultRightColor);
    }

    /// <summary>Fill opacity in percent.</summary>
    public int FillOpacity
    {
        get => _fillOpacity;
        set => _fillOpacity = Math.Clamp(value, 0, 100);
    }

    // What also goes into the "HitCam" camera picture.
    public bool CameraPoints { get; set; }

    public bool CameraThreads { get; set; } = true;

    public bool CameraFill { get; set; } = true;

    public bool CameraShots { get; set; } = true;

    private string _leftColor = DefaultLeftColor;
    private string _rightColor = DefaultRightColor;
    private int _fillOpacity = DefaultFillOpacity;

    /// <summary>0xRRGGBB of a "#RRGGBB" setting.</summary>
    public static uint ParseColor(string color) => uint.Parse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber);

    private static string NormalizeColor(string? value, string fallback)
    {
        var text = value?.Trim() ?? "";
        if (!text.StartsWith('#'))
            text = "#" + text;
        return text.Length == 7 && uint.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _)
            ? text.ToUpperInvariant()
            : fallback;
    }
}
