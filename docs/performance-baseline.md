# Synthetic performance baseline

The repeatable harness in [`tools/Modernization.Probes`](../tools/Modernization.Probes/Program.cs)
measures the real inventory tree builder and console framebuffer with generated
data. It never loads saved profiles, connects to a server, or changes a pool.

The September 25, 2026 Windows run found a concrete console bottleneck:
`CopyRectangle` allocated a rectangle-sized byte array and copied each pixel
twice. It now copies rows in the safe vertical direction using overlapping span
copies, with no rectangle allocation. Clipped source pixels still become zeroes;
clipped destination pixels remain ignored. A framebuffer snapshot is no longer
needed for overlapping scrolls. This is separate from the earlier fixes to frame
presentation and resize ownership, which remain intact.

## Reproduce

Build once, then run the compiled executable so restore/build time stays outside
the measurement. Run each command separately and check its exit code.

```powershell
dotnet build tools/Modernization.Probes/Modernization.Probes.csproj -c Release -p:RestoreLockedMode=true
dotnet tools/Modernization.Probes/bin/Release/net10.0/Modernization.Probes.dll --samples 50 --warmup 20 --output tools/Modernization.Probes/bin/local-results.json --label local
```

The same commands work on Linux. Native Skia dependencies must be installed as
for the shell tests; a desktop/display server is not required. The process uses
offscreen Avalonia with the real status icons and writeable bitmaps.

The checked-in comparison sets `DOTNET_TieredCompilation=0` **for the probe
process only** on both runs, preventing tier transitions from distorting short
sequential cases. For an exact comparison, set that environment variable before
starting the executable, and restore its previous value afterwards. Application
runtime settings are unchanged. Also run with normal runtime settings when
investigating actual desktop behavior. Use the same machine, runtime, sample
count, warmup count, and compilation setting for both sides; avoid concurrent
builds and compare several runs before imposing a timing budget.

## Evidence

[Before JSON](performance/2026-09-25-net10-before.json) and
[after JSON](performance/2026-09-25-net10-after.json) retain all samples, per-sample
allocations, collection counts, runtime/OS metadata, assembly versions, and shell
assembly SHA-256 hashes. Both ran on Windows build 26200, x64, 16 logical
processors, .NET 10.0.12, workstation GC, with 20 warmups and 50 measured samples.
The UTC capture date is September 26; the local work date was September 25.

| Operation | Before median / p95 | After median / p95 | Managed bytes per operation, before → after |
| --- | ---: | ---: | ---: |
| 1080p overlapping scroll plus bitmap flush | 24.503 / 25.730 ms | 0.344 / 0.751 ms | 8,286,952 → 208 |
| 4K overlapping scroll plus bitmap flush | 98.477 / 101.391 ms | 5.239 / 5.915 ms | 33,162,472 → 208 |
| 4K raw frame plus bitmap flush | 4.462 / 4.998 ms | 4.475 / 5.885 ms | 208 → 208 |
| 20 queued 4K small updates and one UI drain | 2.431 / 2.698 ms | 2.441 / 2.830 ms | 208 → 208 |

Each burst still produces exactly one presentation, and the harness checks the
last updated pixel. Framebuffer regression tests compare the actual presented
pixels against an independent snapshot oracle for every overlap direction,
clipped/offscreen inputs, zero/negative sizes, very large coordinates, and a
deterministic sequence of copies. A repeated 1080p scroll test permits at most
64 KiB across ten copies, rejecting rectangle-sized temporary buffers while
allowing small runtime/profiler overhead. Its original exact-zero assertion
measured 7,976 bytes in a later hosted Linux run; the source of those bytes was
not instrumented. The historical table above includes dispatcher/flush overhead
and is unchanged by this test correction.

The inventory implementation was left unchanged. The post-change baseline is:

| Synthetic inventory | Median / p95 tree build | Managed bytes per build |
| --- | ---: | ---: |
| 4 hosts, 100 VMs, 5 SRs | 0.098 / 0.128 ms | 56,024 |
| 16 hosts, 1,000 VMs, 17 SRs | 1.611 / 1.788 ms | 556,360 |
| 64 hosts, 1,000 VMs, 65 SRs | 5.613 / 6.234 ms | 950,360 |
| 64 hosts, 5,000 VMs, 65 SRs | 25.562 / 28.340 ms | 4,236,800 |

VMs have deterministic reverse-sorted names, 80% are running on distributed
hosts, and 20% are halted without a home. There is one local SR per host and one
shared SR. Template/snapshot/control-domain records verify filtering. Counts and
unique object references are checked after every scenario. These workload sizes
are stress inputs, not a claim about supported server pool limits.

## October 1 inventory refresh and layout

The tree builder now resolves each VM/SR home once, rather than rescanning the
whole inventory for every host. Inventory event bursts queue one refresh per
connection per UI turn; updates during refresh schedule a later turn. Stale
connection callbacks and disposed view models cannot rebuild a current tree.
A failing connection is logged and retained until the next inventory notification
schedules a turn; the remaining connections still refresh. A newer notification
for that connection takes precedence, and persistent failures do not create an
automatic UI retry loop.

The desktop probe found that replacing tree nodes dominated layout time, even
when most hosts were collapsed. Refresh now reconciles the existing nodes:
metadata updates preserve containers, ordering uses collection moves, and only
added/removed objects change membership. Objects moving between hosts are
detached first and retain their identity. Selection and collapsed state survive;
the selected object's new ancestor path expands. Existing detail refresh still
runs when the selected node instance is unchanged.

[Layout JSON](performance/2026-10-01-inventory-layout.json) compares replacement
and reuse using the **same current tree builder**, production MainWindow
TreeView/template/styles and production expansion helpers. Each sample queues
200 notifications, changes the selected guest's name and drains one refresh,
including tree construction, state reconciliation and synchronous layout.

| Synthetic inventory | Replace median | Reuse median |
| --- | ---: | ---: |
| 4 hosts, 100 VMs | 51.929 ms | 1.092 ms |
| 16 hosts, 1,000 VMs | 132.225 ms | 6.238 ms |
| 64 hosts, 5,000 VMs | 143.932 ms | 22.098 ms |

The fixtures have running VMs distributed across hosts, one host expanded and
the others collapsed. They use two warmups and five measured samples per case.
An untimed migration also verifies that the guest instance survives and its
destination expands. CPU/default-tiering and desktop activity vary: the earlier
5,000-VM paired run measured 197.4/24.6 ms. These measurements justify preserving
containers; they are not a timing budget or a supported pool-size claim.

Reproduce on a Windows desktop session with no live profiles or pools:

```powershell
dotnet run --project tools/AdvancedNetworking.UiProbe -c Release -p:RestoreLockedMode=true -- --modernization --evidence-directory artifacts/inventory-layout-local
```

Twenty-three focused tests cover topology/order changes, missing hosts, storage
moves, thread-safe event bursts, updates during refresh, stale connections,
disposal, post failure, per-connection rebuild failures/retry without starvation,
newer notifications after failure, node retention, collection moves, migration
and root replacement.
The UI probe additionally checks selection, fresh metadata, collapsed hosts and
migration against the real TreeView. It bypasses MainViewModel initialization
and detail panes; it does not load saved profiles or credentials. The timing
excludes network work, detail refresh, frame presentation and native input
latency. The October 2 probe also measures fully expanded synthetic trees;
actual event streams and native desktop behavior still need acceptance.

## Limits and next investigation

### October 2 production instrumentation

The [bounded JSONL capture workflow](performance-capture.md) records this
provider directly from the desktop without extra tracing tools. It also covers
actual RRD fetch phases and reports missing phases, dropped samples and evidence
kind. No live pool is available for this follow-up; synthetic capture checks
do not add live performance results to the measurements below.

The opt-in EventSource `XcpNgCenter-Shell-Performance` records inventory burst
counts, per-connection rebuild/reconciliation, detail refresh phases, console
tunnel startup, bitmap flush timings and frame dimensions. Synchronous operation
events include elapsed milliseconds and allocations on the measuring thread;
cross-thread scopes report unknown allocation (`-1`). Disabled timing scopes do
not allocate. Payloads omit hostnames, VM names, UUIDs, object references,
credentials and clipboard contents.

Capture on the real target desktop with the standard .NET tracing tools, for example:

```text
dotnet-trace collect --process-id <pid> --providers XcpNgCenter-Shell-Performance
```

The [official tracing guide](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace)
explains collection and trace formats. Attach only to the intended test process;
other enabled providers can expose information outside this provider's payloads.
Measure a representative event stream, detail selection and sustained console
activity before choosing further performance changes. These phases do not include
GPU/native composition or all network/decoder costs.

The production tree probe now pairs replacement/reuse for both partly collapsed
and fully expanded host trees at each inventory size. It still uses synthetic
inventory and bypasses MainViewModel's detail panes. Results therefore support
layout comparisons, while the new production events cover the previously missing
live event/detail phases when a disposable environment becomes available.

The September 25 results above are synthetic CPU measurements. Their tree timing excludes tree-control layout,
selection restoration, event arrival rates, and detail-pane refresh. Hosts are
cache records without live sessions. Console timing includes offscreen Skia
bitmap upload from managed memory, but excludes network decoding, compression,
GPU presentation, input latency, and native desktop composition. Allocation
counts cover the measuring thread's managed allocations, not native bitmap
memory or other threads. Neither the timing nor the resize workload proves a
long-running process has no resource leaks.

The October 1 builder already resolves VM/SR placement once per object; the
September 25 repeated host scan is historical. Capture real inventory event
frequency and a UI trace before another optimization. A 25 ms synthetic rebuild
alone does not establish repeated user-visible pauses. Preserve standalone/pool layout, stopped VM
placement, privacy formatting, expansion, and selection semantics in any change.

Use [platform acceptance](platform-acceptance.md) to record real Windows/Linux
console scrolling, desktop resize, 30-minute activity, and large-pool navigation.
Those checks are deferred until a suitable test environment is available.
