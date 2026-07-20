# UI rewrite (Avalonia preview)

This track replaces the Windows 7–era WinForms chrome with a new Avalonia shell.
It is **additive**: the supported production client remains `XenAdmin` until the
rewrite reaches feature parity for your environment.

## Status (handoff)

| Item | State |
|------|--------|
| Integration branch | `development` |
| Test build | CI artifacts **`drop-shell-win-x64`** and **`drop-shell-linux-x64`** from Test Builds on `development` |

**Last soak-tested locally:** live connect to a private pool, infrastructure tree (pool → hosts → VMs), General summary pane, public-IP warning behavior, RFB console input.

**Windows preview scope:** complete. **Phase 1 parity (in progress / shipping):** Logs/Tasks, VM power + New VM + basic edit, ISO attach, New SR (ISO/iSCSI/NFS), console pop-out.

## Branch basis

Lives on `development` with the modernization stack (CI, security, `HttpClient`, Import Wizard
fixes, plugin DoEvents removal, and `XenAdmin` → `net8.0-windows`).

## Projects

| Project | Role |
|---------|------|
| `XenAdmin` | Current supported WinForms client (`net8.0-windows`) |
| `XcpNgCenter.Shell` | Avalonia preview shell (`net8.0`, Windows-first; Linux later) |
| `XcpNgCenter.Rfb` | WinForms-free RFB client core (`net8.0`, buffer callbacks) |

## Done in this track

- Brand-first welcome composition (XCP-ng mark, product name, Connect CTA)
- Design tokens (graphite + brand orange, Outfit type — not Inter / purple AI defaults)
- Live connect through `XenModel` (`XenConnection`): username/password, disconnect
- TOFU TLS pin store with Avalonia trust dialogs (first-seen + changed; AppData JSON)
- Public-IP warning + acknowledgement via `HostnameAddressClassifier`
  - Complete IPv4 dotted-quad only — incomplete typing (`1`, `10`, `192.168`) does not warn
- Infrastructure tree: pool → hosts → VMs (`InfrastructureTreeBuilder`, Avalonia `TreeView`)
  - Live refresh on `CachePopulated` / `XenObjectsUpdated`
- General summary for selected pool / host / VM (`GeneralSummaryBuilder`)
- Storage tab (read-only): pool/host SR list + VM disks (`StorageSummaryBuilder`)
- Network tab (read-only): networks / management PIFs / VM VIFs (`NetworkSummaryBuilder`)
- Richer General fields (UUID, uptime, OS, tools, IPs, tags, HA, IQN, …)
- Tab ScrollViewer padding so right-aligned values clear the scrollbar
- Console tab: hosted RFB viewer (fit-to-pane), pointer/keyboard, remote cursor, richer keysyms
- Hover/focus **Copy** on General / Storage / Network property rows
- Saved servers with optional Windows DPAPI password vault (forget-password; vault disabled on non-Windows)
- Clear trusted-certificate pins from the welcome surface
- Shutdown disconnects live sessions and disposes the hosted RFB console
- Console input-capture hint when the RFB viewer is focused
- CI: self-contained `win-x64` and `linux-x64` publish artifacts
- **Phase 1 parity:** `ShellActionRunner` + Logs/Tasks tab (`ConnectionsManager.History`)
- VM action bar: start / shutdown / reboot / suspend / resume / force variants (wraps on narrow widths)
- Avalonia New VM wizard (`CreateVMAction`) and basic Edit (rename / CPU / memory — full Properties later)
- ISO attach/eject (`ChangeVMISOAction` / `CreateCdDriveAction`)
- New SR wizard: NFS ISO, NFS VHD, iSCSI (`SrCreateAction`) + SR refresh
- Console pop-out window with reattach + Ctrl+Alt+Del
- Scrollable infrastructure detail card; Console viewer fixed **520px** tall (scroll the card on small windows)
- **Snapshots tab (Phase 2 start):** list, take disk snapshot, revert, delete

### Key shell layout

```
XcpNgCenter.Rfb/     RfbClient (from VNCStream), IRfbFramebuffer, RfbStream
XcpNgCenter.Shell/
  Services/          Bootstrap, TOFU, vault, builders, HostedConsoleSession, ShellActionRunner
  ViewModels/        MainViewModel (+ Actions), wizards/dialogs, ActionLogRow
  Views/             MainWindow, NewVm/NewSr/Iso/Edit/ConsolePopOut windows
```

Shell references **`XenModel` + `XenCenterLib` + `XcpNgCenter.Rfb`** (not WinForms `XenAdmin`).  
Startup wiring: `ShellBootstrap` → `InvokeHelper` + `IXenAdminConfigProvider` + cert callback.  
Console connect: `DuplicateSession` + `HTTPHelper.CONNECT` → `RfbClient` → `WriteableBitmap`.

## Next (priority order)

1. **Merge PR #20** (`cursor/shell-phase1-parity-abb3`) if not already on `development`.
2. **Full VM Properties dialog** (boot, HA, home server, GPU/USB, description, tags) — Edit is rename/CPU/memory only today.
3. **Clone / delete VM**, then migrate/copy.
4. **More SR types** + richer iSCSI probe UI.
5. **Linux desktop soak** — run `drop-shell-linux-x64`.
6. Alerts, performance graphs, HA/AD/DR (later). RDP + plugins stay WinForms-only.

**RDP strategy (decided for preview):** keep RDP on WinForms/`XenAdmin` only. The Avalonia shell focuses on hosted RFB/VNC; an ActiveX-free RDP path is deferred.

## How to try the preview

**Preferred (no SDK install):** download the CI artifact `drop-shell-win-x64` (Windows) or `drop-shell-linux-x64` (Linux) from the
[Test Builds](https://github.com/Narehood/xenadmin/actions/workflows/test-builds.yml?query=branch%3Adevelopment)
run on `development`, unzip, and run `XcpNgCenter.Shell` / `XcpNgCenter.Shell.exe`.

**From source (requires .NET 8 SDK):**

```bash
dotnet publish XcpNgCenter.Shell -c Release -r win-x64 --self-contained true -o artifacts/shell-win-x64
.\artifacts\shell-win-x64\XcpNgCenter.Shell.exe
```

```bash
dotnet publish XcpNgCenter.Shell -c Release -r linux-x64 --self-contained true -o artifacts/shell-linux-x64
./artifacts/shell-linux-x64/XcpNgCenter.Shell
```

Pins: `%APPDATA%\XCP-ng\XCP-ng Center Shell\known-servers.json` (Windows) / `~/.config/XCP-ng/XCP-ng Center Shell/` (Linux XDG)  
Saved servers: same folder, `saved-servers.json`

## Non-goals for the preview

- Feature parity with every WinForms wizard
- Reviving `origin/avalonia` as-is
- Dropping WinForms before soak testing the net8 client
- Blocking the rewrite on Linux until Windows Storage/Network/Console slices land
