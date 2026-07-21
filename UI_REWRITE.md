# UI rewrite (Avalonia preview)

This track replaces the Windows 7–era WinForms chrome with a new Avalonia shell.
It is **additive**: the supported production client remains `XenAdmin` until the
rewrite reaches feature parity for your environment.

## Status (handoff)

| Item | State |
|------|--------|
| Integration branch | `development` |
| Active PR | `#21` — `cursor/shell-linux-migrate-harden-417d` → `development` (CI green; soak `drop-shell-linux-x64`) |
| Follow-on | `cursor/shell-alerts-graphs-704a` — Alerts + Performance graphs |
| Test build | CI artifacts **`drop-shell-win-x64`** and **`drop-shell-linux-x64`** |

**Phase status:** Phase 1–2 + Linux migrate harden / Import-Export on `#21`. Alerts + RRD performance graphs on the follow-on branch.

## Branch basis

Lives on `development` with the modernization stack (CI, security, `HttpClient`, Import Wizard
fixes, plugin DoEvents removal, and `XenAdmin` → `net8.0-windows`).

## Projects

| Project | Role |
|---------|------|
| `XenAdmin` | Current supported WinForms client (`net8.0-windows`) |
| `XcpNgCenter.Shell` | Avalonia preview shell (`net8.0`, Windows-first; Linux later) |
| `XcpNgCenter.Rfb` | WinForms-free RFB client core (`net8.0`, buffer callbacks) |

## Done in this track (through PR #21 + alerts/graphs)

- Brand-first welcome (form column + banner no longer overlap)
- Live connect + TOFU, tree, General/Storage/Network/Console (RFB fit/input/cursor/pop-out/CAD)
- Logs/Tasks via `ConnectionsManager.History` + `ShellActionRunner`
- New VM wizard; ISO attach/eject; Snapshots (disk-only)
- Full VM Properties; Clone / Copy / Migrate / Cross-pool / Move / Delete
- New SR wizard (NFS ISO/VHD, SMB, iSCSI+GFS2, HBA/FCoE)
- Import / Export XVA; sidebar UX polish; status icons
- **Alerts** — XAPI `Message` → `ShellMessageAlert` / `ShellAlarmMessageAlert` into XenModel `Alert` collection; badge + Alerts tab; dismiss selected/all via `DismissAlertsAction`
- **Performance** — WinForms-free `ShellRrdMaintainer` (`/host_rrds` `/vm_rrds` `/rrd_updates`) + Avalonia `PerformanceChart` for default Host/VM CPU/memory/network(/disk) series

### Key shell layout

```
XcpNgCenter.Rfb/     RfbClient (from VNCStream), IRfbFramebuffer, RfbStream
XcpNgCenter.Shell/
  Alerts/            ShellMessageAlert, ShellAlarmMessageAlert
  Actions/           DismissAlertsAction
  Services/          … ShellAlertHub, Performance/ShellRrdMaintainer + GraphBuilder
  ViewModels/        MainViewModel (+ Actions, AlertsGraphs), …
  Views/             MainWindow + wizards/dialogs
  Controls/          RfbConsoleView, PerformanceChart
```

Shell references **`XenModel` + `XenCenterLib` + `XcpNgCenter.Rfb`** (not WinForms `XenAdmin`).

## Next (priority order for following agents)

1. **Merge PR #21** after Linux soak of `drop-shell-linux-x64` is clean.
2. Merge alerts/graphs follow-on after soak.
3. Optional: zoom/time-range picker; persist graph layouts via `pool.gui_config`; alert fix-links (HA/SR) when those wizards land.
4. Later: HA/AD/DR wizards. RDP + plugins stay WinForms-only.
5. Optional: multi-LUN HBA create in one pass; OVF/OVA appliance wizards (XVA only shipped).

**RDP strategy (decided):** keep RDP on WinForms/`XenAdmin` only.

## How to try the preview

**Preferred:** CI artifact `drop-shell-win-x64` / `drop-shell-linux-x64` from Test Builds on the PR branch or `development`.

**From source:**

```bash
dotnet publish XcpNgCenter.Shell -c Release -r win-x64 --self-contained true -o artifacts/shell-win-x64
dotnet publish XcpNgCenter.Shell -c Release -r linux-x64 --self-contained true -o artifacts/shell-linux-x64
```

Pins / saved servers: `%APPDATA%\XCP-ng\XCP-ng Center Shell\` (Windows) or `~/.config/XCP-ng/XCP-ng Center Shell/` (Linux).

## Non-goals

- Feature parity with every WinForms wizard in one go
- Reviving `origin/avalonia` as-is
- Dropping WinForms before soak testing
