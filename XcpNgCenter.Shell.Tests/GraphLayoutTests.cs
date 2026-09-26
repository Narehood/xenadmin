using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Actions;
using XcpNgCenter.Shell.Services.Performance;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.Tests;

public sealed class GraphLayoutTests
{
    [Fact]
    public void SavedGraphAndSourceOrderRoundTripsWithoutDependingOnSamples()
    {
        var f = new Fixture();
        f.Pool.gui_config[ShellGraphLayoutKeys.GetLayoutKey(5, f.Host)] = "missing_disk,cpu1";
        f.Pool.gui_config[ShellGraphLayoutKeys.GetGraphNameKey(5, f.Host)] = "Second";
        f.Pool.gui_config[ShellGraphLayoutKeys.GetLayoutKey(0, f.Host)] = "cpu2,cpu0";
        f.Pool.gui_config[ShellGraphLayoutKeys.GetGraphNameKey(0, f.Host)] = "First";

        var layout = ShellGraphLayout.Read(f.Host, null);
        Assert.Equal(new[] { "First", "Second" }, layout.Select(graph => graph.Title));
        Assert.Equal(new[] { "missing_disk", "cpu1" }, layout[1].DataSourceLeaves);
        var written = ShellGraphLayout.Write(f.Pool.gui_config, "host", f.Host.uuid, layout);
        Assert.Equal("cpu2,cpu0", written[ShellGraphLayoutKeys.GetLayoutKey(0, f.Host)]);
        Assert.Equal("missing_disk,cpu1", written[ShellGraphLayoutKeys.GetLayoutKey(1, f.Host)]);
        Assert.False(written.ContainsKey(ShellGraphLayoutKeys.GetLayoutKey(5, f.Host)));
        f.Pool.gui_config = written;
        var reloaded = ShellGraphLayout.Read(f.Host, null);
        Assert.Equal(layout.Select(graph => graph.Title), reloaded.Select(graph => graph.Title));
        Assert.Equal(layout.SelectMany(graph => graph.DataSourceLeaves), reloaded.SelectMany(graph => graph.DataSourceLeaves));
    }

    [Theory]
    [InlineData("XenCenter.GraphLayout.0.host.host-uuid", true)]
    [InlineData("XenCenter.GraphName.2147483647.host.host-uuid", true)]
    [InlineData("XenCenter.GraphLayout.0.host.host-uuid-other", false)]
    [InlineData("XenCenter.GraphLayout.0.vm.host-uuid", false)]
    [InlineData("Other.GraphLayout.0.host.host-uuid", false)]
    [InlineData("XenCenter.GraphColor.0.host.host-uuid", false)]
    [InlineData("prefix.XenCenter.GraphLayout.0.host.host-uuid", false)]
    [InlineData("XenCenter.GraphLayout.0.host.host-uuid.suffix", false)]
    [InlineData("XenCenter.GraphLayout.-1.host.host-uuid", false)]
    [InlineData("XenCenter.GraphLayout.00.host.host-uuid", false)]
    [InlineData("XenCenter.GraphLayout.2147483648.host.host-uuid", false)]
    public void OnlyExactCanonicalTargetKeysAreOwned(string key, bool owned)
    {
        Assert.Equal(owned, ShellGraphLayoutKeys.IsTargetKey(key, "host", "host-uuid"));
        var original = new Dictionary<string, string> { [key] = "old value" };
        var updated = ShellGraphLayout.Write(original, "host", "host-uuid", [new("CPU", ["cpu0"])]);
        if (!owned) Assert.Equal("old value", updated[key]);
        Assert.Equal("old value", original[key]);
    }

    [Fact]
    public void MissingSavedSourcesAndGraphsStayVisibleWithoutDefaultFallback()
    {
        var f = new Fixture();
        f.Save("Previously available", ["removed_source"]);
        f.Pool.gui_config[ShellGraphLayoutKeys.GetLayoutKey(1, f.Host)] = "";
        using var maintainer = new ShellRrdMaintainer(f.Host, action => action());
        Merge(maintainer, "host:host-uuid:memory_free_kib", "memory_free_kib", "1024", "KiB");

        var graphs = PerformanceGraphBuilder.Build(f.Host, maintainer, RrdArchiveInterval.FiveSecond);
        Assert.Equal(2, graphs.Count);
        Assert.Equal("Previously available", graphs[0].Title);
        Assert.Empty(graphs[0].Series);
        Assert.False(graphs[0].HasData);
        Assert.Contains("Unavailable data sources: removed_source", graphs[0].AvailabilityNotice);
        Assert.Contains("no data sources", graphs[1].AvailabilityNotice);
        Assert.Equal("removed_source", Assert.Single(PerformanceGraphBuilder.DescribeLayout(graphs)[0].Leaves));
        Assert.DoesNotContain(graphs, graph => graph.Title == "Memory");
    }

    [Fact]
    public void AvailableAndMissingSavedSelectionsKeepTheirOrder()
    {
        var f = new Fixture();
        f.Save("Custom", ["missing", "cpu1", "cpu0"]);
        using var maintainer = new ShellRrdMaintainer(f.Host, action => action());
        Merge(maintainer, "host:host-uuid:cpu0", "cpu0", "0.2", "(fraction)");
        Merge(maintainer, "host:host-uuid:cpu1", "cpu1", "0.3", "(fraction)");
        var graph = Assert.Single(PerformanceGraphBuilder.Build(f.Host, maintainer, RrdArchiveInterval.FiveSecond));
        Assert.Equal(new[] { "host:host-uuid:cpu1", "host:host-uuid:cpu0" }, graph.Series.Select(series => series.Id));
        Assert.Equal(new[] { "missing", "cpu1", "cpu0" }, Assert.Single(PerformanceGraphBuilder.DescribeLayout([graph])).Leaves);
        Assert.Contains("missing", graph.AvailabilityNotice);
    }

    [Fact]
    public void MalformedSavedLeafCannotBeReinterpretedAsAnotherObjectsMemory()
    {
        var f = new Fixture();
        f.Save("Malformed", ["other:memory"]);
        using var maintainer = new ShellRrdMaintainer(f.Host, action => action());
        Merge(maintainer, "host-uuid:other:memory_internal_free", "memory_internal_free", "1024", "KiB");
        var graph = Assert.Single(PerformanceGraphBuilder.Build(f.Host, maintainer, RrdArchiveInterval.FiveSecond));
        Assert.Empty(graph.Series);
        Assert.Equal("other:memory", Assert.Single(graph.Layout!.DataSourceLeaves));
        Assert.Contains("Unavailable", graph.AvailabilityNotice);
    }

    [Fact]
    public void CatalogUsesEveryRangeButExcludesOtherTargetsAndHiddenSources()
    {
        var f = new Fixture();
        using var maintainer = new ShellRrdMaintainer(f.Host, action => action());
        Merge(maintainer, "host:host-uuid:cpu0", "cpu0", "0.2", "(fraction)");
        Merge(maintainer, "host:host-uuid:past", "past", "4", "bytes", RrdArchiveInterval.OneDay);
        Merge(maintainer, "host:other:foreign", "foreign", "1", "bytes");
        Merge(maintainer, "vm:host-uuid:foreign_vm", "foreign_vm", "1", "bytes");
        var hidden = new RrdSeries("host:host-uuid:hidden", "hidden", "Hidden", "bytes") { Hide = true };
        maintainer.Archives[RrdArchiveInterval.FiveSecond].SeriesById[hidden.Id] = hidden;

        var catalog = ShellGraphLayout.Catalog(f.Host, maintainer, [new("Custom", ["missing", "cpu0"])]);
        Assert.Equal(new[] { "cpu0", "missing", "past" }, catalog.Select(option => option.Leaf).Order(StringComparer.Ordinal));
        Assert.False(catalog.Single(option => option.Leaf == "missing").IsAvailable);
        Assert.True(catalog.Single(option => option.Leaf == "past").IsAvailable);
    }

    [Fact]
    public void MemoryCatalogGroupsNewSourcesAndRetainsSavedAliases()
    {
        var f = new Fixture();
        f.Save("RAM", ["memory_total_kib", "memory_free_kib"]);
        using var maintainer = new ShellRrdMaintainer(f.Host, action => action());
        Merge(maintainer, "host:host-uuid:memory_total_kib", "memory_total_kib", "4096", "KiB");
        Merge(maintainer, "host:host-uuid:memory_free_kib", "memory_free_kib", "1024", "KiB");
        var layout = ShellGraphLayout.Read(f.Host, maintainer);
        Assert.Equal(new[] { "memory_total_kib", "memory_free_kib" }, layout[0].DataSourceLeaves);
        var catalog = ShellGraphLayout.Catalog(f.Host, maintainer, layout);
        Assert.Equal(2, catalog.Count);
        Assert.All(catalog, option => Assert.Equal("Memory (used/free/total)", option.Label));
        Assert.Single(ShellGraphLayout.Catalog(f.Host, maintainer, []));
        var graph = Assert.Single(PerformanceGraphBuilder.Build(f.Host, maintainer, RrdArchiveInterval.FiveSecond));
        Assert.Equal(new[] { "Used", "Free", "Total" }, graph.Series.Select(series => series.Name));
        Assert.Empty(graph.AvailabilityNotice);
    }

    [Fact]
    public void RenamingMemoryGraphToCpuDoesNotChangeItsUnitsOrClipToOneHundred()
    {
        var f = new Fixture();
        f.Save("CPU comparison", ["memory_free_kib"]);
        using var maintainer = new ShellRrdMaintainer(f.Host, action => action());
        Merge(maintainer, "host:host-uuid:memory_total_kib", "memory_total_kib", "4096", "KiB");
        Merge(maintainer, "host:host-uuid:memory_free_kib", "memory_free_kib", "1024", "KiB");
        var graph = Assert.Single(PerformanceGraphBuilder.Build(f.Host, maintainer, RrdArchiveInterval.FiveSecond));
        Assert.Equal(4194304, graph.YAxisMax);
        Assert.All(graph.Series, series => Assert.Equal("bytes", series.Units));
    }

    [Fact]
    public void UnknownUnitsPreserveRawSamplesWithoutInferringPercentFromCpuName()
    {
        var f = new Fixture();
        f.Save("CPU activity", ["cpu0"]);
        using var maintainer = new ShellRrdMaintainer(f.Host, action => action());
        Merge(maintainer, "host:host-uuid:cpu0", "cpu0", "0.5", "");
        var graph = Assert.Single(PerformanceGraphBuilder.Build(f.Host, maintainer, RrdArchiveInterval.FiveSecond));
        Assert.Null(graph.YAxisMax);
        Assert.Equal(0.5, Assert.Single(Assert.Single(graph.Series).Points).Value);
        Assert.Empty(Assert.Single(graph.Series).Units);
    }

    [Fact]
    public void DefinitionAndSnapshotOwnImmutableCopies()
    {
        var f = new Fixture();
        f.Save("Original", ["cpu0"]);
        var leaves = new List<string> { "cpu0" };
        var definition = new GraphLayoutDefinition("Draft", leaves);
        var snapshot = ShellGraphLayout.Snapshot(f.Host);
        leaves[0] = "cpu9";
        f.Pool.gui_config[ShellGraphLayoutKeys.GetGraphNameKey(0, f.Host)] = "Changed";
        Assert.Equal("cpu0", Assert.Single(definition.DataSourceLeaves));
        Assert.Equal("Original", snapshot.Entries[ShellGraphLayoutKeys.GetGraphNameKey(0, f.Host)]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)definition.DataSourceLeaves)[0] = "cpu9");
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)snapshot.Entries).Clear());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" cpu0")]
    [InlineData("cpu0,cpu1")]
    [InlineData("host:other:cpu0")]
    public void InvalidSourcesCannotCreateAnAmbiguousCsvLayout(string leaf)
        => Assert.Throws<InvalidOperationException>(() => ShellGraphLayout.Validate([new("CPU", [leaf])]));

    [Fact]
    public void EmptyGraphsAndDuplicateSourcesRequireCorrectionWhileMissingSourcesRemainValid()
    {
        Assert.Throws<InvalidOperationException>(() => ShellGraphLayout.Validate([]));
        Assert.Throws<InvalidOperationException>(() => ShellGraphLayout.Validate([new("Empty", [])]));
        Assert.Throws<InvalidOperationException>(() => ShellGraphLayout.Validate([new("Duplicate", ["cpu0", "cpu0"])]));
        ShellGraphLayout.Validate([new("", ["removed_but_preserved"])]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveWorkerPreservesFreshUnrelatedConfigurationAndOwnsSubmittedDraft(bool vmTarget)
    {
        var f = new Fixture();
        IXenObject target = vmTarget ? f.Vm : f.Host;
        f.Save("Old", ["cpu0"], target);
        var cacheBefore = new Dictionary<string, string>(f.Pool.gui_config);
        var serverConfig = new Dictionary<string, string>(cacheBefore)
        {
            ["concurrent-setting"] = "new server value",
            [$"XenCenter.GraphLayout.0.host.{f.Host.uuid}-similar"] = "untouched"
        };
        using var server = new RpcServer(4, request => ReplyFor(request, target, f.Pool, serverConfig));
        f.MarkConnected(server.Session);
        var draft = new List<GraphLayoutDefinition> { new("Selected", ["cpu1", "missing", "cpu0"]) };
        var action = new SaveShellGraphLayoutAction(target, draft, ShellGraphLayout.Snapshot(target));
        draft[0] = new("Later edit", ["cpu9"]);
        Assert.Contains("pool.set_gui_config", action.GetApiMethodsToRoleCheck.Select(method => method.Method));
        await Task.Run(() => action.RunSync(server.Session));
        var requests = await server.Requests;

        Assert.Equal(new[] { vmTarget ? "VM.get_uuid" : "host.get_uuid", "pool.get_uuid", "pool.get_gui_config", "pool.set_gui_config" },
            requests.Select(request => request.GetProperty("method").GetString()));
        var written = requests[^1].GetProperty("params")[2].Deserialize<Dictionary<string, string>>()!;
        Assert.Equal("Selected", written[ShellGraphLayoutKeys.GetGraphNameKey(0, target)]);
        Assert.Equal("cpu1,missing,cpu0", written[ShellGraphLayoutKeys.GetLayoutKey(0, target)]);
        Assert.Equal("new server value", written["concurrent-setting"]);
        Assert.Equal("untouched", written[$"XenCenter.GraphLayout.0.host.{f.Host.uuid}-similar"]);
        Assert.Equal(cacheBefore.OrderBy(pair => pair.Key), f.Pool.gui_config.OrderBy(pair => pair.Key));
        Assert.True(action.Succeeded);
        Assert.False(server.HasPendingRequest);
    }

    [Fact]
    public async Task ServerLayoutChangeRejectsStaleDraftWithoutWriting()
    {
        var f = new Fixture();
        f.Save("Original", ["cpu0"]);
        var fresh = new Dictionary<string, string>(f.Pool.gui_config)
        {
            [ShellGraphLayoutKeys.GetGraphNameKey(0, f.Host)] = "Another editor"
        };
        using var server = new RpcServer(3, request => ReplyFor(request, f.Host, f.Pool, fresh));
        f.MarkConnected(server.Session);
        var action = new SaveShellGraphLayoutAction(f.Host, [new GraphLayoutDefinition("Mine", ["cpu1"])], ShellGraphLayout.Snapshot(f.Host));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => action.RunSync(server.Session)));
        Assert.Contains("changed after", error.Message);
        Assert.DoesNotContain(await server.Requests, request => request.GetProperty("method").GetString() == "pool.set_gui_config");
        Assert.False(server.HasPendingRequest);
    }

    [Theory]
    [InlineData("target", 1)]
    [InlineData("pool", 2)]
    public async Task ServerIdentityChangeRejectsDraftBeforeReadingOrWritingConfiguration(string changed, int calls)
    {
        var f = new Fixture();
        using var server = new RpcServer(calls, request =>
        {
            var method = request.GetProperty("method").GetString();
            return changed == "target" && method == "host.get_uuid" || changed == "pool" && method == "pool.get_uuid"
                ? new RpcReply("replacement") : ReplyFor(request, f.Host, f.Pool, f.Pool.gui_config);
        });
        f.MarkConnected(server.Session);
        var action = new SaveShellGraphLayoutAction(f.Host, [new GraphLayoutDefinition("CPU", ["cpu0"])]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => action.RunSync(server.Session)));
        Assert.Equal(calls, (await server.Requests).Count);
        Assert.False(server.HasPendingRequest);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("pool")]
    [InlineData("layout")]
    [InlineData("disconnected")]
    public void CacheChangesAreRejectedBeforeAnyRpc(string changed)
    {
        var f = new Fixture();
        using var server = new RpcServer(0, _ => throw new InvalidOperationException("Unexpected RPC"));
        if (changed != "disconnected") f.MarkConnected(server.Session);
        var action = new SaveShellGraphLayoutAction(f.Host, [new GraphLayoutDefinition("CPU", ["cpu0"])]);
        switch (changed)
        {
            case "target": f.Host.uuid = "replacement-host"; break;
            case "pool": f.Pool.uuid = "replacement-pool"; break;
            case "layout": f.Save("Another editor", ["cpu1"]); break;
        }
        Assert.Throws<InvalidOperationException>(() => action.RunSync(server.Session));
        Assert.False(server.HasPendingRequest);
    }

    [Theory]
    [InlineData("OPERATION_NOT_ALLOWED")]
    [InlineData("SESSION_INVALID")]
    public async Task FailedSaveDoesNotRetryOrMutateTheCache(string failure)
    {
        var f = new Fixture();
        f.Save("Original", ["cpu0"]);
        var before = new Dictionary<string, string>(f.Pool.gui_config);
        using var server = new RpcServer(4, request => request.GetProperty("method").GetString() == "pool.set_gui_config"
            ? new RpcReply(null, failure) : ReplyFor(request, f.Host, f.Pool, before));
        f.MarkConnected(server.Session);
        var action = new SaveShellGraphLayoutAction(f.Host, [new GraphLayoutDefinition("Unsaved", ["missing"])]);
        var error = await Assert.ThrowsAsync<Failure>(() => Task.Run(() => action.RunSync(server.Session)));
        Assert.Equal(failure, error.ErrorDescription[0]);
        Assert.Equal(4, (await server.Requests).Count);
        Assert.Equal(before.OrderBy(pair => pair.Key), f.Pool.gui_config.OrderBy(pair => pair.Key));
        Assert.True(action.IsError);
        Assert.False(server.HasPendingRequest);
    }

    private static RpcReply ReplyFor(JsonElement request, IXenObject target, Pool pool, Dictionary<string, string> config)
        => request.GetProperty("method").GetString() switch
        {
            "host.get_uuid" or "VM.get_uuid" => new(ShellGraphLayoutKeys.ObjectUuid(target)),
            "pool.get_uuid" => new(pool.uuid),
            "pool.get_gui_config" => new(config),
            "pool.set_gui_config" => new(null),
            var method => throw new InvalidOperationException($"Unexpected method {method}")
        };

    private static void Merge(ShellRrdMaintainer maintainer, string id, string leaf, string raw, string units,
        RrdArchiveInterval interval = RrdArchiveInterval.FiveSecond)
    {
        var series = new RrdSeries(id, leaf, leaf, units);
        series.AddRawValue(raw, new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc).Ticks);
        maintainer.Archives[interval].Merge([series]);
    }

    private sealed record RpcReply(object? Result, string? Error = null);

    private sealed class RpcServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(15));

        public RpcServer(int count, Func<JsonElement, RpcReply> reply)
        {
            _listener.Start();
            Session = new Session($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/")
                { opaque_ref = "OpaqueRef:graph-test-session", Timeout = 3000 };
            Requests = ServeAsync(count, reply);
        }

        public Session Session { get; }
        public Task<IReadOnlyList<JsonElement>> Requests { get; }
        public bool HasPendingRequest => _listener.Pending();

        private async Task<IReadOnlyList<JsonElement>> ServeAsync(int count, Func<JsonElement, RpcReply> reply)
        {
            var requests = new List<JsonElement>();
            for (var index = 0; index < count; index++)
            {
                using var client = await _listener.AcceptTcpClientAsync(_timeout.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var length = 0;
                while (await reader.ReadLineAsync(_timeout.Token) is { Length: > 0 } header)
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header.Split(':', 2)[1]);
                var body = new char[length];
                var offset = 0;
                while (offset < body.Length)
                {
                    var read = await reader.ReadAsync(body.AsMemory(offset), _timeout.Token);
                    if (read == 0) throw new EndOfStreamException();
                    offset += read;
                }
                using var document = JsonDocument.Parse(new string(body));
                var request = document.RootElement.Clone();
                requests.Add(request);
                var response = reply(request);
                var id = request.GetProperty("id");
                var payload = response.Error == null
                    ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = response.Result })
                    : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = 1, message = response.Error, data = new[] { "loopback regression" } } });
                var bytes = Encoding.UTF8.GetBytes(payload);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _timeout.Token);
                await stream.WriteAsync(bytes, _timeout.Token);
            }
            return requests;
        }

        public void Dispose()
        {
            _timeout.Cancel();
            _listener.Stop();
            _timeout.Dispose();
        }
    }

    private sealed class Fixture
    {
        public XenConnection Connection { get; } = new();
        public Host Host { get; }
        public VM Vm { get; }
        public Pool Pool { get; }

        public Fixture()
        {
            Host = Add("host-ref", new Host { uuid = "host-uuid", name_label = "Host" });
            Vm = Add("vm-ref", new VM { uuid = "vm-uuid", name_label = "VM", power_state = vm_power_state.Running, VCPUs_at_startup = 2 });
            Pool = Add("pool-ref", new Pool { uuid = "pool-uuid", master = new(Host.opaque_ref) });
        }

        public void Save(string title, IReadOnlyList<string> leaves, IXenObject? target = null)
        {
            target ??= Host;
            Pool.gui_config[ShellGraphLayoutKeys.GetLayoutKey(0, target)] = string.Join(',', leaves);
            Pool.gui_config[ShellGraphLayoutKeys.GetGraphNameKey(0, target)] = title;
        }

        public void MarkConnected(Session session)
        {
            var field = typeof(XenConnection).GetField("connectTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var state = Activator.CreateInstance(field.FieldType, BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: ["127.0.0.1", 1], culture: null)!;
            field.FieldType.GetField("Connected")!.SetValue(state, true);
            field.FieldType.GetField("Session")!.SetValue(state, session);
            field.SetValue(Connection, state);
        }

        private T Add<T>(string reference, T value) where T : XenObject<T>
        {
            Connection.Cache.UpdateFrom(Connection, [new ObjectChange(typeof(T), reference, value)]);
            return Connection.Resolve(new XenRef<T>(reference));
        }
    }
}
