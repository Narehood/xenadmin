# UI rewrite (Avalonia preview)

This track replaces the Windows 7–era WinForms chrome with a new Avalonia shell.
It is **additive**: the supported production client remains `XenAdmin` until the
rewrite reaches feature parity for your environment.

## Status (handoff)

| Item | State |
|------|--------|
| Integration branch | `development` |
| Merged | `#20` — Phase 1–2 parity |
| Active PR | `#21` — Linux soak + migrate harden + VM chrome + Import/Export (**ready for soak / merge**) |
| Test build | CI artifacts **`drop-shell-win-x64`** and **`drop-shell-linux-x64`** |

**Phase status:** Phase 1–2 on `development`. PR `#21` is feature-complete for this pass (Cursor draft-review migrate gaps closed). Soak `drop-shell-linux-x64`, then merge.

## Branch basis

Lives on `development` with the modernization stack (CI, security, `HttpClient`, Import Wizard
fixes, plugin DoEvents removal, and `XenAdmin` → `net8.0-windows`).

## Projects

| Project | Role |
|---------|------|
| `XenAdmin` | Current supported WinForms client (`net8.0-windows`) |
| `XcpNgCenter.Shell` | Avalonia preview shell (`net8.0`, Windows + Linux publish) |
| `XcpNgCenter.Rfb` | WinForms-free RFB client core (`net8.0`, buffer callbacks) |

## Done in this track (through PR `#21`)

- Brand-first welcome; modern launch **splash**
- Live connect + TOFU, tree, General/Storage/Network/Console (RFB fit/input/cursor/pop-out/`Ctrl+Alt+Del`)
- Logs/Tasks via `ConnectionsManager.History` + `ShellActionRunner`
- New VM wizard (OS template icons + filter + type labels; primary disk size from Storage step); ISO attach/eject; Snapshots (disk-only tree)
- **Full VM Properties** — General/tags, CPU/memory, boot, HA/startup, home server, GPU, USB
- **Clone / Copy / Migrate / Cross-pool / Move / Delete**
  - Intra-pool live migrate: `VMMigrateAction`
  - Storage / cross-pool: `VMCrossPoolMigrateAction` with per-disk SR + per-VIF maps; empty VIF map for intra-pool
  - Halted Move: migrate_send **Move** dialog when licensed + CBT-clear + eligible hosts; **intra-pool finish → `VMMoveAction`**; else simple Move SR picker
  - CBT / `RestrictCrossPoolMigrate` / `CanFitDisks` / no-op disk-map rejection
- **Import / Export XVA** — sidebar chooser → `ImportVmAction` / `ExportVmAction` (Disconnect remains on tree context menu)
- **New SR wizard** — NFS ISO, SMB/CIFS ISO, NFS VHD, SMB, iSCSI (+ GFS2), HBA/FCoE (+ GFS2)
- HA prompts; start-failure host table (assert_can_boot_here + resume CPU check + session logout)
- VM chrome: power toggles, Force* in context menu, Console ISO, status icons, storage under hosts/pool
- UX: denser infra tree + General rows; copyable orange border on **hover**; Pop Out / Add Server casing

### Cursor draft-review (`#21`) — verified addressed

| Item | Status |
|------|--------|
| Intra-pool halted Move → `VMMoveAction` | Done (`ShellMigrateWizardMode.Move`) |
| Empty-host migrate_send → fall back to `VmMoveWindow` | Done (`CanPreferMigrateSendMove`) |
| Resume CPU incompatibility in start-failure table | Done |
| Session logout after diagnosis | Done |
| `CanFitDisks` on SR pickers | Done |
| CBT / license guards on Move / Cross-pool | Done |
| Move dialog title / hide transfer network for intra-pool Move | Done |
| Reject all-disk no-op maps | Done |

### Key shell layout

```
XcpNgCenter.Rfb/     RfbClient (from VNCStream), IRfbFramebuffer, RfbStream
XcpNgCenter.Shell/
  Services/          Bootstrap, TOFU, vault, builders, HostedConsoleSession, ShellActionRunner, HA prompts, ShellStoragePicker, ShellTemplateIcons
  ViewModels/        MainViewModel (+ Actions), wizards/dialogs, ActionLogRow
  Views/             MainWindow + splash + Import/Export + Properties/Clone/Copy/Migrate/CrossPool/Move/Delete/NewVm/NewSr/…
```

Shell references **`XenModel` + `XenCenterLib` + `XcpNgCenter.Rfb`** (not WinForms `XenAdmin`).

## Next (priority order for following agents)

1. **Soak + merge PR `#21`** — run `drop-shell-linux-x64`; merge when soak is clean and CI is green.
2. Harden further migrate / import-export edge cases if soak finds more.
3. Optional: multi-LUN HBA create in one pass; OVF/OVA appliance wizards.
4. Alerts, performance graphs, HA/AD/DR wizards (later). RDP + plugins stay WinForms-only.

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
