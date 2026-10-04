# Performance capture workflow

The shell can record its own performance provider to an offline JSONL file.
This is opt-in and requires no additional .NET tools or signing certificate.
It records timings, measuring-thread allocations, inventory batch counts,
console startup outcomes and frame dimensions. It does not subscribe to RPC,
application logs, clipboard events or other EventSource providers.

No disposable pool was available for the October 2 implementation. Local
fixtures verify collection, bounded buffering, duration, flushing, statistics
and evidence classification. Linux package CI also runs the real CLI capture
through isolated desktop startup. These are synthetic checks; live measurements
remain pending.

## Capture

Create an empty directory for the evidence. Use a new absolute filename on a
local disk; existing files are rejected. Start the shell normally, adding:

```powershell
& 'C:\path\to\XcpNgCenter.Shell.exe' --performance-capture 'C:\evidence\pool-navigation.jsonl' --performance-capture-seconds 180
```

```sh
./XcpNgCenter.Shell --performance-capture /absolute/evidence/pool-navigation.jsonl --performance-capture-seconds 180
```

The default duration is 180 seconds; the range is 1–3600 seconds. Collection
ends and flushes automatically while the application remains open. Closing the
application early also flushes the capture. The buffer holds up to 4096 samples
and the file up to 128 MiB. Overload drops samples instead of waiting on the UI
or console worker. The summary records observed, written and dropped counts;
shutdown waits for admitted callbacks to finish that accounting before flushing.
The recorded duration ends when collection stops; draining buffered output does
not add time to that measurement.
Invalid capture arguments or output paths report an error and return a nonzero
exit code before desktop startup. A writer failure is reported at shutdown and
also returns a nonzero exit code, preserving any separate desktop exception.

For comparisons, record the exact commit/build, OS/session, display scale,
inventory size and workload outside the capture. Use the same deployment and
sequence for both builds, and repeat at least three times. Keep separate runs
for these phases:

1. Connect to the disposable pool, then expand/collapse the same host/VM tree
   and observe ordinary inventory events. Record the event-producing workload.
2. Select the same host and VM detail panes, including storage, networking and
   snapshots. Keep the selection sequence and dwell times consistent.
3. Open performance graphs and allow initial metadata/history loading followed
   by several incremental polls. Record selected ranges and sources.
4. Open a guest console, generate ordinary screen activity, resize it, then
   close/reopen it. Use a separate run for idle display and for a controlled
   reboot/reconnect investigation. A capture does not itself diagnose a hang.

## Summarize

From the repository, using Python 3.12 or later:

```text
python scripts/summarize-performance-capture.py C:\evidence\pool-navigation.jsonl --evidence-kind live --output C:\evidence\pool-navigation-summary.json
```

Use `--evidence-kind synthetic` for fixtures or an isolated empty desktop. The
default is `unclassified`; the reader cannot establish whether a pool was live.
The output includes median/p95/p99/max duration by operation, known allocation
samples, batch coalescing, frame rate/dimensions and startup outcomes. Missing
phases remain explicit. A missing footer, zero samples or dropped samples makes
the capture unsuitable for a performance comparison; malformed records and
mismatched sample/loss counts are rejected. Summary files are never overwritten.

Measuring-thread allocations omit other threads and native bitmap memory;
`-1` means unknown. Timings exclude GPU composition and full network/decoder
costs. Frame counts are presented-frame notifications, not input latency or
proof of guest responsiveness. Use independent guest/server evidence for reboot
diagnosis. The existing [dotnet-trace option](performance-baseline.md) remains
available when runtime/CPU investigation is needed.

## Regression gates

`PerformanceCaptureTests` exercises the actual EventListener/writer, provider
isolation, concurrent shutdown/overload accounting, argument handling, no-overwrite behavior and
automatic stop.
Blocked-output coverage verifies that the duration excludes flush time.
`PerformanceCaptureStartupTests` covers application startup/shutdown errors with
an actual failing capture output stream.
`scripts/test-performance-capture.py` checks report statistics, phase coverage
and incomplete/dropped/empty/invalid evidence. These suites run
through [platform acceptance](platform-acceptance.md). Linux package startup
creates `desktop-performance.jsonl` and its synthetic summary in the evidence
directory, while retaining the visible-window and startup checks.
