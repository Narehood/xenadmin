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
deterministic sequence of copies. A repeated 1080p scroll test checks that copying
itself allocates zero managed bytes; the table includes dispatcher/flush overhead.

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

## Limits and next investigation

These are synthetic CPU measurements. Tree timing excludes tree-control layout,
selection restoration, event arrival rates, and detail-pane refresh. Hosts are
cache records without live sessions. Console timing includes offscreen Skia
bitmap upload from managed memory, but excludes network decoding, compression,
GPU presentation, input latency, and native desktop composition. Allocation
counts cover the measuring thread's managed allocations, not native bitmap
memory or other threads. Neither the timing nor the resize workload proves a
long-running process has no resource leaks.

At fixed VM count the tree cost grows with host count; source inspection shows
the builder checks all VMs once per host. A future change can group resolved home
hosts once per build, but first capture real inventory event frequency and a UI
trace. A 25 ms synthetic rebuild alone does not establish that the application
has repeated user-visible pauses. Preserve standalone/pool layout, stopped VM
placement, privacy formatting, expansion, and selection semantics in any change.

Use [platform acceptance](platform-acceptance.md) to record real Windows/Linux
console scrolling, desktop resize, 30-minute activity, and large-pool navigation.
Those checks are deferred until a suitable test environment is available.
