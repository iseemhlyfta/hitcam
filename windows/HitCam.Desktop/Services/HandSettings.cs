namespace HitCam.Desktop.Services;

/// <summary>Hand tracking ("hands" in settings.json); off by default.</summary>
// Plain setters, not init, for the same reason as AppSettings: defaults must survive fields missing from the file.
public sealed record HandSettings
{
    public bool Enabled { get; set; }

    /// <summary>Lines between the points (the hand's "skeleton"); off: only the points.</summary>
    public bool ShowSkeleton { get; set; }

    /// <summary>
    /// The finger gun: a sharp upward jerk fires a shot, shown in the preview and in the "HitCam" camera. On by
    /// default (it needs tracking on anyway).
    /// </summary>
    public bool Shots { get; set; } = true;
}
