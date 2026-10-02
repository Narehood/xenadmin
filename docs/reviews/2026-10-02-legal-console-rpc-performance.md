# October 2 legal and modernization implementation

The requested batch restores redistribution notices and implements the selected
console reliability, RPC transport and performance work. Signing is deferred at
the user's direction. Base: merged PR #62, `84cb0b57c`; target: `development`.
WinForms remains supported and the Avalonia shell remains an additive preview.

## Changes and contract checks

`6896346c0` embeds the unchanged original `LICENSE` and reviewed
`THIRD-PARTY-NOTICES.txt` in the shared library. Both clients display the full
terms offline; build/publish directories and portable archives contain exact
copies. BSD binary clauses require the copyright, conditions and disclaimer,
rather than a copyright summary alone. The bundle retains the historical
source/asset notices and records current package licenses, Apache log4net's
upstream NOTICE, Outfit OFL and the bundled .NET runtime. DiscUtils's license is
taken from the repository commit recorded in its 0.16.13 NuGet metadata. Native
edit controls are checked for complete text, including after handle creation;
the About probe verifies the narrow viewport rather than visibility flags alone.
Missing/truncated license or third-party files, unknown dependency versions and
an unrecorded runtime version fail before a payload executes. Maintenance and
installer limits are in [legal-notices.md](../legal-notices.md).

`0da95103b` separates RPC serialization from transport. Modern calls reuse an
owned `HttpClient` pool, with separate duplicate-client pools and retirement on
policy changes. Explicit TLS destination names go through the existing TOFU
policy; strict validation applies without one. Proxy credentials, cookies,
redirects, protocol choice, XenAPI errors and method-only logging are preserved.
Timeouts cover response-body reading; explicit cancellation has a separate
exception contract. Generated methods remain synchronous, with an explicit async
entry point for JSON callers. Framework retains its HTTP implementation. Action
and logout cleanup release locally owned transports without logging out copied
pool tokens or disposing borrowed sessions. No application mutation retry loop
was added. See [rpc-transport.md](../rpc-transport.md).

`0e1381a9d` bounds shell console startup using the configured connection timeout.
Cancellation reaches socket/DNS startup, proxy/TLS negotiation and a silent RFB
handshake. A completed handshake disarms the deadline; idle displays retain an
unbounded read lifetime. Stale generations cannot acquire a replacement stream.
The phase status leaves Connecting on failure. Tests cover silent HTTP/proxy/TLS
peers, blocked handshake, successful idle behavior, cancellation and the actual
hosted-session state. This does not close the Debian reboot incident.

The opt-in performance provider records inventory event coalescing, build and
reconciliation, detail phases, console startup and bitmap flushes. Its payloads
contain fixed operation labels, timings, counts and allocation measurements;
identifier/credential/clipboard data is excluded. Disabled timing scopes do not
allocate. The production TreeView probe now includes fully expanded hosts and
preserves selection, expansion and migration behavior. Capture live event/detail
traces before another optimization.

## Validation

Local evidence is under `artifacts/legal-console-rpc-performance-20261002-accepted`,
`artifacts/legal-about-final-20261002` and
`artifacts/legal-notices-final-20261002` (ignored artifacts). The first runner
passes **25 automated checks**; the final focused rerun covers license source
provenance and the stronger viewport assertion without changing RPC/performance
behavior.

- Both full solution configurations build with the pinned SDK/runtime.
- Shell: **1,005 passing cases per configuration**, including 28 added cases.
- Shared libraries: **88 passing cases on each of net481/net10.0**, including
  15 loopback RPC contract cases on both targets.
- WinForms: **339 lifecycle/designer checks**, **23 archive checks** and
  **32,138 resources in 289 sets** per configuration; **12 proxy cases**.
- Six actual offscreen UI modes pass; final About coverage has **24 checks**,
  760x650/440x400 sizes and 1/1.5/2 render scales. Expanded inventory coverage
  has **378 checks**. Screenshots were inspected.
- Six rejected legal archive fixtures pass; a fresh self-contained Windows
  archive preserves full legal files and package/runtime coverage and starts
  successfully through the isolated helper smoke checks.
- Portable lockfile hashes are unchanged during full and final validation.
  The shared test project's added model reference intentionally adds its pinned
  dependency graph; no central package versions changed.

The broader run caught and corrected split-packet assumptions in the new socket
test, a nonblocking ValueTask wait in the new socket path, and Framework handling
of a declined redirect/non-200 response. Subsequent complete acceptance passes.
Imported license text preserves upstream whitespace; code diffs were checked
without normalizing the repository's existing line endings. Existing Windows ACL
platform-analysis warnings remain in the unchanged updater tests.

## Expanded inventory measurements

These are paired synthetic cache measurements with the production tree,
templates and styles on Windows 10.0.26300 / .NET 10.0.12. Each median uses five
samples after two warmups, with 200 notifications per burst and one rebuild.
The replacement comparison is a reference implementation; node retention was
implemented in the October 1 batch. This batch extends its measurement coverage.

| Hosts / VMs | Partly collapsed replacement / reuse (ms) | Fully expanded replacement / reuse (ms) |
| --- | --- | --- |
| 4 / 100 | 46.930 / 1.030 | 185.597 / 1.047 |
| 16 / 1,000 | 87.245 / 2.400 | 733.118 / 1.478 |
| 64 / 5,000 | 101.362 / 9.758 | 4,499.388 / 9.507 |

No saved profile, credentials or live pool was loaded. The probe bypasses
MainViewModel initialization and detail panes, and excludes real network/event
rates, native input/GPU composition and prolonged console activity. Allocation
numbers cover the measuring thread. Production phase events are available for
those future live exercises; these medians are not deployment performance claims.

## Remaining acceptance

This is an implementation and local validation record, not independent reviewer
approval. Check hosted Windows/Linux and CodeQL results on the current PR head.
Trusted local RDP interop was reused; actual Visual Studio interop generation
was not exercised. Physical desktop/scaling, Wise MSI installation and legal
dialog access, updater/UAC/rollback/restart, native RDP guest login and live-pool
login/proxy/polling/reconnect/import/export/migration tests remain pending. No
release or live-pool operation is performed by this batch. Signing remains
deferred as requested.
