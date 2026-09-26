using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XcpNgCenter.Shell.Services.Performance;

namespace XcpNgCenter.Shell.ViewModels;

public sealed class GraphLayoutDraft : ObservableObject
{
    private readonly Func<bool> _canEdit;
    private readonly Action _changed;
    private string _title;
    internal ObservableCollection<GraphDataSourceOption> MutableSeries { get; }

    internal GraphLayoutDraft(string title, IEnumerable<GraphDataSourceOption> series, Func<bool> canEdit, Action changed)
    {
        _title = title;
        _canEdit = canEdit;
        _changed = changed;
        MutableSeries = new(series);
        Series = new(MutableSeries);
    }

    public ReadOnlyObservableCollection<GraphDataSourceOption> Series { get; }
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled graph" : Title;
    public string Title
    {
        get => _title;
        set
        {
            if (!_canEdit() || !SetProperty(ref _title, value ?? "")) return;
            OnPropertyChanged(nameof(DisplayTitle));
            _changed();
        }
    }
}

/// <summary>Owns an isolated draft; only Save passes a copied layout to persistence.</summary>
public sealed class GraphLayoutEditorViewModel : ViewModelBase
{
    private readonly ObservableCollection<GraphLayoutDraft> _graphs = [];
    private readonly IReadOnlyList<GraphDataSourceOption> _catalog;
    private readonly Func<IReadOnlyList<GraphLayoutDefinition>, Task> _save;
    private readonly Action _close;
    private IReadOnlyList<GraphDataSourceOption> _availableSources = Array.Empty<GraphDataSourceOption>();
    private GraphLayoutDraft? _selectedGraph;
    private GraphDataSourceOption? _selectedSeries;
    private GraphDataSourceOption? _selectedAvailableSource;
    private bool _isSaving;
    private bool _closed;
    private bool _changingCollections;
    private string _statusMessage = "";
    private string _validationMessage = "";

    public GraphLayoutEditorViewModel(IReadOnlyList<GraphLayoutDefinition> graphs,
        IReadOnlyList<GraphDataSourceOption> sources,
        Func<IReadOnlyList<GraphLayoutDefinition>, Task> save, Action close)
    {
        _save = save;
        _close = close;
        _catalog = Array.AsReadOnly(sources.GroupBy(source => source.Leaf, StringComparer.Ordinal)
            .Select(group => group.First()).ToArray());
        Graphs = new(_graphs);
        AddGraphCommand = new RelayCommand(AddGraph, () => CanEdit);
        RemoveGraphCommand = new RelayCommand(RemoveGraph, () => CanEditGraph);
        MoveGraphUpCommand = new RelayCommand(() => MoveGraph(-1), () => CanMoveGraph(-1));
        MoveGraphDownCommand = new RelayCommand(() => MoveGraph(1), () => CanMoveGraph(1));
        AddSeriesCommand = new RelayCommand(AddSeries, () => CanAddSeries);
        RemoveSeriesCommand = new RelayCommand(RemoveSeries, () => CanRemoveSeries);
        MoveSeriesUpCommand = new RelayCommand(() => MoveSeries(-1), () => CanMoveSeries(-1));
        MoveSeriesDownCommand = new RelayCommand(() => MoveSeries(1), () => CanMoveSeries(1));
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave);
        CancelCommand = new RelayCommand(Cancel, () => CanEdit);

        foreach (var graph in graphs)
            _graphs.Add(CreateDraft(graph.Title, graph.DataSourceLeaves.Select(FindSource)));
        _selectedGraph = _graphs.FirstOrDefault();
        _selectedSeries = _selectedGraph?.Series.FirstOrDefault();
        RefreshState();
    }

    public ReadOnlyObservableCollection<GraphLayoutDraft> Graphs { get; }
    public IReadOnlyList<GraphDataSourceOption> AvailableSources => _availableSources;
    public bool CanEdit => !IsSaving && !_closed;
    public bool CanEditGraph => CanEdit && SelectedGraph != null;
    public bool CanSave => CanEdit && ValidationMessage.Length == 0;
    public bool CanAddSeries => CanEditGraph && SelectedAvailableSource != null && AvailableSources.Contains(SelectedAvailableSource);
    public bool CanRemoveSeries => CanEditGraph && SelectedSeries != null && SelectedGraph!.Series.Contains(SelectedSeries);
    public bool HasUnavailableSeries => SelectedGraph?.Series.Any(source => !source.IsAvailable) == true;
    public bool HasAvailableSources => AvailableSources.Count > 0;
    public bool HasValidationMessage => ValidationMessage.Length > 0;
    public bool HasStatusMessage => StatusMessage.Length > 0;
    public string ValidationMessage => _validationMessage;
    public string StatusMessage => _statusMessage;

    public bool IsSaving
    {
        get => _isSaving;
        private set { if (SetProperty(ref _isSaving, value)) RefreshState(); }
    }
    public GraphLayoutDraft? SelectedGraph
    {
        get => _selectedGraph;
        set
        {
            if (!CanEdit || _changingCollections || value != null && !_graphs.Contains(value) || !SetProperty(ref _selectedGraph, value)) return;
            SetProperty(ref _selectedSeries, value?.Series.FirstOrDefault(), nameof(SelectedSeries));
            RefreshState();
        }
    }
    public GraphDataSourceOption? SelectedSeries
    {
        get => _selectedSeries;
        set
        {
            if (!CanEdit || _changingCollections || value != null && SelectedGraph?.Series.Contains(value) != true || !SetProperty(ref _selectedSeries, value)) return;
            RefreshState();
        }
    }
    public GraphDataSourceOption? SelectedAvailableSource
    {
        get => _selectedAvailableSource;
        set
        {
            if (!CanEdit || _changingCollections) return;
            if (value != null && !AvailableSources.Contains(value)) value = null;
            if (!SetProperty(ref _selectedAvailableSource, value)) return;
            AddSeriesCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanAddSeries));
        }
    }

    public IRelayCommand AddGraphCommand { get; }
    public IRelayCommand RemoveGraphCommand { get; }
    public IRelayCommand MoveGraphUpCommand { get; }
    public IRelayCommand MoveGraphDownCommand { get; }
    public IRelayCommand AddSeriesCommand { get; }
    public IRelayCommand RemoveSeriesCommand { get; }
    public IRelayCommand MoveSeriesUpCommand { get; }
    public IRelayCommand MoveSeriesDownCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }

    private GraphDataSourceOption FindSource(string leaf) => _catalog.FirstOrDefault(source => source.Leaf == leaf)
        ?? new GraphDataSourceOption(leaf, string.IsNullOrWhiteSpace(leaf) ? "Missing source identifier" : leaf, "", false);
    private GraphLayoutDraft CreateDraft(string title, IEnumerable<GraphDataSourceOption> series)
        => new(title, series, () => CanEdit && !_changingCollections, RefreshState);

    private void AddGraph()
    {
        if (!CanEdit) return;
        var number = 1;
        while (_graphs.Any(graph => graph.Title == $"Graph {number}")) number++;
        var draft = CreateDraft($"Graph {number}", []);
        ChangeCollection(() => _graphs.Add(draft));
        SelectedGraph = draft;
    }
    private void RemoveGraph()
    {
        if (!CanEditGraph) return;
        var index = _graphs.IndexOf(SelectedGraph!);
        ChangeCollection(() => _graphs.RemoveAt(index));
        SelectedGraph = _graphs.Count == 0 ? null : _graphs[Math.Min(index, _graphs.Count - 1)];
    }
    private bool CanMoveGraph(int offset) => CanEditGraph && _graphs.IndexOf(SelectedGraph!) + offset >= 0
        && _graphs.IndexOf(SelectedGraph!) + offset < _graphs.Count;
    private void MoveGraph(int offset)
    {
        if (!CanMoveGraph(offset)) return;
        var graph = SelectedGraph!;
        var source = SelectedSeries;
        var index = _graphs.IndexOf(SelectedGraph!);
        ChangeCollection(() =>
        {
            _graphs.Move(index, index + offset);
            // Avalonia filters same-value binding notifications. Publish a guarded
            // transition so the list also restores its cleared visual selection.
            SetProperty(ref _selectedGraph, null, nameof(SelectedGraph));
            SetProperty(ref _selectedGraph, graph, nameof(SelectedGraph));
            RestoreSeriesSelection(source);
        });
        RefreshState();
    }
    private void AddSeries()
    {
        if (!CanAddSeries) return;
        var source = SelectedAvailableSource!;
        ChangeCollection(() => SelectedGraph!.MutableSeries.Add(source));
        SetProperty(ref _selectedSeries, source, nameof(SelectedSeries));
        RefreshState();
    }
    private void RemoveSeries()
    {
        if (!CanRemoveSeries) return;
        var series = SelectedGraph!.MutableSeries;
        var index = series.IndexOf(SelectedSeries!);
        ChangeCollection(() =>
        {
            series.RemoveAt(index);
            RestoreSeriesSelection(series.Count == 0 ? null : series[Math.Min(index, series.Count - 1)]);
        });
        RefreshState();
    }
    private bool CanMoveSeries(int offset) => CanRemoveSeries && SelectedGraph!.Series.IndexOf(SelectedSeries!) + offset >= 0
        && SelectedGraph.Series.IndexOf(SelectedSeries!) + offset < SelectedGraph.Series.Count;
    private void MoveSeries(int offset)
    {
        if (!CanMoveSeries(offset)) return;
        var series = SelectedGraph!.MutableSeries;
        var source = SelectedSeries;
        var index = series.IndexOf(SelectedSeries!);
        ChangeCollection(() =>
        {
            series.Move(index, index + offset);
            RestoreSeriesSelection(source);
        });
        RefreshState();
    }

    private void RestoreSeriesSelection(GraphDataSourceOption? source)
    {
        SetProperty(ref _selectedSeries, null, nameof(SelectedSeries));
        SetProperty(ref _selectedSeries, source, nameof(SelectedSeries));
    }

    private void ChangeCollection(Action change)
    {
        // List controls can feed a temporary null selection back through their
        // two-way binding while processing Move/Remove or a new ItemsSource.
        var previous = _changingCollections;
        _changingCollections = true;
        try { change(); }
        finally { _changingCollections = previous; }
    }

    private IReadOnlyList<GraphLayoutDefinition> Snapshot() => Array.AsReadOnly(_graphs.Select((graph, index) =>
        new GraphLayoutDefinition(string.IsNullOrWhiteSpace(graph.Title) ? $"Graph {index + 1}" : graph.Title,
            Array.AsReadOnly(graph.Series.Select(source => source.Leaf).ToArray()))).ToArray());

    private void RefreshState()
    {
        var validation = "";
        try { ShellGraphLayout.Validate(Snapshot()); }
        catch (InvalidOperationException error) { validation = error.Message; }
        SetProperty(ref _validationMessage, validation, nameof(ValidationMessage));
        var available = _catalog.Where(source => source.IsAvailable && !string.IsNullOrWhiteSpace(source.Leaf)
            && SelectedGraph != null && !SelectedGraph.Series.Any(existing => existing.Leaf == source.Leaf)).ToArray();
        ChangeCollection(() =>
        {
            if (!_availableSources.SequenceEqual(available))
            {
                var selection = _selectedAvailableSource != null && available.Contains(_selectedAvailableSource)
                    ? _selectedAvailableSource : available.FirstOrDefault();
                SetProperty(ref _availableSources, Array.AsReadOnly(available), nameof(AvailableSources));
                SetProperty(ref _selectedAvailableSource, null, nameof(SelectedAvailableSource));
                SetProperty(ref _selectedAvailableSource, selection, nameof(SelectedAvailableSource));
            }
        });
        OnPropertyChanged(nameof(SelectedAvailableSource));
        foreach (var property in new[] { nameof(CanEdit), nameof(CanEditGraph), nameof(CanSave), nameof(CanAddSeries),
                     nameof(CanRemoveSeries), nameof(HasAvailableSources), nameof(HasUnavailableSeries),
                     nameof(HasValidationMessage), nameof(HasStatusMessage) })
            OnPropertyChanged(property);
        foreach (var command in new[] { AddGraphCommand, RemoveGraphCommand, MoveGraphUpCommand, MoveGraphDownCommand,
                     AddSeriesCommand, RemoveSeriesCommand, MoveSeriesUpCommand, MoveSeriesDownCommand, CancelCommand })
            command.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }
    private void Cancel()
    {
        if (!CanEdit) return;
        _closed = true;
        RefreshState();
        _close();
    }
    private async Task SaveAsync()
    {
        if (!CanSave) return;
        var snapshot = Snapshot();
        IsSaving = true;
        SetProperty(ref _statusMessage, "Saving graph layout...", nameof(StatusMessage));
        OnPropertyChanged(nameof(HasStatusMessage));
        try
        {
            await _save(snapshot);
            _closed = true;
            IsSaving = false;
            _close();
        }
        catch (Exception error)
        {
            SetProperty(ref _statusMessage, $"The layout could not be saved: {error.Message}", nameof(StatusMessage));
        }
        finally { IsSaving = false; }
    }
}
