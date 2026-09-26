# UI rewrite (Avalonia preview)

This track replaces the Windows 7–era WinForms chrome with a new Avalonia shell.
It is **additive**: the supported production client remains `XenAdmin` until the
rewrite reaches feature parity for your environment.

## Status (handoff)

| Item | State |
|------|--------|
| Integration branch | `development` |
| Milestone | Property/settings parity soak + compact VM chrome + Linux soak |
| Test build | CI artifacts **`drop-shell-win-x64`** and **`drop-shell-linux-x64`** |
| GitHub Releases | Calendar tags `vYYYY.M.D.N`; optional workflow **Publish Shell Release** |

## Projects

| Project | Role |
|---------|------|
| `XenAdmin` | Current supported WinForms client (`net10.0-windows`) |
| `XcpNgCenter.Shell` | Avalonia preview shell (`net10.0`) |
| `XcpNgCenter.Rfb` | WinForms-free RFB client core (`net10.0`) |

## Done

- **Network management** — host/pool private and VLAN network creation, VLAN/uplink editing, names/descriptions/tags, automatic VM inclusion, MTU, and removal of unused networks. VM interfaces support add/edit/remove/connect/disconnect, network selection, generated/custom MACs, and bandwidth limits. Advanced controls add pool-wide NIC bonds (create/change mode/remove), host IPv4/IPv6 configuration, and SR-IOV provisioning/removal with topology and dependency checks. See [advanced networking](docs/advanced-networking.md) for supported operations and restrictions. Physical NIC removal and tunnel provisioning still use WinForms; live validation of the new controls is pending.
- Connect/TOFU/tree/General/Storage/Network/Console; Logs/Tasks; New VM with explicit host-aware storage placement; snapshot tree with disk, memory-checkpoint, and supported quiesced modes
- **Host Properties** — general/autostart, custom fields, metric alerts + email delivery, out-of-band power, multipathing, syslog, GPU policy/integrated GPU, pool live-patching/IGMP/TLS policies, clustering, and NRPE
- **VM Properties** — general/tags, custom fields, metric alerts, CPU/memory, boot/startup/HA, home server, GPU/USB, shadow-memory tuning, container integration, and cloud-config drive editing
- Clone/Copy/Migrate/Cross-pool/Move/Delete; New SR; Import/Export XVA + OVF/OVA
- VM chrome: power toggles top-right, kind+name header, compact CD/DVD row, console-first layout
- **Alerts** — always-visible sidebar badge; Copy on alert rows
- **Performance** — RRD charts with hover + local time axis; [graph editor](docs/graph-editor.md) for graph/source ordering and compatible layout saves, retained missing sources, and Cancel without persistence. RRD samples keep UTC identities through DST transitions and use the selected history archive.
- **Splash** / calendar **versioning** / GitHub **download, install, and restart updates**: installed-code bootstrap, fresh release/package verification, and protected staging after UAC elevation. Real UAC/apply/restart validation remains outstanding; see the [remediation record](docs/reviews/2026-09-07-remediation.md).
- **Settings** (beside alerts): saved-session/main-password controls; reconnect policy; direct/system/custom proxy with protected credentials and API timeout; graph/console/log display options and console shortcuts; TOFU/public-IP security policy; alert/OVF confirmations; privacy masking; About/update check
- **OVF validation** — fatal validation failures block import; non-fatal warnings require per-appliance acceptance unless explicitly disabled in Settings
- **Auto-reconnect** saved servers with stored passwords (DPAPI on Windows or a private AES device key on Unix by default; optional main-password vault uses PBKDF2-SHA256 and AES-GCM, unlocked once per session)
- **Linux soak hardening** — RFB cursor alpha preserved; GTK-friendly file pickers; XDG config paths; chart Outfit font via embedded family; update assets preserve executable modes

### Key shell layout

```
XcpNgCenter.Shell/
  Alerts/            ShellMessageAlert, ShellAlarmMessageAlert, ShellAlertFixActions
  Actions/           DismissAlertsAction, SaveShellGraphLayoutAction
  Services/          ShellAlertHub, Performance/*, ShellGitHubUpdateChecker, ShellUpdateInstaller,
                     ShellPaths, ShellExternalOpener, ShellAppSettings, SavedServerStore
  ViewModels/        … OvfImport/Export, NewSr multi-LUN, MainViewModel.AlertsGraphs/Updates
  Views/             … Splash, Settings, OvfImport/Export, global alerts overlay, ShellFilePicker
  Controls/          RfbConsoleView, PerformanceChart
```

Shell references **`XenModel` + `XenCenterLib` + `XenOvfApi` + `XcpNgCenter.Rfb`** (not WinForms `XenAdmin`).

## Next

1. Complete the [platform acceptance checklist](docs/platform-acceptance.md): **Linux/Windows desktop soak** and live multi-server, migration, import/export, advanced networking, alerts, graphs, and RFB checks. Native Linux CI covers automated tests and packaged desktop startup.
2. Verify download/apply/restart updates between published **`vYYYY.M.D.N`** builds. The starting binary must stamp a **lower** version than the release tag.
3. Follow the prioritized [modernization roadmap](docs/modernization-roadmap.md): HA/AD/DR wizards and WinForms designer metadata maintenance.
4. WinForms plugin tabs, Windows-specific external-tool launchers, and RDP stay in the production client.

## How to try

```bash
pwsh scripts/Publish-Shell.ps1 -RuntimeIdentifier win-x64 -ArchivePath artifacts/shell-win-x64.zip -BuildRevision 1
# Run on Linux to preserve executable permissions:
pwsh scripts/Publish-Shell.ps1 -RuntimeIdentifier linux-x64 -ArchivePath artifacts/shell-linux-x64.tar.gz -BuildRevision 1
```

Pins / prefs: `%APPDATA%\XCP-ng\XCP-ng Center Shell\` or `~/.config/XCP-ng/XCP-ng Center Shell/`.

**Linux notes:** device-protected passwords use a user-only AES `device.key` under the XDG config root. Main-password protection instead keeps its encryption key in unlocked session memory. Legacy main-password credentials migrate on unlock to a new document format that older shells cannot read; old copied credential/settings pairs remain exposed. In-place updates require a writable portable install directory and preserve the shell executable mode. Linux runtime validation of the latest credential/update changes remains outstanding.

## Non-goals

- Feature parity with every WinForms wizard in one go
- Reviving `origin/avalonia` as-is
- Dropping WinForms before soak testing is clean
