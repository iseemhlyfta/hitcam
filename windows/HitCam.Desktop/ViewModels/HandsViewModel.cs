using System.Diagnostics;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using HitCam.Desktop.Services;
using HitCam.Desktop.Views;
using HitCam.Vision;
using HitCam.Vision.Hands;
using ReactiveUI;

namespace HitCam.Desktop.ViewModels;

/// <summary>
/// The "Hands" panel: on/off, skeleton lines and the tracking stats; the tracked hands for the preview overlay.
/// Changes are handed to <c>apply</c> at once and saved shortly after the last one. Must be used on the UI thread.
/// </summary>
public sealed class HandsViewModel : ReactiveObject
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly Func<bool> _modelsFound;
    private readonly Action<HandSettings> _apply;
    private readonly Action<HandSettings> _save;
    private readonly Subject<Unit> _changed = new();
    private HandSettings _settings;
    private bool _isDirty;
    private bool _hasModels;
    private string _statsText = "";
    private string _errorText = "";
    private IReadOnlyList<TrackedHand> _hands = [];
    private IReadOnlyList<FiredShot> _shots = [];
    private bool _isActive;

    /// <param name="modelsFound">Whether the hand models are installed; checked now and whenever tracking is switched on.</param>
    /// <param name="apply">Reconfigures tracking; called on every change.</param>
    /// <param name="save">Persists the settings; called <see cref="SaveDelay"/> after the last change.</param>
    public HandsViewModel(HandSettings initial, Func<bool> modelsFound, Action<HandSettings> apply, Action<HandSettings> save, IScheduler ui)
    {
        _settings = initial;
        _modelsFound = modelsFound;
        _apply = apply;
        _save = save;
        _changed.Throttle(SaveDelay, ui).Subscribe(_ => SaveNow());
        _hasModels = modelsFound();
    }

    public HandSettings Settings => _settings;

    /// <summary>Tracking switch; off by default.</summary>
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

    /// <summary>Points (and lines, if on) on the hands in the preview.</summary>
    public bool ShowPoints { get => _settings.ShowPoints; set => Update(_settings with { ShowPoints = value }); }

    public bool ShowSkeleton { get => _settings.ShowSkeleton; set => Update(_settings with { ShowSkeleton = value }); }

    /// <summary>Finger-gun shots: in the preview and the camera.</summary>
    public bool ShotsEnabled { get => _settings.Shots; set => Update(_settings with { Shots = value }); }

    /// <summary>Shots still playing in the preview (at most a quarter of a second old).</summary>
    public IReadOnlyList<FiredShot> Shots { get => _shots; private set => this.RaiseAndSetIfChanged(ref _shots, value); }

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

    /// <summary>Where to put the models when they are missing.</summary>
    public string NoModelsText => Loc.HandsNoModels(HandModelFiles.DefaultDirectories[^1]);

    /// <summary>"Hands: 30 fps · 4 ms · DirectML"; empty while nothing runs.</summary>
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

    /// <summary>Hands of the last analysed frame, for the preview overlay.</summary>
    public IReadOnlyList<TrackedHand> Hands { get => _hands; private set => this.RaiseAndSetIfChanged(ref _hands, value); }

    /// <summary>Set by the owner: tracking is on and a phone is streaming. Results only count while it is.</summary>
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
                StatsText = Loc.HandsStarting(status.Provider ?? "");
                ErrorText = "";
                break;
            case VisionState.Failed:
                StatsText = "";
                ErrorText = Loc.VisionFailed(status.Error ?? "");
                Hands = [];
                break;
            default:
                ClearResults();
                break;
        }
    }

    public void ShowResult(HandResult result)
    {
        if (!IsActive)
            return;
        Hands = result.Hands;
        if (result.Shots.Count > 0 && _settings.Shots)
        {
            var now = Stopwatch.GetTimestamp();
            Shots = [.. Shots.Where(s => Stopwatch.GetElapsedTime(s.Timestamp).TotalMilliseconds < ShotEffect.DurationMs),
                .. result.Shots.Select(s => new FiredShot(s, now))];
        }
        StatsText = Loc.HandsStats(result.Hands.Count, result.Stats.AnalysisFps, result.Stats.InferenceMilliseconds, result.Stats.Provider);
    }

    public void ClearResults()
    {
        Hands = [];
        Shots = [];
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

    private void Update(HandSettings settings)
    {
        if (settings == _settings)
            return;
        _settings = settings;
        this.RaisePropertyChanged(nameof(IsEnabled));
        this.RaisePropertyChanged(nameof(ShowPoints));
        this.RaisePropertyChanged(nameof(ShowSkeleton));
        this.RaisePropertyChanged(nameof(ShotsEnabled));
        this.RaisePropertyChanged(nameof(Settings));
        _apply(_settings);
        _isDirty = true;
        _changed.OnNext(Unit.Default);
    }
}
