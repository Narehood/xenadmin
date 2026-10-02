# Async lifecycle, WinForms console and performance capture follow-up

The user selected remaining modernization items 1, 2 and 4 on October 2:
incremental async/cancellation work, WinForms console startup hardening and live
performance investigation. Installer work and release signing are deferred.
The user confirmed that no disposable XCP-ng pool is available; this pass
prepares and verifies collection rather than claiming live measurements.

Implementation commits:

- `71cca31ed`: heartbeat uses cancellable async RPC and delay, with explicit
  completion and local duplicate-transport cleanup. The typed time adapter
  preserves the XAPI converter. Graph metadata polling uses async RPC; RRD
  HTTP/XML phases close on cancellation and idle waits use `Task.Delay`.
  Elevation login accepts a token and failed session creation cleans up owned
  resources. The legacy proxy probe now selects its exact retained signature.
- `e8e944992`: both clients share the startup-only deadline guard. WinForms
  covers login, tunnel and handshake startup, cancels retry waits and guest
  discovery on closure, rejects stale handoff, closes failed RFB streams, and
  distinguishes copied pool tokens from independently elevated sessions.
  Thread assertions tolerate an absent main window and repeated graphics
  disposal is safe.
- `f9ad9b530`: opt-in bounded JSONL capture, duration/overload accounting,
  offline summary statistics and coverage, and actual capture CLI execution
  during isolated Linux desktop package checks. No new package dependencies.
- `528082be5`: capture admission and queue completion share a short gate;
  shutdown cannot overtake callbacks updating loss counts. EventSource disable
  runs outside that gate to avoid lock inversion. The footer includes observed
  counts and the reader rejects inconsistent written/dropped totals.

## Regression evidence

Focused shared checks pass on both `net10.0` and `net481`: compact XAPI date/wire
shape, in-flight heartbeat cancellation without pool logout/disconnect,
stop-before-start/idle-stop, silent TLS during elevation, shared guard deadline
and connected idle safety, and silent HTTP GET cancellation.

The graph cases cover blocked XML/body disposal, metadata RPC cancellation,
duplicate ownership, prompt idle-stop and queued UI suppression. Capture cases
exercise the real provider/writer and reject other-provider secrets; overload
records lost samples, duration stops collection without closing the desktop,
and invalid arguments/overwrite attempts fail. Seven Python report fixtures
cover statistics, phase coverage and incomplete/dropped/empty/invalid evidence,
including inconsistent loss totals. Four concurrent producers race shutdown
with both tiny and normal queue capacities, checking the actual file/footer.

Both full solution configurations and full test suites pass: 1,020 shell cases
per configuration and 96 shared cases per framework. The WinForms probe passes
349 lifecycle/designer checks per configuration, including the actual RFB
client's timeout, cancellation, retry-after-failure and successful idle paths;
it uses synthetic streams and in-memory settings. All 32,138 resources in 289
sets, 23 archive checks per configuration and 12 proxy cases pass.

The first integrated pass stopped when the reflection-based proxy probe matched
the newly overloaded `ConnectStream` ambiguously. Specifying the original
four-argument signature fixes the probe; all authentication cases then pass.
The final integrated pass at `f9ad9b530` passes all 26 checks, including all six
UI probe modes and a fresh verified Windows archive. Evidence is in
`artifacts/async-winforms-capture-final-20261002`; its manifest records source,
commands, results, package hash and unchanged portable lockfiles. The final
capture deadline assertion checks parsed frame dimensions rather than matching
digits that could also occur in a timestamp. Local RDP interop is reused; hosted
Windows builds generate it with the Windows SDK. Check Windows/Linux and CodeQL
on the final PR head; the new Linux capture path requires native hosted execution.

After the shutdown-accounting fix, all eight capture cases pass. Full shell
suites pass again with 1,021 cases each in Release and Debug; both shared
frameworks pass all 96 cases. The seven report fixtures pass. Follow-up logs
are `artifacts/capture-shutdown-shell-Release.log`,
`artifacts/capture-shutdown-shell-Debug.log`, `artifacts/capture-shutdown-shared.log`
and `artifacts/performance-capture-shutdown-tests.log`. The earlier full
acceptance manifest remains evidence for the broader WinForms/package checks;
current-head hosted acceptance verifies the collector in fresh builds/packages.

## Remaining limitations

Generated SDK calls, administrative action execution and the main
connection/event worker retain their synchronous compatibility path. RRD
HTTP/XML parsing remains synchronous during active worker phases. This is an
incremental lifecycle change, not a replacement of every legacy worker.
Cancelling a client request does not establish cancellation of a server task.

No live pool, physical desktop, actual MSI/update/UAC/rollback, RDP guest login
or guest reboot acceptance was performed. The Debian reboot incident remains
open. The capture workflow's tests and empty-desktop package capture are
synthetic; they do not demonstrate real pool performance. Follow
[performance capture](../performance-capture.md) and
[platform acceptance](../platform-acceptance.md) when a suitable environment
becomes available. Installer/signing work remains deferred.
