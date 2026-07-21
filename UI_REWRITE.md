# UI rewrite (Avalonia preview)

This track replaces the Windows 7–era WinForms chrome with a new Avalonia shell.
It is **additive**: the supported production client remains `XenAdmin` until the
rewrite reaches feature parity for your environment.

## Status (handoff)

| Item | State |
|------|--------|
| Integration branch | `development` (tip includes merged `#20`–`#24`) |
| Open Cursor PRs / branches | Linux soak fixes (this track) |
| Test build | CI artifacts **`drop-shell-win-x64`** and **`drop-shell-linux-x64`** |
| GitHub Releases | **`v2026.7.21.1`** published (update-banner E2E); optional workflow **Publish Shell Release** |

## Projects

| Project | Role |
|---------|------|
| `XenAdmin` | Current supported WinForms client (`net8.0-windows`) |
| `XcpNgCenter.Shell` | Avalonia preview shell (`net8.0`) |
| `XcpNgCenter.Rfb` | WinForms-free RFB client core (`net8.0`) |

## Done on `development` (through PR `#24` + soak fixes)

- Connect/TOFU/tree/General/Storage/Network/Console; Logs/Tasks; New VM; Snapshots (disk-only)
- Full VM Properties; Clone/Copy/Migrate/Cross-pool/Move/Delete; New SR; Import/Export XVA + OVF/OVA
- VM chrome: power toggles, status icons, snapshot tree, Console ISO, denser tree/General
- **Alerts** — always-visible sidebar badge (gray `0` / orange count) opens an **app-global** alerts pane
- **Performance** — RRD charts with hover tooltips + time-axis labels; ranges 10m / 2h / 1w / 1y; layout save
- **Splash** — paints before bootstrap; shows `year.month.day.revision`; held ~2.8s
- **Versioning** — `year.month.day.revision` (CI `-p:BuildRevision=${{ github.run_number }}`)
- **Update banner** — GitHub Releases check; bottom-right View release / Dismiss (Linux uses `xdg-open`)
- Brand-orange Fluent tab underline; multi-LUN HBA/FCoE
- **Linux soak hardening** — RFB cursor alpha preserved; GTK-friendly file pickers; XDG config fallback; chart Outfit font via embedded family

### Key shell layout

```
XcpNgCenter.Shell/
  Alerts/            ShellMessageAlert, ShellAlarmMessageAlert, ShellAlertFixActions
  Actions/           DismissAlertsAction, SaveShellGraphLayoutAction
  Services/          ShellAlertHub, Performance/*, ShellGitHubUpdateChecker, ShellVersionInfo,
                     ShellPaths, ShellExternalOpener
  ViewModels/        … OvfImport/Export, NewSr multi-LUN, MainViewModel.AlertsGraphs/Updates
  Views/             … Splash, OvfImport/Export, global alerts overlay, ShellFilePicker
  Controls/          RfbConsoleView, PerformanceChart
```

Shell references **`XenModel` + `XenCenterLib` + `XenOvfApi` + `XcpNgCenter.Rfb`** (not WinForms `XenAdmin`).

## Next

1. Continue **Linux desktop soak** of `drop-shell-linux-x64` against real pools (multi-server, migrate/move, import/export, alerts, graphs, RFB).
2. Verify update banner against published **`v2026.7.21.1`** (or newer via workflow **Publish Shell Release**). Soak binary must stamp a **lower** version than the release tag.
3. Later: HA/AD/DR wizards (richer HA alert fix-links); graph editor beyond save-current-defaults; memory/quiesced snapshots.
4. RDP stays WinForms-only.

## How to try

```bash
dotnet publish XcpNgCenter.Shell -c Release -r win-x64 --self-contained true -p:BuildRevision=1 -o artifacts/shell-win-x64
dotnet publish XcpNgCenter.Shell -c Release -r linux-x64 --self-contained true -p:BuildRevision=1 -o artifacts/shell-linux-x64
```

Pins / prefs: `%APPDATA%\XCP-ng\XCP-ng Center Shell\` or `~/.config/XCP-ng/XCP-ng Center Shell/`.

**Linux notes:** saved passwords are not persisted (no DPAPI); re-enter on reconnect. Update banner **View release** uses `xdg-open`.

## Non-goals

- Feature parity with every WinForms wizard in one go
- Reviving `origin/avalonia` as-is
- Dropping WinForms before soak testing
