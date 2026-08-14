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
| `XenAdmin` | Current supported WinForms client (`net8.0-windows`) |
| `XcpNgCenter.Shell` | Avalonia preview shell (`net8.0`) |
| `XcpNgCenter.Rfb` | WinForms-free RFB client core (`net8.0`) |

## Done

- Connect/TOFU/tree/General/Storage/Network/Console; Logs/Tasks; New VM with explicit host-aware storage placement; snapshot tree with disk, memory-checkpoint, and supported quiesced modes
- **Host Properties** — general/autostart, custom fields, metric alerts + email delivery, out-of-band power, multipathing, syslog, GPU policy/integrated GPU, pool live-patching/IGMP/TLS policies, clustering, and NRPE
- **VM Properties** — general/tags, custom fields, metric alerts, CPU/memory, boot/startup/HA, home server, GPU/USB, shadow-memory tuning, container integration, and cloud-config drive editing
- Clone/Copy/Migrate/Cross-pool/Move/Delete; New SR; Import/Export XVA + OVF/OVA
- VM chrome: power toggles top-right, kind+name header, compact CD/DVD row, console-first layout
- **Alerts** — always-visible sidebar badge; Copy on alert rows
- **Performance** — RRD charts with hover + time axis; layout save
- **Splash** / calendar **versioning** / verified GitHub **download, install, and restart updates**
- **Settings** (beside alerts): saved-session/main-password controls; reconnect policy; direct/system/custom proxy with protected credentials and API timeout; graph/console/log display options and console shortcuts; TOFU/public-IP security policy; alert/OVF confirmations; privacy masking; About/update check
- **OVF validation** — fatal validation failures block import; non-fatal warnings require per-appliance acceptance unless explicitly disabled in Settings
- **Auto-reconnect** saved servers with stored passwords (DPAPI on Windows; AES key file on Linux)
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

1. Continue **Linux/Windows desktop soak** of CI artifacts against real pools (multi-server, migrate/move, import/export, alerts, graphs, RFB).
2. Verify download/apply/restart updates between published **`vYYYY.M.D.N`** builds. The starting binary must stamp a **lower** version than the release tag.
3. Later: HA/AD/DR wizards (richer HA alert fix-links); graph editor beyond save-current-defaults.
4. WinForms plugin tabs, Windows-specific external-tool launchers, and RDP stay in the production client.

## How to try

```bash
dotnet publish XcpNgCenter.Shell -c Release -r win-x64 --self-contained true -p:BuildRevision=1 -o artifacts/shell-win-x64
dotnet publish XcpNgCenter.Shell -c Release -r linux-x64 --self-contained true -p:BuildRevision=1 -o artifacts/shell-linux-x64
```

Pins / prefs: `%APPDATA%\XCP-ng\XCP-ng Center Shell\` or `~/.config/XCP-ng/XCP-ng Center Shell/`.

**Linux notes:** saved passwords use a user-only AES `device.key` under the XDG config root. In-place updates require a writable portable install directory and preserve the shell executable mode.

## Non-goals

- Feature parity with every WinForms wizard in one go
- Reviving `origin/avalonia` as-is
- Dropping WinForms before soak testing is clean
