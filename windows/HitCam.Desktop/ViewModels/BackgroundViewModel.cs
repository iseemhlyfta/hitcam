using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using HitCam.Desktop.Services;
using HitCam.Vision;
using HitCam.Vision.Segmentation;
using ReactiveUI;

namespace HitCam.Desktop.ViewModels;

/// <summary>A choice in the background segments.</summary>
public sealed record BackgroundModeOption(BackgroundMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The "Background" section: on/off, blur or picture, strength, edge, what happens while the person is not found; the
/// stats. Changes are handed to <c>apply</c> at once and saved shortly after the last one. Must be used on the UI thread.
/// </summary>
public sealed class BackgroundViewModel : ReactiveObject
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly Func<bool> _modelFound;
    private readonly Action<BackgroundSettings> _apply;
    private readonly Action<BackgroundSettings> _save;
    private readonly Subject<Unit> _changed = new();
    private BackgroundSettings _settings;
    private bool _isDirty;
    private bool _hasModel;
    private bool _isActive;
    private string _statsText = "";
    private string _errorText = "";

    /// <param name="modelFound">Whether the segmentation model is installed; checked now and whenever the effect is switched on.</param>
    /// <param name="apply">Reconfigures the background; called on every change.</param>
    /// <param name="save">Persists the settings; called <see cref="SaveDelay"/> after the last change.</param>
    public BackgroundViewModel(BackgroundSettings initial, Func<bool> modelFound, Action<BackgroundSettings> apply, Action<BackgroundSettings> save,
        IScheduler ui)
    {
        _settings = initial;
        _modelFound = modelFound;
        _apply = apply;
        _save = save;
        _changed.Throttle(SaveDelay, ui).Subscribe(_ => SaveNow());
        _hasModel = modelFound();
    }

    public BackgroundSettings Settings => _settings;

    public bool IsOn
    {
        get => _settings.Enabled;
        set
        {
            if (value && !_settings.Enabled)
                HasModel = _modelFound();
            Update(_settings with { Enabled = value });
        }
    }

    public IReadOnlyList<BackgroundModeOption> Modes { get; } =
    [
        new(BackgroundMode.Blur, Loc.BackgroundBlur),
        new(BackgroundMode.Replace, Loc.BackgroundReplace),
    ];

    public BackgroundModeOption SelectedMode
    {
        get => Modes.First(m => m.Mode == _settings.Mode);
        set
        {
            if (value is null)
                return;
            ErrorText = "";
            Update(_settings with { Mode = value.Mode });
        }
    }

    public bool IsReplace => _settings.Mode == BackgroundMode.Replace;

    /// <summary>The blur strength also blurs the room while no picture is chosen.</summary>
    public bool ShowsStrength => !IsReplace || _settings.ImagePath is null;

    public double Strength
    {
        get => _settings.Strength;
        set => Update(_settings with { Strength = double.IsFinite(value) ? (int)Math.Round(value) : BackgroundSettings.DefaultStrength });
    }

    public string StrengthText => $"{_settings.Strength}%";

    public double Edge
    {
        get => _settings.Edge;
        set => Update(_settings with { Edge = double.IsFinite(value) ? (int)Math.Round(value) : BackgroundSettings.DefaultEdge });
    }

    public string EdgeText => $"{_settings.Edge}%";

    public bool BlurWhileUnknown { get => _settings.BlurWhileUnknown; set => Update(_settings with { BlurWhileUnknown = value }); }

    /// <summary>The picture's file name, or a note that none is chosen.</summary>
    public string ImageText => _settings.ImagePath is { } path ? Path.GetFileName(path) : Loc.BackgroundNoImage;

    /// <summary>A picture the user picked (from the file dialog).</summary>
    public void SetImage(string path)
    {
        ErrorText = "";
        Update(_settings with { ImagePath = path });
    }

    /// <summary>The owner could not read the picture.</summary>
    public void ShowImageFailed() => ErrorText = Loc.BackgroundImageFailed;

    public bool HasModel
    {
        get => _hasModel;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasModel, value);
            this.RaisePropertyChanged(nameof(HasNoModel));
        }
    }

    public bool HasNoModel => !HasModel;

    public string NoModelText => Loc.BackgroundNoModels(SegmentModelFiles.DefaultDirectories[^1]);

    /// <summary>Set by the owner: the effect is on, the model is there and a phone is streaming.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            this.RaiseAndSetIfChanged(ref _isActive, value);
            if (!value)
                StatsText = "";
        }
    }

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

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _errorText, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => ErrorText.Length > 0;

    public void ShowStatus(VisionStatus status)
    {
        if (!IsActive)
            return;
        switch (status.State)
        {
            case VisionState.Loading:
                StatsText = Loc.VisionLoading;
                ErrorText = "";
                break;
            case VisionState.Running:
                StatsText = Loc.BackgroundStarting(status.Provider ?? "");
                ErrorText = "";
                break;
            case VisionState.Failed:
                StatsText = "";
                ErrorText = Loc.VisionFailed(status.Error ?? "");
                break;
            default:
                StatsText = "";
                break;
        }
    }

    public void ShowResult(SegmentResult result, double compositeMilliseconds)
    {
        if (!IsActive)
            return;
        StatsText = Loc.BackgroundStats(result.Stats.AnalysisFps, result.Stats.InferenceMilliseconds, result.Stats.Provider, compositeMilliseconds);
    }

    /// <summary>Writes a change still waiting for <see cref="SaveDelay"/>; also called on exit.</summary>
    public void SaveNow()
    {
        if (!_isDirty)
            return;
        _isDirty = false;
        _save(_settings);
    }

    private void Update(BackgroundSettings settings)
    {
        if (settings == _settings)
            return;
        _settings = settings;
        foreach (var name in AllProperties)
            this.RaisePropertyChanged(name);
        _apply(_settings);
        _isDirty = true;
        _changed.OnNext(Unit.Default);
    }

    private static readonly string[] AllProperties =
    [
        nameof(IsOn), nameof(SelectedMode), nameof(IsReplace), nameof(ShowsStrength), nameof(Strength), nameof(StrengthText),
        nameof(Edge), nameof(EdgeText), nameof(BlurWhileUnknown), nameof(ImageText), nameof(Settings),
    ];
}
