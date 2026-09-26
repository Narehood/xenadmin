using XcpNgCenter.Shell.Services.Performance;
using XcpNgCenter.Shell.ViewModels;
using System.Collections.Specialized;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class GraphLayoutEditorTests
{
    private static readonly GraphDataSourceOption Cpu = new("cpu0", "CPU 0", "%", true);
    private static readonly GraphDataSourceOption Network = new("vif_0_rx", "Network received", "bytes/s", true);
    private static readonly GraphDataSourceOption Memory = new("memory", "Memory (used/free/total)", "bytes", true);
    private static IReadOnlyList<GraphDataSourceOption> Catalog => [Cpu, Network, Memory];

    [Fact]
    public void DraftEditsAndCancelLeaveOriginalLayoutUntouched()
    {
        var original = new[] { new GraphLayoutDefinition("Original", ["cpu0"]) };
        var f = new Fixture(original);
        f.Editor.SelectedGraph!.Title = "Edited";
        f.Editor.SelectedAvailableSource = Memory;
        f.Editor.AddSeriesCommand.Execute(null);
        f.Editor.AddGraphCommand.Execute(null);
        f.Editor.CancelCommand.Execute(null);

        Assert.Equal("Original", original[0].Title);
        Assert.Equal(new[] { "cpu0" }, original[0].DataSourceLeaves);
        Assert.Empty(f.Saves);
        Assert.Equal(1, f.Closes);
        Assert.False(f.Editor.CanSave);
        f.Editor.CancelCommand.Execute(null);
        Assert.Equal(1, f.Closes);
    }

    [Fact]
    public void ConstructorCopiesTheGraphListAndCatalog()
    {
        var graphs = new List<GraphLayoutDefinition> { new("Original", ["cpu0"]) };
        var sources = Catalog.ToList();
        var editor = new GraphLayoutEditorViewModel(graphs, sources, _ => Task.CompletedTask, () => { });
        graphs.Clear();
        sources.Clear();

        Assert.Single(editor.Graphs);
        Assert.Equal("Original", editor.SelectedGraph!.Title);
        Assert.Equal(new[] { "vif_0_rx", "memory" }, editor.AvailableSources.Select(source => source.Leaf));
    }

    [Fact]
    public async Task SavePreservesEditedGraphAndSeriesOrder()
    {
        var f = new Fixture([new("Compute", ["cpu0", "memory"]), new("Network", ["vif_0_rx"])]);
        var compute = f.Editor.SelectedGraph!;
        compute.Title = "Processor and memory";
        f.Editor.SelectedSeries = Memory;
        f.Editor.MoveSeriesUpCommand.Execute(null);
        f.Editor.MoveGraphDownCommand.Execute(null);
        Assert.Same(compute, f.Editor.SelectedGraph);
        Assert.Same(Memory, f.Editor.SelectedSeries);
        await f.Editor.SaveCommand.ExecuteAsync(null);

        var saved = Assert.Single(f.Saves);
        Assert.Equal(new[] { "Network", "Processor and memory" }, saved.Select(graph => graph.Title));
        Assert.Equal(new[] { "memory", "cpu0" }, saved[1].DataSourceLeaves);
        Assert.Equal(1, f.Closes);
        Assert.Throws<NotSupportedException>(() => ((IList<GraphLayoutDefinition>)saved).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)saved[1].DataSourceLeaves).Clear());
    }

    [Fact]
    public void AddAndRemoveSeriesUpdateTheAvailableCatalogAndPreventDuplicates()
    {
        var f = new Fixture([new("Compute", ["cpu0"])]);
        Assert.DoesNotContain(Cpu, f.Editor.AvailableSources);
        f.Editor.SelectedAvailableSource = Memory;
        f.Editor.AddSeriesCommand.Execute(null);
        Assert.Equal(new[] { "cpu0", "memory" }, f.Editor.SelectedGraph!.Series.Select(source => source.Leaf));
        Assert.DoesNotContain(Memory, f.Editor.AvailableSources);
        f.Editor.SelectedAvailableSource = Memory; // A stale UI selection cannot add the same leaf again.
        f.Editor.AddSeriesCommand.Execute(null);
        Assert.Equal(2, f.Editor.SelectedGraph.Series.Count);
        Assert.Equal(f.Editor.SelectedGraph.Series.Count,
            f.Editor.SelectedGraph.Series.Select(source => source.Leaf).Distinct().Count());

        f.Editor.SelectedSeries = Memory;
        f.Editor.RemoveSeriesCommand.Execute(null);
        Assert.Contains(Memory, f.Editor.AvailableSources);
        Assert.DoesNotContain(Memory, f.Editor.SelectedGraph.Series);
    }

    [Fact]
    public void NewGraphsStartEmptyAndCanBeRemovedWithoutChangingOtherDrafts()
    {
        var f = new Fixture([new("Graph 1", ["cpu0"])]);
        var originalDraft = f.Editor.SelectedGraph;
        f.Editor.AddGraphCommand.Execute(null);
        Assert.Equal("Graph 2", f.Editor.SelectedGraph!.Title);
        Assert.Empty(f.Editor.SelectedGraph.Series);
        Assert.False(f.Editor.CanSave);
        Assert.True(f.Editor.HasValidationMessage);
        f.Editor.SelectedAvailableSource = Network;
        f.Editor.AddSeriesCommand.Execute(null);
        Assert.True(f.Editor.CanSave);
        f.Editor.RemoveGraphCommand.Execute(null);
        Assert.Same(originalDraft, f.Editor.SelectedGraph);
        Assert.Single(f.Editor.Graphs);
        Assert.True(f.Editor.CanSave);
    }

    [Fact]
    public async Task UnavailableExistingSourcesStayInSavedLayoutUntilExplicitlyRemoved()
    {
        var f = new Fixture([new("Legacy graph", ["missing_device", "memory"])]);
        Assert.True(f.Editor.HasUnavailableSeries);
        var missing = f.Editor.SelectedGraph!.Series[0];
        Assert.Equal("missing_device", missing.Leaf);
        Assert.False(missing.IsAvailable);
        Assert.DoesNotContain(missing, f.Editor.AvailableSources);
        Assert.Contains("used/free/total", f.Editor.SelectedGraph.Series[1].Label);
        Assert.True(f.Editor.CanSave);
        await f.Editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "missing_device", "memory" }, Assert.Single(f.Saves)[0].DataSourceLeaves);
    }

    [Fact]
    public void RemovedUnavailableSourceCannotBeAddedBackFromTheCatalog()
    {
        var unavailable = new GraphDataSourceOption("missing_device", "Old device", "bytes/s", false);
        var editor = new GraphLayoutEditorViewModel([new("Legacy", ["missing_device", "cpu0"])],
            [.. Catalog, unavailable], _ => Task.CompletedTask, () => { });
        editor.RemoveSeriesCommand.Execute(null);
        Assert.False(editor.HasUnavailableSeries);
        Assert.DoesNotContain(unavailable, editor.AvailableSources);
        editor.SelectedAvailableSource = unavailable;
        Assert.NotEqual(unavailable, editor.SelectedAvailableSource);
    }

    [Fact]
    public async Task EmptyGraphsAndMissingSeriesCannotReachPersistence()
    {
        var f = new Fixture([]);
        Assert.False(f.Editor.CanSave);
        Assert.False(f.Editor.SaveCommand.CanExecute(null));
        await f.Editor.SaveCommand.ExecuteAsync(null);
        f.Editor.AddGraphCommand.Execute(null);
        Assert.False(f.Editor.CanSave);
        await f.Editor.SaveCommand.ExecuteAsync(null);
        f.Editor.AddSeriesCommand.Execute(null);
        Assert.True(f.Editor.CanSave);
        f.Editor.RemoveSeriesCommand.Execute(null);
        Assert.False(f.Editor.CanSave);
        f.Editor.RemoveGraphCommand.Execute(null);
        Assert.Null(f.Editor.SelectedGraph);
        Assert.False(f.Editor.CanSave);
        Assert.Empty(f.Saves);
    }

    [Fact]
    public async Task FailedSaveRetainsTheDraftAndAllowsRetry()
    {
        var attempts = 0;
        var closes = 0;
        IReadOnlyList<GraphLayoutDefinition>? saved = null;
        var editor = new GraphLayoutEditorViewModel([new("Original", ["cpu0"])], Catalog, layout =>
        {
            if (++attempts == 1) throw new InvalidOperationException("Permission denied");
            saved = layout;
            return Task.CompletedTask;
        }, () => closes++);
        editor.SelectedGraph!.Title = "My draft";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, closes);
        Assert.Equal("My draft", editor.SelectedGraph.Title);
        Assert.Contains("Permission denied", editor.StatusMessage);
        Assert.True(editor.HasStatusMessage);
        Assert.False(editor.IsSaving);
        Assert.True(editor.CanSave);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(2, attempts);
        Assert.Equal("My draft", saved![0].Title);
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task SaveBlocksAllMutationsCancellationAndDuplicateSavesUntilCompletion()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var closes = 0;
        var editor = new GraphLayoutEditorViewModel([new("First", ["cpu0", "memory"]), new("Second", ["vif_0_rx"])],
            Catalog, _ => { calls++; return completion.Task; }, () => closes++);
        var selected = editor.SelectedGraph!;
        var operation = editor.SaveCommand.ExecuteAsync(null);
        Assert.True(editor.IsSaving);
        Assert.False(editor.CanEdit);
        Assert.False(editor.CanSave);
        foreach (var command in new[] { editor.AddGraphCommand, editor.RemoveGraphCommand, editor.MoveGraphUpCommand,
                     editor.MoveGraphDownCommand, editor.AddSeriesCommand, editor.RemoveSeriesCommand,
                     editor.MoveSeriesUpCommand, editor.MoveSeriesDownCommand, editor.CancelCommand })
        {
            Assert.False(command.CanExecute(null));
            command.Execute(null);
        }
        selected.Title = "Changed while saving";
        editor.SelectedGraph = editor.Graphs[1];
        editor.SelectedSeries = Memory;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("First", selected.Title);
        Assert.Same(selected, editor.SelectedGraph);
        Assert.Same(Cpu, editor.SelectedSeries);
        Assert.Equal(2, editor.Graphs.Count);
        Assert.Equal(2, selected.Series.Count);
        Assert.Equal(1, calls);
        Assert.Equal(0, closes);
        completion.SetResult();
        await operation;
        Assert.False(editor.IsSaving);
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task BlankTitlesReceiveTheirFinalGraphPositionAsTheDefault()
    {
        var f = new Fixture([new("First", ["cpu0"]), new("Second", ["memory"])]);
        f.Editor.SelectedGraph!.Title = "  ";
        f.Editor.MoveGraphDownCommand.Execute(null);
        await f.Editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Second", "Graph 2" }, Assert.Single(f.Saves).Select(graph => graph.Title));
    }

    [Fact]
    public void MovingBeyondEitherBoundaryDoesNotChangeGraphOrSourceOrder()
    {
        var f = new Fixture([new("Only graph", ["cpu0", "memory"])]);
        Assert.False(f.Editor.MoveGraphUpCommand.CanExecute(null));
        Assert.False(f.Editor.MoveGraphDownCommand.CanExecute(null));
        f.Editor.MoveGraphUpCommand.Execute(null);
        f.Editor.MoveGraphDownCommand.Execute(null);
        Assert.False(f.Editor.MoveSeriesUpCommand.CanExecute(null));
        f.Editor.MoveSeriesUpCommand.Execute(null);
        f.Editor.SelectedSeries = Memory;
        Assert.False(f.Editor.MoveSeriesDownCommand.CanExecute(null));
        f.Editor.MoveSeriesDownCommand.Execute(null);
        Assert.Equal(new[] { "cpu0", "memory" }, f.Editor.SelectedGraph!.Series.Select(source => source.Leaf));
        Assert.Single(f.Editor.Graphs);
    }

    [Fact]
    public void DuplicateExistingLeavesMustBeRepairedBeforeSaving()
    {
        var f = new Fixture([new("Existing", ["cpu0", "cpu0"])]);
        Assert.False(f.Editor.CanSave);
        f.Editor.RemoveSeriesCommand.Execute(null);
        Assert.Single(f.Editor.SelectedGraph!.Series);
        Assert.True(f.Editor.CanSave);
    }

    [Fact]
    public void ListBindingNullFeedbackDuringMovesDoesNotLoseTheSelectedGraphOrSource()
    {
        var f = new Fixture([new("First", ["cpu0", "memory"]), new("Second", ["vif_0_rx"])]);
        var graph = f.Editor.SelectedGraph!;
        f.Editor.SelectedSeries = Memory;
        GraphLayoutDraft? visualGraph = graph;
        GraphLayoutDraft? lastBoundGraph = graph;
        f.Editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(f.Editor.SelectedGraph) && !ReferenceEquals(lastBoundGraph, f.Editor.SelectedGraph))
                visualGraph = lastBoundGraph = f.Editor.SelectedGraph;
        };
        ((INotifyCollectionChanged)f.Editor.Graphs).CollectionChanged += (_, _) =>
        {
            visualGraph = null;
            f.Editor.SelectedGraph = null;
        };
        ((INotifyCollectionChanged)graph.Series).CollectionChanged += (_, _) => f.Editor.SelectedSeries = null;
        f.Editor.MoveSeriesUpCommand.Execute(null);
        Assert.Same(Memory, f.Editor.SelectedSeries);
        f.Editor.MoveGraphDownCommand.Execute(null);
        Assert.Same(graph, f.Editor.SelectedGraph);
        Assert.Same(graph, visualGraph);
        Assert.Same(Memory, f.Editor.SelectedSeries);
        Assert.Equal(new[] { "memory", "cpu0" }, graph.Series.Select(source => source.Leaf));
        Assert.Equal(new[] { "vif_0_rx" }, f.Editor.Graphs[0].Series.Select(source => source.Leaf));
        Assert.True(f.Editor.CanSave);
    }

    [Fact]
    public void AvailableSourceBindingNullFeedbackDoesNotClearTheNextValidSelection()
    {
        var f = new Fixture([new("First", ["cpu0"])]);
        f.Editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(f.Editor.AvailableSources)) f.Editor.SelectedAvailableSource = null;
        };
        f.Editor.SelectedAvailableSource = Network;
        f.Editor.AddSeriesCommand.Execute(null);
        Assert.Same(Memory, f.Editor.SelectedAvailableSource);
        Assert.True(f.Editor.CanAddSeries);
        Assert.Equal(new[] { "cpu0", "vif_0_rx" }, f.Editor.SelectedGraph!.Series.Select(source => source.Leaf));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid,leaf")]
    [InlineData("invalid:leaf")]
    public void InvalidExistingSourceCanBeRemovedToMakeTheDraftSaveable(string leaf)
    {
        var f = new Fixture([new("Existing", [leaf, "cpu0"])]);
        Assert.False(f.Editor.CanSave);
        Assert.True(f.Editor.HasValidationMessage);
        f.Editor.RemoveSeriesCommand.Execute(null);
        Assert.True(f.Editor.CanSave);
        Assert.False(f.Editor.HasValidationMessage);
        Assert.Equal("cpu0", Assert.Single(f.Editor.SelectedGraph!.Series).Leaf);
    }

    private sealed class Fixture
    {
        public GraphLayoutEditorViewModel Editor { get; }
        public List<IReadOnlyList<GraphLayoutDefinition>> Saves { get; } = [];
        public int Closes { get; private set; }

        public Fixture(IReadOnlyList<GraphLayoutDefinition> graphs)
        {
            Editor = new(graphs, Catalog, layout => { Saves.Add(layout); return Task.CompletedTask; }, () => Closes++);
        }
    }
}
