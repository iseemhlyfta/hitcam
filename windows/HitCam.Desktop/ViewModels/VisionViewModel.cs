using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using HitCam.Desktop.Services;
using HitCam.Vision;
using ReactiveUI;

namespace HitCam.Desktop.ViewModels;

/// <summary>A model in the model choice: "Fast", "Accurate" or the custom model's own name.</summary>
public sealed record ModelOption(ModelInfo Model, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One class in the class filter.</summary>
public sealed class ClassItemViewModel(string name, Action changed) : ReactiveObject
{
    private bool _isChecked = true;
    private bool _isVisible = true;

    public string Name { get; } = name;

    /// <summary>Checked classes are reported.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            this.RaiseAndSetIfChanged(ref _isChecked, value);
            changed();
        }
    }

    /// <summary>Matches the search.</summary>
    public bool IsVisible { get => _isVisible; set => this.RaiseAndSetIfChanged(ref _isVisible, value); }

    /// <summary>Sets the check without reporting a change (bulk updates report once).</summary>
    internal void SetSilently(bool isChecked) => this.RaiseAndSetIfChanged(ref _isChecked, isChecked, nameof(IsChecked));
}

/// <summary>A group of the class filter; its check box turns the whole group on or off.</summary>
public sealed class ClassGroupViewModel : ReactiveObject
{
    private readonly Action _changed;
    private bool _isVisible = true;

    public ClassGroupViewModel(ClassGroup group, Action changed)
    {
        Key = group.Key;
        Title = group.Title;
        _changed = changed;
        Items = [.. group.Classes.Select(name => new ClassItemViewModel(name, changed))];
    }

    public string Key { get; }

    public string Title { get; }

    public IReadOnlyList<ClassItemViewModel> Items { get; }

    /// <summary>True if all classes are checked, false if none, null if some.</summary>
    public bool? IsChecked
    {
        get => Items.All(i => i.IsChecked) ? true : Items.Any(i => i.IsChecked) ? null : false;
        set
        {
            // A click on a partly checked group unchecks it (CheckBox without three states goes null → false).
            var check = value == true;
            foreach (var item in Items)
                item.SetSilently(check);
            Refresh();
            _changed();
        }
    }

    public bool IsVisible { get => _isVisible; set => this.RaiseAndSetIfChanged(ref _isVisible, value); }

    internal void Refresh() => this.RaisePropertyChanged(nameof(IsChecked));
}

/// <summary>
/// The "Object analysis" panel: on/off, burn-in, model, threshold, class filter and the analysis stats. Changes are
/// handed to <c>apply</c> at once and saved shortly after the last one. Must be used on the UI thread.
/// </summary>
public sealed class VisionViewModel : ReactiveObject
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly Func<ModelCatalogResult> _scan;
    private readonly Action<VisionSettings> _apply;
    private readonly Action<VisionSettings> _save;
    private readonly Subject<Unit> _changed = new();
    private VisionSettings _settings;
    private bool _isDirty;
    private IReadOnlyList<ModelOption> _models = [];
    private IReadOnlyList<ClassGroupViewModel> _groups = [];
    private string _classSearch = "";
    private string _statsText = "";
    private string _modelText = "";
    private string _errorText = "";
    private IReadOnlyList<Track> _tracks = [];
    private bool _isActive;

    /// <param name="scan">Finds the models; called now and again whenever analysis is switched on.</param>
    /// <param name="apply">Reconfigures the analysis; called on every change.</param>
    /// <param name="save">Persists the settings; called <see cref="SaveDelay"/> after the last change.</param>
    /// <param name="ui">Scheduler of the UI thread (virtual time in tests).</param>
    public VisionViewModel(VisionSettings initial, Func<ModelCatalogResult> scan, Action<VisionSettings> apply,
        Action<VisionSettings> save, IScheduler ui)
    {
        _settings = initial;
        _scan = scan;
        _apply = apply;
        _save = save;
        _changed.Throttle(SaveDelay, ui).Subscribe(_ => SaveNow());
        SelectAllCommand = ReactiveCommand.Create(() => SetAllClasses(true));
        SelectNoneCommand = ReactiveCommand.Create(() => SetAllClasses(false));
        LoadModels();
    }

    public VisionSettings Settings => _settings;

    public ReactiveCommand<Unit, Unit> SelectAllCommand { get; }

    public ReactiveCommand<Unit, Unit> SelectNoneCommand { get; }

    /// <summary>Analysis switch; off by default.</summary>
    public bool IsEnabled
    {
        get => _settings.Enabled;
        set
        {
            if (value && !_settings.Enabled)
                LoadModels();
            Update(_settings with { Enabled = value });
        }
    }

    /// <summary>Also draw the boxes into the "HitCam" camera picture.</summary>
    public bool BurnIn { get => _settings.BurnIn; set => Update(_settings with { BurnIn = value }); }

    /// <summary>Labels on the boxes, in the preview and in the camera.</summary>
    public bool ShowLabels { get => _settings.ShowLabels; set => Update(_settings with { ShowLabels = value }); }

    // Models

    public IReadOnlyList<ModelOption> Models
    {
        get => _models;
        private set
        {
            this.RaiseAndSetIfChanged(ref _models, value);
            this.RaisePropertyChanged(nameof(HasModels));
            this.RaisePropertyChanged(nameof(HasNoModels));
            this.RaisePropertyChanged(nameof(UsesModelList));
            this.RaisePropertyChanged(nameof(UsesModelSegments));
        }
    }

    public bool HasModels => Models.Count > 0;

    public bool HasNoModels => !HasModels;

    /// <summary>Two or three models fit in a segmented control; more go into a drop-down list.</summary>
    public bool UsesModelSegments => Models.Count is > 0 and <= 3;

    public bool UsesModelList => Models.Count > 3;

    /// <summary>Where to put models when none were found.</summary>
    public string NoModelsText => Loc.VisionNoModels(ModelCatalog.UserModelsDirectory);

    /// <summary>The saved model, or the first one if it is gone.</summary>
    public ModelOption? SelectedModel
    {
        get => Models.FirstOrDefault(m => string.Equals(m.Model.Id, _settings.ModelId, StringComparison.OrdinalIgnoreCase))
               ?? Models.FirstOrDefault();
        set
        {
            // Segmented lists briefly report "nothing selected" while they rebuild; keep the last choice then.
            if (value is null)
                return;
            var changed = !string.Equals(value.Model.Id, SelectedModel?.Model.Id, StringComparison.OrdinalIgnoreCase);
            Update(_settings with { ModelId = value.Model.Id });
            if (changed)
                BuildGroups();
        }
    }

    // Threshold

    /// <summary>Minimum confidence, percent.</summary>
    public double Threshold
    {
        get => _settings.Threshold;
        set => Update(_settings with
        {
            Threshold = double.IsFinite(value)
                ? (int)Math.Clamp(Math.Round(value), VisionSettings.MinThreshold, VisionSettings.MaxThreshold)
                : VisionSettings.DefaultThreshold,
        });
    }

    public string ThresholdText => $"{_settings.Threshold}%";

    // Class filter

    public IReadOnlyList<ClassGroupViewModel> Groups { get => _groups; private set => this.RaiseAndSetIfChanged(ref _groups, value); }

    /// <summary>Filters the list by class or group name.</summary>
    public string ClassSearch
    {
        get => _classSearch;
        set
        {
            this.RaiseAndSetIfChanged(ref _classSearch, value ?? "");
            ApplySearch();
        }
    }

    /// <summary>"All classes", "12 of 80 classes" or "No classes".</summary>
    public string ClassFilterText
    {
        get
        {
            var total = Groups.Sum(g => g.Items.Count);
            var on = Groups.Sum(g => g.Items.Count(i => i.IsChecked));
            return on == total ? Loc.VisionAllClasses : on == 0 ? Loc.VisionNoClasses : Loc.VisionSomeClasses(on, total);
        }
    }

    // Status

    /// <summary>"Analysis: 24 fps · 12 ms · DirectML"; empty while nothing runs.</summary>
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

    /// <summary>The running model's own name, e.g. "RF-DETR Nano (COCO)".</summary>
    public string ModelText
    {
        get => _modelText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _modelText, value);
            this.RaisePropertyChanged(nameof(HasModelText));
        }
    }

    public bool HasModelText => ModelText.Length > 0;

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

    /// <summary>Tracks of the last analysed frame, for the preview overlay.</summary>
    public IReadOnlyList<Track> Tracks { get => _tracks; private set => this.RaiseAndSetIfChanged(ref _tracks, value); }

    /// <summary>Set by the owner: analysis is on and a phone is streaming. Results only count while it is.</summary>
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

    /// <summary>What the detector gets for the selected model.</summary>
    public DetectionOptions? CurrentOptions => SelectedModel is { } model ? _settings.ToOptions(model.Model) : null;

    public void ShowStatus(VisionStatus status)
    {
        if (!IsActive)
            return;
        switch (status.State)
        {
            case VisionState.Loading:
                StatsText = Loc.VisionLoading;
                ErrorText = "";
                ModelText = status.Model?.Name ?? "";
                break;
            case VisionState.Running:
                StatsText = Loc.VisionStarting(status.Provider ?? "");
                ErrorText = "";
                ModelText = status.Model?.Name ?? "";
                break;
            case VisionState.Failed:
                StatsText = "";
                ErrorText = Loc.VisionFailed(status.Error ?? "");
                Tracks = [];
                break;
            default:
                ClearResults();
                break;
        }
    }

    public void ShowResult(VisionResult result)
    {
        if (!IsActive)
            return;
        Tracks = result.Tracks;
        StatsText = Loc.VisionStats(result.Stats.AnalysisFps, result.Stats.InferenceMilliseconds, result.Stats.Provider);
        ModelText = result.Model.Name;
    }

    /// <summary>Nothing analysed (off, disconnected): no boxes, no stats.</summary>
    public void ClearResults()
    {
        Tracks = [];
        StatsText = "";
        ModelText = "";
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

    private void LoadModels()
    {
        var catalog = _scan();
        var previous = SelectedModel?.Model.Id;
        Models = [.. catalog.Models.Select(m => new ModelOption(m, Label(m)))];
        this.RaisePropertyChanged(nameof(SelectedModel));
        if (Groups.Count == 0 || previous != SelectedModel?.Model.Id)
            BuildGroups();
    }

    private static string Label(ModelInfo model) => model.Role switch
    {
        ModelRole.Fast => Loc.VisionModelFast,
        ModelRole.Accurate => Loc.VisionModelAccurate,
        _ => model.Name,
    };

    /// <summary>The selected model's classes in groups, checked unless excluded by name.</summary>
    private void BuildGroups()
    {
        var names = SelectedModel?.Model.Classes.Values ?? (IEnumerable<string>)ClassGroups.CocoGroups.SelectMany(g => g.Classes);
        var excluded = new HashSet<string>(_settings.ExcludedClasses, StringComparer.OrdinalIgnoreCase);
        var groups = ClassGroups.For(names.Distinct(StringComparer.OrdinalIgnoreCase))
            .Select(g => new ClassGroupViewModel(g, OnClassesChanged))
            .ToList();
        foreach (var item in groups.SelectMany(g => g.Items))
            item.SetSilently(!excluded.Contains(item.Name));
        Groups = groups;
        ApplySearch();
        this.RaisePropertyChanged(nameof(ClassFilterText));
    }

    private void ApplySearch()
    {
        var query = _classSearch.Trim();
        foreach (var group in Groups)
        {
            var groupMatches = query.Length == 0 || group.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            foreach (var item in group.Items)
                item.IsVisible = groupMatches || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            group.IsVisible = group.Items.Any(i => i.IsVisible);
        }
    }

    private void SetAllClasses(bool check)
    {
        foreach (var group in Groups)
        {
            foreach (var item in group.Items)
                item.SetSilently(check);
            group.Refresh();
        }
        OnClassesChanged();
    }

    private void OnClassesChanged()
    {
        foreach (var group in Groups)
            group.Refresh();
        // Classes this model does not know keep their state (they may matter for another model).
        var known = Groups.SelectMany(g => g.Items).ToList();
        var knownNames = new HashSet<string>(known.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        var excluded = _settings.ExcludedClasses.Where(n => !knownNames.Contains(n))
            .Concat(known.Where(i => !i.IsChecked).Select(i => i.Name));
        Update(_settings with { ExcludedClasses = [.. excluded] });
        this.RaisePropertyChanged(nameof(ClassFilterText));
    }

    private void Update(VisionSettings settings)
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
        nameof(IsEnabled), nameof(BurnIn), nameof(ShowLabels), nameof(SelectedModel), nameof(Threshold), nameof(ThresholdText),
        nameof(Settings), nameof(CurrentOptions),
    ];
}
