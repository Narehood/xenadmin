# UI rewrite (Avalonia preview)

This track replaces the Windows 7–era WinForms chrome with a new Avalonia shell.
It is **additive**: the supported production client remains `XenAdmin` until the
rewrite reaches feature parity for your environment.

## Status (handoff)

| Item | State |
|------|--------|
| Integration branch | `development` |
| Active PR | `#20` — `cursor/shell-phase1-parity-abb3` → `development` |
| Test build | CI artifacts **`drop-shell-win-x64`** and **`drop-shell-linux-x64`** |

**Phase status:** Phase 1 + Phase 2 action/Properties/SR slice is implemented on PR `#20`. Soak-test that build, then merge to `development`.

## Branch basis

Lives on `development` with the modernization stack (CI, security, `HttpClient`, Import Wizard
fixes, plugin DoEvents removal, and `XenAdmin` → `net8.0-windows`).

## Projects

| Project | Role |
|---------|------|
| `XenAdmin` | Current supported WinForms client (`net8.0-windows`) |
| `XcpNgCenter.Shell` | Avalonia preview shell (`net8.0`, Windows-first; Linux later) |
| `XcpNgCenter.Rfb` | WinForms-free RFB client core (`net8.0`, buffer callbacks) |

## Done in this track (through PR #20)

- Brand-first welcome (form column + banner no longer overlap)
- Live connect + TOFU, tree, General/Storage/Network/Console (RFB fit/input/cursor/pop-out/CAD)
- Logs/Tasks via `ConnectionsManager.History` + `ShellActionRunner`
- New VM wizard; ISO attach/eject; Snapshots (disk-only)
- **Full VM Properties** — General/tags, CPU/memory, boot, HA/startup, home server, GPU, USB
- **Clone / Copy / Migrate / Cross-pool / Move / Delete**
  - Intra-pool live migrate: `VMMigrateAction`
  - Cross-pool / storage migrate or copy: simplified `VMCrossPoolMigrateAction` dialog (single SR + network map)
  - Halted move-to-SR: `VMMoveAction`
- **New SR wizard** — NFS ISO, SMB/CIFS ISO, NFS VHD, SMB storage, iSCSI (+ optional **GFS2**) with IQN/LUN probe + CHAP
- Layout polish: wrap actions, scrollable detail, console Height=520, Properties scroll padding

### Key shell layout

```
XcpNgCenter.Rfb/     RfbClient (from VNCStream), IRfbFramebuffer, RfbStream
XcpNgCenter.Shell/
  Services/          Bootstrap, TOFU, vault, builders, HostedConsoleSession, ShellActionRunner
  ViewModels/        MainViewModel (+ Actions), wizards/dialogs, ActionLogRow
  Views/             MainWindow + Properties/Clone/Copy/Migrate/CrossPool/Move/Delete/NewVm/NewSr/…
```

Shell references **`XenModel` + `XenCenterLib` + `XcpNgCenter.Rfb`** (not WinForms `XenAdmin`).

## Next (priority order for following agents)

1. **Merge PR #20** after soak feedback is clean.
2. **Richer cross-pool UI** — per-disk / per-VIF mapping (current dialog maps all disks→one SR, all VIFs→one network).
3. **HBA / FCoE SR** front-ends (`lvmohba` / `lvmofcoe`) if hardware available to test.
4. **Linux desktop soak** — run `drop-shell-linux-x64`.
5. Alerts, performance graphs, HA/AD/DR wizards (later). RDP + plugins stay WinForms-only.

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
