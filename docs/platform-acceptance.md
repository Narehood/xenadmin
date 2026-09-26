# Platform acceptance

Use the same source commit and packaged archives throughout an acceptance pass.
Record the commit, calendar version, archive SHA-256, OS version, desktop/session
type, display scale, and pass/fail evidence. A missing environment means **pending**,
not passed. The .NET 10 migration does not by itself establish desktop or pool
compatibility.

## Automated on every PR

`Test Builds` uses the SDK in `global.json` on Windows and Ubuntu 24.04:

| Gate | Windows | Native Linux |
| --- | --- | --- |
| Locked source restore | Entire solution | Shared and shell project graphs |
| Full WinForms/RDP build | Release and Debug, Windows SDK interop tools | Not applicable |
| Shared tests | net481 and net10.0 | net10.0 |
| Shell tests | Release and Debug | Release and Debug |
| Self-contained package | win-x64 ZIP | linux-x64 tar.gz |
| Package execution | Four malformed updater helper modes reject with exit 1 | Same, using the native Linux executable |
| Runtime boundary | Bundled .NET 10; startup hooks remain disabled even with a hostile hook environment | Same |
| Desktop startup | Manual acceptance below | Real Avalonia main window becomes visible under Xvfb/Openbox, completes post-window startup, and survives three seconds |

`scripts/Publish-Shell.ps1` seeds RID-specific lockfiles under each project's
`obj` directory and leaves checked-in portable locks unchanged. CI checks that
invariant after publishing. It packages a fresh publish directory, keeps shell
and runtime files at the archive root for deployed updaters, and preserves Linux
executable permissions. Download the nested ZIP/tar.gz from Actions; uploading a
bare Linux directory would lose its executable modes.

The isolated CI desktop starts Openbox on its private Xvfb display to exercise a
normal X11 window-manager session. The verifier still requires the exact visible
main-window title, its owning process ID, completed startup, and a surviving
process; starting a window manager does not bypass those checks. Failed window
discovery includes raw X11 window identities in the uploaded evidence.
Avalonia 11.3.20 [looks up existing X11 atoms](https://github.com/AvaloniaUI/Avalonia/blob/11.3.20/src/tools/DevGenerators/X11AtomsGenerator.cs);
a bare Xvfb display can lack the PID atom required for process identification.
[Openbox creates that metadata](https://github.com/danakj/openbox/blob/master/obt/prop.c).
The verifier waits for its EWMH readiness property before starting Avalonia.

`scripts/verify-shell-package.py` extracts and **executes a trusted local build**.
It checks archive-root contents, bundled runtime metadata, Linux executable mode,
and actual helper exit codes with `DOTNET_STARTUP_HOOKS` pointing at a missing
assembly. Linux desktop startup uses isolated XDG config/cache/data directories;
it never opens saved profiles or connects to a pool. Its trace must reach
`post-window-startup` and the visible window must have the main-window title, so
a surviving splash or startup-failure dialog cannot satisfy the check. The smoke
terminates its own process afterward; this does not validate ordinary UI shutdown.

The release workflow uses the same packaging and executable checks before
attaching an asset. Smoke JSON, startup logs, and test TRX files are uploaded even
when a check fails. This is not a substitute for a release soak.

Local examples, after a locked source restore and the test suites:

```powershell
./scripts/Publish-Shell.ps1 -RuntimeIdentifier win-x64 -ArchivePath artifacts/shell.zip
python scripts/verify-shell-package.py --archive artifacts/shell.zip --rid win-x64 --evidence-directory artifacts/smoke-windows
```

```bash
pwsh ./scripts/Publish-Shell.ps1 -RuntimeIdentifier linux-x64 -ArchivePath artifacts/shell.tar.gz
xvfb-run -a python3 scripts/verify-shell-package.py --archive artifacts/shell.tar.gz --rid linux-x64 --evidence-directory artifacts/smoke-linux --desktop --window-manager
```

The verifier requires Python 3.12+; Linux also needs `xvfb`, `xauth`, `xdotool`, `openbox`, `x11-utils`,
`libice6`, `libsm6`, and `libfontconfig1`. Run each package on its native OS.
An existing desktop session may be used in place of Xvfb for a local smoke;
omit `--window-manager` when that session already has a window manager.

## Desktop and updater acceptance: pending user validation

No disposable pool, VM, or test machine is available for this implementation
pass. The user will perform the following checks later. Keep every row pending
until its evidence is recorded; ordinary unit tests do not prove UAC behavior,
token ownership, graphics-driver compatibility, or a successful server operation.

Use a disposable profile and installation directory, retaining an untouched copy
of the original profile for recovery. A migrated main-password profile must not
be reopened by an older client. Use a version newer than the installed binary for
update offers and record both versions. Never exercise failure injection or
network reconfiguration against a production management path.

| Scenario | Procedure and required outcome |
| --- | --- |
| Windows desktop | Start supported Windows versions at 100/150/200% scaling; verify splash handoff, empty/saved/locked profiles, Settings tabs, dark/light/system themes, color picker, tooltips, minimum-size layout, and ordinary close/reopen. |
| Linux desktop | Start on the target distribution under X11 and Wayland/XWayland; repeat settings, scaling, clipboard dialog, file pickers, and close/reopen. Confirm native dependencies and fonts without the build SDK installed. |
| Writable portable update | On Windows and Linux, update a disposable writable installation. Confirm fresh release authentication, progress, version after restart, settings retention, shell/runtime files replaced, cleanup, and Linux executable modes. |
| Protected Windows update | Install under a protected directory; accept UAC. Confirm staging and helpers are administrator-owned, replacement succeeds, the progress window survives shutdown, and the relaunched full app has the original user's **unelevated** token. Record owner and integrity level. |
| UAC cancellation | Cancel elevation. Original app remains available; version and files are unchanged; error/retry UI is usable; no elevated full app remains. |
| Different-account elevation | Supply a separate administrator account. Confirm the original user's profile is retained, staging is protected, and the full app restarts as the original user without an elevated token. |
| Authentication failure | Disconnect networking during fresh release authentication or use a deliberately corrupted copy of an archive. Installation must not begin from unauthenticated/cached executable content. Record retained installation hashes and visible recovery guidance. |
| Replacement failure/rollback | In an isolated test installation, hold a replaceable file open or inject an I/O failure. Confirm original files are restored, failure stays visible, the app can reopen, and retry works. Process-crash recovery does not establish power-loss durability. |
| Broker/restart failure | Prevent the restarted executable from surviving startup. Confirm the progress UI reports failure instead of declaring completion; recovery must never launch the full app elevated. |
| Legacy/custom repository | Verify a legacy bootstrap gets manual-upgrade guidance; a custom repository gets manual-install guidance before protected/elevated installation. Do not relax authentication to make either path automatic. |

## Live-pool acceptance: pending user validation

Use an isolated pool with out-of-band host access, an expendable guest, test
storage, and a documented recovery path. Record server versions and driver/NIC
capabilities. A before/after inventory and completed server task are stronger
evidence than a dismissed UI dialog.

| Workflow | Acceptance evidence |
| --- | --- |
| Connect/TLS/vault | First trust, matching and changed pins, lock/unlock, saved metadata, reconnect after loss, and no credentials in logs. |
| Guest/host console | Dock/pop out/full-screen, resize/fold, keyboard release, fit vs 1:1 scrolling, clipboard review/consent/cancellation, retry after guest failure, and host-console retry limits. Check actual guest keystrokes with a harmless draft. |
| Basic networking | Create/edit/remove a private network and VLAN; add/edit/remove/connect/disconnect VIFs; confirm server state, hot-plug guidance, management/cluster protection, and draft preservation across cache refresh. |
| Advanced networking | Exercise supported bond modes, host IPv4/IPv6 configuration, and SR-IOV only on compatible expendable hardware. Confirm warning/confirmation behavior, stale-selection rejection, rollback/reconnect plan, physical-link recovery, and management/cluster restrictions. Unsupported capability must remain unavailable with an explanation. |
| Migration | Move an expendable running and stopped guest to an eligible host/storage; verify task progress, resulting host/storage, console reconnection, and rejection when prerequisites change. |
| Import/export | Import/export an expendable VM as XVA and supported OVF/OVA; verify disks, boot, manifests, cancellation, temporary-file cleanup, and unchanged source appliance after failure. Include a large OVA without UI stalls. |
| Performance/history | On a representative large inventory, capture tree refresh/selection responsiveness, allocations/GC, console activity, and long-range RRD polling. Record workload and timings before proposing a performance refactor. |
| Graph editor | Add/remove/reorder graphs and sources on a host and running VM; rename, save, reopen in both clients, and confirm Cancel leaves the saved layout unchanged. Retain an unavailable source, select empty and populated week/year ranges, and compare timestamps with the server, including DST transitions. Confirm denied/stale saves retain the draft. |

### Host IP editor scope

Select a host and use **Configure host IP** in its Network tab. Select an attached
interface, then IPv4 or IPv6 and the assignment mode. Static IPv4 accepts a dotted
subnet mask; static IPv6 requires an address/prefix pair. DNS entries must match
the selected address family. The other family's addresses and DNS are retained.
The editor preserves the management interface and its preferred address family;
it does not move management to another NIC. Apply one family at a time and keep
host console access available before confirming.

HA, cluster/protected interfaces, bond members, tunnels, unmanaged interfaces,
and SR-IOV interfaces are rejected. Multiple existing static IPv6 addresses must
be managed with server tools because this API accepts one address per call.
Changes to an interface's identity or configuration while the editor is open are
rejected before submission. A lost connection is reported as an unconfirmed
operation: inspect the host's address and reconnect manually, verifying its
certificate, before retrying. Automatic rollback or reconnect to an unverified
new address is not promised.

IPv4 Static/DHCP uses the same single-host `ChangeNetworkingAction` as WinForms.
Disabling IPv4 calls `PIF.async_reconfigure_ip` directly with the reviewed empty
address, mask, and gateway; the shared bring-up path retains the previous address
for non-static modes. IPv6 uses `PIF.async_reconfigure_ipv6`; both direct paths
use shared task polling. The planner is covered with
synthetic-cache regression cases for stale identities/config, management-family
protection, address/mask/gateway/DNS validation, duplicate addresses, unchanged
settings, family preservation, DHCP/autoconf, protected topology, and drafts.
Loopback JSON-RPC tests execute the IPv4-disable worker and verify its exact
request, preserved DNS/IPv6/management state, stale-identity rejection, cleanup,
and no mutation retry after a server rejection or invalid session.
Actual connectivity and server task behavior remain the user's manual check.
The [XAPI interface reference](https://xapi-project.github.io/new-docs/xen-api/classes/index.print.html#pif)
documents the configuration calls; current [XAPI implementation](https://github.com/xapi-project/xen-api/blob/master/ocaml/xapi/xapi_pif.ml)
preserves opposite-family DNS and rejects disabling the primary management family.

## Evidence template

Copy this into the PR/release acceptance record. Replace every pending item with
a result and evidence location only after that check has run.

```text
Source commit / version:
Archive / SHA-256:
OS / desktop session / scale:
Server version / topology / NIC driver (where relevant):
Scenario:
Expected result:
Actual result: pending | pass | fail
Logs / screenshots / task IDs (remove secrets):
Remaining limitations:
Tester / UTC date:
```

Implementation references: [setup-dotnet global.json support](https://github.com/actions/setup-dotnet)
and [NuGet lock-file location and locked restore](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies).
