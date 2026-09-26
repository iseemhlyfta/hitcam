using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Media;
using HitCam.Desktop.Services;
using HitCam.Vision;
using HitCam.Vision.Faces;
using ReactiveUI;

namespace HitCam.Desktop.ViewModels;

/// <summary>A choice in the effect segments.</summary>
public sealed record FaceEffectOption(FaceEffectKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The "Faces" panel: on/off, the effect and its strength, what goes to the camera, the stats; the tracked faces for
/// the preview. Clicks on a face go to <c>toggle</c>. Changes are handed to <c>apply</c> at once and saved shortly after
/// the last one. Must be used on the UI thread.
/// </summary>
public sealed class FacesViewModel : ReactiveObject
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly Func<bool> _modelsFound;
    private readonly Action<FaceSettings> _apply;
    private readonly Action<FaceSettings> _save;
    private readonly Subject<Unit> _changed = new();
    private FaceSettings _settings;
    private bool _isDirty;
    private bool _hasModels;
    private string _statsText = "";
    private string _errorText = "";
    private IReadOnlyList<TrackedFace> _faces = [];
    private bool _isActive;

    /// <param name="modelsFound">Whether the face models are installed; checked now and whenever hiding is switched on.</param>
    /// <param name="apply">Reconfigures face hiding; called on every change.</param>
    /// <param name="save">Persists the settings; called <see cref="SaveDelay"/> after the last change.</param>
    /// <param name="toggle">Uncovers or hides a face (by its id).</param>
    /// <param name="forgetPeople">Hides everyone again.</param>
    public FacesViewModel(FaceSettings initial, Func<bool> modelsFound, Action<FaceSettings> apply, Action<FaceSettings> save,
        Action<int> toggle, Action forgetPeople, IScheduler ui)
    {
        _settings = initial;
        _modelsFound = modelsFound;
        _apply = apply;
        _save = save;
        _changed.Throttle(SaveDelay, ui).Subscribe(_ => SaveNow());
        _hasModels = modelsFound();
        ToggleCommand = ReactiveCommand.Create<int>(id =>
        {
            if (IsActive)
                toggle(id);
        });
        HideEveryoneCommand = ReactiveCommand.Create(forgetPeople);
    }

    public FaceSettings Settings => _settings;

    /// <summary>Uncovers a hidden face or hides an uncovered one; the parameter is the face's id.</summary>
    public ReactiveCommand<int, Unit> ToggleCommand { get; }

    /// <summary>Forgets who was uncovered: every face is hidden again.</summary>
    public ReactiveCommand<Unit, Unit> HideEveryoneCommand { get; }

    public bool IsEnabled
    {
        get => _settings.Enabled;
        set
        {
            if (value && !_settings.Enabled)
                HasModels = _modelsFound();
            Update(_settings with { Enabled = value });
        }
    }

    public IReadOnlyList<FaceEffectOption> Effects { get; } =
    [
        new(FaceEffectKind.Mosaic, Loc.FacesMosaic),
        new(FaceEffectKind.Blur, Loc.FacesBlur),
        new(FaceEffectKind.Fill, Loc.FacesFill),
    ];

    public FaceEffectOption SelectedEffect
    {
        get => Effects.First(e => e.Kind == _settings.Effect);
        set
        {
            if (value is not null)
                Update(_settings with { Effect = value.Kind });
        }
    }

    /// <summary>The strength slider means nothing for a fill; the colour means something only for it.</summary>
    public bool IsFill => _settings.Effect == FaceEffectKind.Fill;

    public bool HasStrength => !IsFill;

    /// <summary>Mosaic cell size or blur radius, percent.</summary>
    public double Strength
    {
        get => _settings.Strength;
        set => Update(_settings with { Strength = double.IsFinite(value) ? (int)Math.Round(value) : FaceSettings.DefaultStrength });
    }

    public string StrengthText => $"{_settings.Strength}%";

    public Color FillColor
    {
        get => Color.Parse(_settings.FillColor);
        set => Update(_settings with { FillColor = $"#{value.R:X2}{value.G:X2}{value.B:X2}" });
    }

    public bool CameraEffect { get => _settings.CameraEffect; set => Update(_settings with { CameraEffect = value }); }

    public bool CameraFrame { get => _settings.CameraFrame; set => Update(_settings with { CameraFrame = value }); }

    /// <summary>Faces of the last analysed frame, for the preview.</summary>
    public IReadOnlyList<TrackedFace> Faces { get => _faces; private set => this.RaiseAndSetIfChanged(ref _faces, value); }

    public bool HasModels
    {
        get => _hasModels;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasModels, value);
            this.RaisePropertyChanged(nameof(HasNoModels));
        }
    }

    public bool HasNoModels => !HasModels;

    public string NoModelsText => Loc.FacesNoModels(FaceModelFiles.DefaultDirectories[^1]);

    /// <summary>"Faces: 2 (hidden 1) · 30 fps · 4 ms · DirectML"; empty while nothing runs.</summary>
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

    /// <summary>Set by the owner: hiding is on and a phone is streaming. Results only count while it is.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            this.RaiseAndSetIfChanged(ref _isActive, value);
            if (!value)
                ClearResults();
        }
    }

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
                StatsText = Loc.FacesStarting(status.Provider ?? "");
                ErrorText = "";
                break;
            case VisionState.Failed:
                StatsText = "";
                ErrorText = Loc.VisionFailed(status.Error ?? "");
                Faces = [];
                break;
            default:
                ClearResults();
                break;
        }
    }

    public void ShowResult(FaceResult result)
    {
        if (!IsActive)
            return;
        Faces = result.Faces;
        StatsText = Loc.FacesStats(result.Faces.Count, result.Faces.Count(f => f.Hidden), result.Stats.AnalysisFps,
            result.Stats.InferenceMilliseconds, result.Stats.Provider);
    }

    public void ClearResults()
    {
        Faces = [];
        StatsText = "";
        ErrorText = "";
    }

    /// <summary>Writes a change still waiting for <see cref="SaveDelay"/>; also called on exit.</summary>
    public void SaveNow()
    {
        if (!_isDirty)
            return;
        _isDirty = false;
        _save(_settings);
    }

    private void Update(FaceSettings settings)
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
        nameof(IsEnabled), nameof(SelectedEffect), nameof(IsFill), nameof(HasStrength), nameof(Strength), nameof(StrengthText),
        nameof(FillColor), nameof(CameraEffect), nameof(CameraFrame), nameof(Settings),
    ];
}
