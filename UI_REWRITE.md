# UI rewrite (Avalonia preview)

This track replaces the Windows 7–era WinForms chrome with a new Avalonia shell.
It is **additive**: the supported production client remains `XenAdmin` until the
rewrite reaches feature parity for your environment.

## Status (handoff)

| Item | State |
|------|--------|
| Integration branch | `development` (tip includes merged `#20`–`#23`) |
| Open Cursor PRs / branches | **none** |
| Test build | CI artifacts **`drop-shell-win-x64`** and **`drop-shell-linux-x64`** |

## Projects

| Project | Role |
|---------|------|
| `XenAdmin` | Current supported WinForms client (`net8.0-windows`) |
| `XcpNgCenter.Shell` | Avalonia preview shell (`net8.0`) |
| `XcpNgCenter.Rfb` | WinForms-free RFB client core (`net8.0`) |

## Done on `development` (through PR `#23`)

- Connect/TOFU/tree/General/Storage/Network/Console; Logs/Tasks; New VM; Snapshots (disk-only)
- Full VM Properties; Clone/Copy/Migrate/Cross-pool/Move/Delete; New SR; Import/Export XVA + OVF/OVA
- VM chrome: power toggles, status icons, snapshot tree, Console ISO, denser tree/General
- **Alerts** — always-visible sidebar badge (gray `0` / orange count) opens an **app-global** alerts pane
- **Performance** — RRD charts with hover tooltips + time-axis labels; ranges 10m / 2h / 1w / 1y; layout save
- **Splash** — paints before bootstrap; shows `year.month.day.revision`; held ~2.8s
- **Versioning** — `year.month.day.revision` (CI `-p:BuildRevision=${{ github.run_number }}`)
- **Update banner** — GitHub Releases check; bottom-right View release / Dismiss
- Brand-orange Fluent tab underline; multi-LUN HBA/FCoE

### Key shell layout

```
XcpNgCenter.Shell/
  Alerts/            ShellMessageAlert, ShellAlarmMessageAlert, ShellAlertFixActions
  Actions/           DismissAlertsAction, SaveShellGraphLayoutAction
  Services/          ShellAlertHub, Performance/*, ShellGitHubUpdateChecker, ShellVersionInfo
  ViewModels/        … OvfImport/Export, NewSr multi-LUN, MainViewModel.AlertsGraphs/Updates
  Views/             … Splash, OvfImport/Export, global alerts overlay
  Controls/          RfbConsoleView, PerformanceChart
```

Shell references **`XenModel` + `XenCenterLib` + `XenOvfApi` + `XcpNgCenter.Rfb`** (not WinForms `XenAdmin`).

## Next

1. **Linux desktop soak** of `drop-shell-linux-x64` from `development` (TOFU, RFB, multi-server, migrate/move, import/export, alerts, graphs).
2. Publish a GitHub Release tagged `vYYYY.M.D.N` so the update banner can be verified end-to-end.
3. Later: HA/AD/DR wizards (richer HA alert fix-links); graph editor beyond save-current-defaults; memory/quiesced snapshots.
4. RDP stays WinForms-only.

## How to try

```bash
dotnet publish XcpNgCenter.Shell -c Release -r win-x64 --self-contained true -p:BuildRevision=1 -o artifacts/shell-win-x64
dotnet publish XcpNgCenter.Shell -c Release -r linux-x64 --self-contained true -p:BuildRevision=1 -o artifacts/shell-linux-x64
```

Pins / prefs: `%APPDATA%\XCP-ng\XCP-ng Center Shell\` or `~/.config/XCP-ng/XCP-ng Center Shell/`.

## Non-goals

- Feature parity with every WinForms wizard in one go
- Reviving `origin/avalonia` as-is
- Dropping WinForms before soak testing
