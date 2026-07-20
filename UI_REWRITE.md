# UI rewrite (Avalonia preview)

This track replaces the Windows 7–era WinForms chrome with a new Avalonia shell.
It is **additive**: the supported production client remains `XenAdmin` until the
rewrite reaches feature parity for your environment.

## Status (handoff)

| Item | State |
|------|--------|
| Integration branch | `development` |
| Test build | CI artifact **`drop-shell-win-x64`** from Test Builds on `development` (self-contained; no .NET SDK required) |

**Last soak-tested locally:** live connect to a private pool, infrastructure tree (pool → hosts → VMs), General summary pane, public-IP warning behavior, RFB console input.

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
- Saved servers with optional Windows DPAPI password vault
- CI: self-contained `win-x64` publish uploaded as `drop-shell-win-x64`

### Key shell layout

```
XcpNgCenter.Rfb/     RfbClient (from VNCStream), IRfbFramebuffer, RfbStream
XcpNgCenter.Shell/
  Services/          Bootstrap, TOFU dialogs, vault, tree builders, HostedConsoleSession
  ViewModels/        MainViewModel, InfraTreeNode, ServerNode, …
  Views/MainWindow   Welcome + tree rail + General/Storage/Network/Console tabs
```

Shell references **`XenModel` + `XenCenterLib` + `XcpNgCenter.Rfb`** (not WinForms `XenAdmin`).  
Startup wiring: `ShellBootstrap` → `InvokeHelper` + `IXenAdminConfigProvider` + cert callback.  
Console connect: `DuplicateSession` + `HTTPHelper.CONNECT` → `RfbClient` → `WriteableBitmap`.

## Next (priority order)

1. **Linux desktop soak** — validate Avalonia shell + Drawing.Common paths after Windows preview is solid.
2. **Broader WinForms parity** — wizards/actions beyond the preview tabs (out of scope for soak).

**RDP strategy (decided for preview):** keep RDP on WinForms/`XenAdmin` only. The Avalonia shell focuses on hosted RFB/VNC; an ActiveX-free RDP path is deferred.

## How to try the preview

**Preferred (no SDK install):** download the CI artifact `drop-shell-win-x64` from the
[Test Builds](https://github.com/Narehood/xenadmin/actions/workflows/test-builds.yml?query=branch%3Adevelopment)
run on `development`, unzip, and run `XcpNgCenter.Shell.exe`.

**From source (requires .NET 8 SDK):**

```bash
dotnet publish XcpNgCenter.Shell -c Release -r win-x64 --self-contained true -o artifacts/shell-win-x64
.\artifacts\shell-win-x64\XcpNgCenter.Shell.exe
```

Pins: `%APPDATA%\XCP-ng\XCP-ng Center Shell\known-servers.json`  
Saved servers: `%APPDATA%\XCP-ng\XCP-ng Center Shell\saved-servers.json`

## Non-goals for the preview

- Feature parity with every WinForms wizard
- Reviving `origin/avalonia` as-is
- Dropping WinForms before soak testing the net8 client
- Blocking the rewrite on Linux until Windows Storage/Network/Console slices land
