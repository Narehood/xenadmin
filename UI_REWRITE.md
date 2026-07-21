# UI rewrite (Avalonia preview)

This track replaces the Windows 7–era WinForms chrome with a new Avalonia shell.
It is **additive**: the supported production client remains `XenAdmin` until the
rewrite reaches feature parity for your environment.

## Status (handoff)

| Item | State |
|------|--------|
| Integration branch | `development` |
| Merged | `#20` — Phase 1–2 parity |
| Follow-up | `#21` — Linux soak + migrate harden + VM chrome |
| Test build | CI artifacts **`drop-shell-win-x64`** and **`drop-shell-linux-x64`** |

**Phase status:** Phase 1 + Phase 2 on `development`. PR `#21` hardens Move/migrate edge cases (WinForms parity) and VM chrome; Linux desktop soak next.

## Branch basis

Lives on `development` with the modernization stack (CI, security, `HttpClient`, Import Wizard
fixes, plugin DoEvents removal, and `XenAdmin` → `net8.0-windows`).

## Projects

| Project | Role |
|---------|------|
| `XenAdmin` | Current supported WinForms client (`net8.0-windows`) |
| `XcpNgCenter.Shell` | Avalonia preview shell (`net8.0`, Windows + Linux publish) |
| `XcpNgCenter.Rfb` | WinForms-free RFB client core (`net8.0`, buffer callbacks) |

## Done in this track (through PR #20 + follow-ups)

- Brand-first welcome (form column + banner no longer overlap)
- Live connect + TOFU, tree, General/Storage/Network/Console (RFB fit/input/cursor/pop-out/CAD)
- Logs/Tasks via `ConnectionsManager.History` + `ShellActionRunner` (UI-thread marshaling; event unsubscribe on dismiss)
- New VM wizard (primary disk honors Storage-step size); ISO attach/eject; Snapshots (disk-only)
- **Full VM Properties** — General/tags, CPU/memory, boot, HA/startup, home server, GPU, USB
- **Clone / Copy / Migrate / Cross-pool / Move / Delete**
  - Intra-pool live migrate: `VMMigrateAction`
  - Storage / cross-pool: `VMCrossPoolMigrateAction` with **per-disk SR** and **per-VIF network** maps (+ apply-to-all); empty VIF map for intra-pool
  - Halted Move: prefers migrate_send **Move** dialog when licensed + CBT-clear + eligible hosts; **intra-pool** finish uses `VMMoveAction` (copy+destroy) like WinForms; else simple Move SR picker
  - CBT / `RestrictCrossPoolMigrate` guards; `CanFitDisks` on SR pickers; reject no-op disk maps
  - SR pickers label shared vs host-local, filter by target-host visibility, skip current location / non-migratable SRs
- **New SR wizard** — NFS ISO, SMB/CIFS ISO, NFS VHD, SMB, iSCSI (+ optional GFS2), **HBA (`lvmohba`)** / **FCoE (`lvmofcoe`)** with LUN probe (+ GFS2 on HBA)
- HA start/resume prompts; richer **start-failure host table** (per-host assert_can_boot_here + resume CPU vendor check; session logout)
- **VM chrome (WinForms-aligned):** power bar above tabs; Force* in context menu; Properties on General; Console ISO selector (and compact pop-out toolbar); WinForms status icons for pool/host/VM/SR (running/halted/suspended/paused/migrating/lifecycle); snapshot **tree**; storage nodes under hosts (local) and pool (shared)
- Layout polish: wrap actions, scrollable detail, console Height=520, Properties scroll padding; denser infra tree; copyable row orange border on hover only; pop-out CAD = Ctrl+Alt+Del

### Key shell layout

```
XcpNgCenter.Rfb/     RfbClient (from VNCStream), IRfbFramebuffer, RfbStream
XcpNgCenter.Shell/
  Services/          Bootstrap, TOFU, vault, builders, HostedConsoleSession, ShellActionRunner, HA prompts, ShellStoragePicker
  ViewModels/        MainViewModel (+ Actions), wizards/dialogs, ActionLogRow
  Views/             MainWindow + Properties/Clone/Copy/Migrate/CrossPool/Move/Delete/NewVm/NewSr/…
```

Shell references **`XenModel` + `XenCenterLib` + `XcpNgCenter.Rfb`** (not WinForms `XenAdmin`).

## Next (priority order for following agents)

1. **Merge PR `#21`** after soak feedback is clean and CI is green.
2. **Linux desktop soak** — run `drop-shell-linux-x64` (Drawing.Common is Windows-only; disk snapshots OK; TOFU/RFB/multi-server).
3. Harden further migrate edge cases if soak finds more.
4. Optional: multi-LUN HBA create in one pass.
5. Alerts, performance graphs, HA/AD/DR wizards (later). RDP + plugins stay WinForms-only.

**RDP strategy (decided):** keep RDP on WinForms/`XenAdmin` only.

**Linux notes:** Password vault is Windows DPAPI-only (hosts/usernames still save). Pins/saved servers use `~/.config/XCP-ng/XCP-ng Center Shell/`. Do not enable memory/quiesced snapshots until screenshot path is Drawing-free.

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
