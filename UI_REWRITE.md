# UI rewrite (Avalonia preview)

This track replaces the Windows 7–era WinForms chrome with a new Avalonia shell.
It is **additive**: the supported production client remains `XenAdmin` until the
rewrite reaches feature parity for your environment.

## Status (handoff)

| Item | State |
|------|--------|
| Integration branch | `development` |
| Active PRs | `#21` soak-ready; `#22` (+ UX follow-up) alerts/graphs → `development` |
| Test build | CI artifacts **`drop-shell-win-x64`** and **`drop-shell-linux-x64`** |

## Projects

| Project | Role |
|---------|------|
| `XenAdmin` | Current supported WinForms client (`net8.0-windows`) |
| `XcpNgCenter.Shell` | Avalonia preview shell (`net8.0`) |
| `XcpNgCenter.Rfb` | WinForms-free RFB client core (`net8.0`) |

## Done through PR #22

- Connect/TOFU/tree/General/Storage/Network/Console; Logs/Tasks; New VM; Snapshots (disk-only)
- Full VM Properties; Clone/Copy/Migrate/Cross-pool/Move/Delete; New SR; Import/Export XVA
- **Alerts** — clickable sidebar badge opens an **app-global** alerts pane (all connected pools/servers); dismiss; fix-links (Repair SR via `SrRepairAction`; HA deferred to WinForms; multipath → Logs hint)
- **Performance** — RRD poller + Avalonia charts with hover crosshair/tooltips and time-axis labels by range; time-range picker (10m / 2h / 1w / 1y); load/save `pool.gui_config` layouts
- **Tab chrome** — Fluent accent + selected tab underline use brand orange (`#F07318`)
- **Multi-LUN HBA/FCoE** — select many LUNs → `ParallelAction` of `SrCreateAction`
- **OVF/OVA** — appliance import/export wizards alongside XVA

### Key shell layout

```
XcpNgCenter.Shell/
  Alerts/            ShellMessageAlert, ShellAlarmMessageAlert, ShellAlertFixActions
  Actions/           DismissAlertsAction, SaveShellGraphLayoutAction
  Services/          ShellAlertHub, Performance/*
  ViewModels/        … OvfImport/Export, NewSr multi-LUN, MainViewModel.AlertsGraphs
  Views/             … OvfImport/Export windows
  Controls/          RfbConsoleView, PerformanceChart
```

Shell references **`XenModel` + `XenCenterLib` + `XenOvfApi` + `XcpNgCenter.Rfb`** (not WinForms `XenAdmin`).

## Next

1. **Soak + merge PR `#21`**, then **`#22`** (alerts/graphs + hover tooltips + global alerts pane).
2. Later: HA/AD/DR wizards (enables richer HA alert fix-links); graph editor UI beyond save-current-defaults.
3. RDP stays WinForms-only.

## How to try

```bash
dotnet publish XcpNgCenter.Shell -c Release -r win-x64 --self-contained true -o artifacts/shell-win-x64
dotnet publish XcpNgCenter.Shell -c Release -r linux-x64 --self-contained true -o artifacts/shell-linux-x64
```

Pins: `%APPDATA%\XCP-ng\XCP-ng Center Shell\` or `~/.config/XCP-ng/XCP-ng Center Shell/`.

## Non-goals

- Feature parity with every WinForms wizard in one go
- Reviving `origin/avalonia` as-is
- Dropping WinForms before soak testing
