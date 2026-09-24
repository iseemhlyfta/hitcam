using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using HitCam.Desktop.Services;
using ReactiveUI;

namespace HitCam.Desktop.ViewModels;

/// <param name="Level">0 off, 1 gentle, 2 strong (<see cref="ProcessingSettings.ArtifactReduction"/>).</param>
public sealed record ArtifactReductionOption(int Level, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Picture processing on this PC ("Processing" panel): noise reduction, colour and sharpness. Every change is applied
/// to the video right away and saved shortly after the last one. Must be used on the UI thread.
/// </summary>
public sealed class ProcessingViewModel : ReactiveObject
{
    // Dragging a slider changes the value dozens of times a second; the file is written once it stops.
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly Action<HitCamProcessing> _apply;
    private readonly Action<ProcessingSettings> _save;
    private readonly Subject<Unit> _changed = new();
    private ProcessingSettings _settings;
    private bool _isDirty;
    private bool _isArtifactReductionAvailable;
    private string _statsText = "";
    private string _artifactErrorText = "";

    /// <param name="apply">Hands the settings to the video pipeline; called on every change.</param>
    /// <param name="save">Persists the settings; called <see cref="SaveDelay"/> after the last change.</param>
    /// <param name="ui">Scheduler of the UI thread (virtual time in tests).</param>
    public ProcessingViewModel(ProcessingSettings initial, Action<HitCamProcessing> apply, Action<ProcessingSettings> save, IScheduler ui)
    {
        _settings = initial;
        _apply = apply;
        _save = save;
        _changed.Throttle(SaveDelay, ui).Subscribe(_ => SaveNow());

        ArtifactReductionOptions =
        [
            new(ProcessingSettings.ArtifactReductionOff, Loc.ProcessingOff),
            new(ProcessingSettings.ArtifactReductionGentle, Loc.ArtifactReductionGentle),
            new(ProcessingSettings.ArtifactReductionStrong, Loc.ArtifactReductionStrong),
        ];
        ResetColorCommand = ReactiveCommand.Create(() => Update(_settings.WithNeutralColor()), this.WhenAnyValue(x => x.IsColorAdjusted));
        Apply();
    }

    public ProcessingSettings Settings => _settings;

    public ReactiveCommand<Unit, Unit> ResetColorCommand { get; }

    // Noise reduction

    /// <summary>0 (off) to 100.</summary>
    public double TemporalStrength
    {
        get => _settings.TemporalStrength;
        set => Update(_settings with { TemporalStrength = Round(value, 0, 100) });
    }

    public string TemporalStrengthText => _settings.TemporalStrength == 0 ? Loc.ProcessingOff : $"{_settings.TemporalStrength}";

    /// <summary>Set once the background check finds an NVIDIA RTX GPU with its runtime; the choice is hidden before.</summary>
    public bool IsArtifactReductionAvailable
    {
        get => _isArtifactReductionAvailable;
        set
        {
            if (_isArtifactReductionAvailable == value)
                return;
            this.RaiseAndSetIfChanged(ref _isArtifactReductionAvailable, value);
            Apply();
        }
    }

    public IReadOnlyList<ArtifactReductionOption> ArtifactReductionOptions { get; }

    public ArtifactReductionOption SelectedArtifactReduction
    {
        get => ArtifactReductionOptions.FirstOrDefault(o => o.Level == _settings.ArtifactReduction) ?? ArtifactReductionOptions[0];
        set
        {
            // The segmented list briefly reports "nothing selected" while it rebuilds; keep the last choice then.
            if (value is null)
                return;
            Update(_settings with { ArtifactReduction = value.Level });
        }
    }

    // Colour and sharpness: -100..100 (0 neutral), sharpness 0..100.

    public double Brightness { get => _settings.Brightness; set => Update(_settings with { Brightness = Round(value, -100, 100) }); }
    public double Contrast { get => _settings.Contrast; set => Update(_settings with { Contrast = Round(value, -100, 100) }); }
    public double Saturation { get => _settings.Saturation; set => Update(_settings with { Saturation = Round(value, -100, 100) }); }
    public double Shadows { get => _settings.Shadows; set => Update(_settings with { Shadows = Round(value, -100, 100) }); }
    public double Highlights { get => _settings.Highlights; set => Update(_settings with { Highlights = Round(value, -100, 100) }); }
    public double Sharpness { get => _settings.Sharpness; set => Update(_settings with { Sharpness = Round(value, 0, 100) }); }

    public string BrightnessText => Signed(_settings.Brightness);
    public string ContrastText => Signed(_settings.Contrast);
    public string SaturationText => Signed(_settings.Saturation);
    public string ShadowsText => Signed(_settings.Shadows);
    public string HighlightsText => Signed(_settings.Highlights);
    public string SharpnessText => $"{_settings.Sharpness}";

    /// <summary>Some colour or sharpness slider is away from neutral; enables "Reset".</summary>
    public bool IsColorAdjusted => !_settings.IsColorNeutral;

    // Stats

    /// <summary>"Processing: N ms · artifacts: N ms"; empty while nothing runs.</summary>
    public string StatsText
    {
        get => _statsText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _statsText, value);
            this.RaisePropertyChanged(nameof(HasStats));
        }
    }

    public bool HasStats => StatsText.Length > 0;

    /// <summary>Why artifact reduction is not running although it is on; empty otherwise.</summary>
    public string ArtifactErrorText
    {
        get => _artifactErrorText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _artifactErrorText, value);
            this.RaisePropertyChanged(nameof(HasArtifactError));
        }
    }

    public bool HasArtifactError => ArtifactErrorText.Length > 0;

    /// <summary>Shows the last frame's stats; call about once a second while a phone is streaming.</summary>
    public void ShowStats(ProcessingStats stats)
    {
        var artifactOn = IsArtifactReductionAvailable && _settings.ArtifactReduction != ProcessingSettings.ArtifactReductionOff;
        var parts = new List<string>(2);
        if (stats.GpuMilliseconds is { } gpu)
            parts.Add(Loc.ProcessingTime(gpu));
        if (artifactOn && stats.ArtifactMilliseconds is { } artifact)
            parts.Add(Loc.ArtifactReductionTime(artifact));
        StatsText = string.Join(" · ", parts);
        ArtifactErrorText = artifactOn && stats.ArtifactError != 0 ? Loc.ArtifactReductionFailed(stats.ArtifactError) : "";
    }

    /// <summary>No video (disconnected): nothing to report.</summary>
    public void ClearStats()
    {
        StatsText = "";
        ArtifactErrorText = "";
    }

    /// <summary>What the DLL gets: artifact reduction stays off until the RTX check has passed.</summary>
    public HitCamProcessing EffectiveNative()
    {
        var native = _settings.ToNative();
        if (!IsArtifactReductionAvailable)
            native.ArtifactReduction = ProcessingSettings.ArtifactReductionOff;
        return native;
    }

    private void Update(ProcessingSettings settings)
    {
        if (settings == _settings)
            return;
        _settings = settings;
        // Every value and text may have changed (e.g. Reset); the panel is small, so raise them all.
        foreach (var name in AllProperties)
            this.RaisePropertyChanged(name);
        Apply();
        _isDirty = true;
        _changed.OnNext(Unit.Default);
    }

    /// <summary>Writes a change still waiting for <see cref="SaveDelay"/>; also called on exit.</summary>
    public void SaveNow()
    {
        if (!_isDirty)
            return;
        _isDirty = false;
        _save(_settings);
    }

    private void Apply() => _apply(EffectiveNative());

    private static readonly string[] AllProperties =
    [
        nameof(TemporalStrength), nameof(TemporalStrengthText), nameof(SelectedArtifactReduction),
        nameof(Brightness), nameof(BrightnessText), nameof(Contrast), nameof(ContrastText),
        nameof(Saturation), nameof(SaturationText), nameof(Shadows), nameof(ShadowsText),
        nameof(Highlights), nameof(HighlightsText), nameof(Sharpness), nameof(SharpnessText),
        nameof(IsColorAdjusted), nameof(Settings),
    ];

    private static int Round(double value, int min, int max) =>
        double.IsFinite(value) ? (int)Math.Clamp(Math.Round(value), min, max) : 0;

    private static string Signed(int value) => $"{value:+0;-0;0}";
}
