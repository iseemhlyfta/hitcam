using System.Globalization;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using HitCam.Desktop.Services;
using HitCam.Vision.Faces;
using ReactiveUI;

namespace HitCam.Desktop.ViewModels;

/// <summary>
/// The "Auto-framing" section: on/off and how close the camera may get. It follows the faces, so it needs the face
/// models. Changes are handed to <c>apply</c> at once and saved shortly after the last one. Must be used on the UI thread.
/// </summary>
public sealed class FramingViewModel : ReactiveObject
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly Func<bool> _modelsFound;
    private readonly Action<FramingSettings> _apply;
    private readonly Action<FramingSettings> _save;
    private readonly Subject<Unit> _changed = new();
    private FramingSettings _settings;
    private bool _isDirty;
    private bool _hasModels;

    public FramingViewModel(FramingSettings initial, Func<bool> modelsFound, Action<FramingSettings> apply, Action<FramingSettings> save,
        IScheduler ui)
    {
        _settings = initial;
        _modelsFound = modelsFound;
        _apply = apply;
        _save = save;
        _changed.Throttle(SaveDelay, ui).Subscribe(_ => SaveNow());
        _hasModels = modelsFound();
    }

    public FramingSettings Settings => _settings;

    public bool IsOn
    {
        get => _settings.Enabled;
        set
        {
            if (value && !_settings.Enabled)
                HasModels = _modelsFound();
            Update(_settings with { Enabled = value });
        }
    }

    /// <summary>Percent, 110..200.</summary>
    public double MaxZoom
    {
        get => _settings.MaxZoom;
        set => Update(_settings with { MaxZoom = double.IsFinite(value) ? (int)Math.Round(value / 10) * 10 : FramingSettings.DefaultMaxZoom });
    }

    public string MaxZoomText => (_settings.MaxZoom / 100.0).ToString("0.0", CultureInfo.CurrentCulture) + "×";

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

    public string NoModelsText => Loc.FramingNoModels(FaceModelFiles.DefaultDirectories[^1]);

    public void SaveNow()
    {
        if (!_isDirty)
            return;
        _isDirty = false;
        _save(_settings);
    }

    private void Update(FramingSettings settings)
    {
        if (settings == _settings)
            return;
        _settings = settings;
        foreach (var name in new[] { nameof(IsOn), nameof(MaxZoom), nameof(MaxZoomText), nameof(Settings) })
            this.RaisePropertyChanged(name);
        _apply(_settings);
        _isDirty = true;
        _changed.OnNext(Unit.Default);
    }
}
