# UI rewrite (Avalonia preview)

This track replaces the Windows 7–era WinForms chrome with a new Avalonia shell.
It is **additive**: the supported production client remains `XenAdmin` until the
rewrite reaches feature parity for your environment.

## Status (handoff)

| Item | State |
|------|--------|
| Integration branch | `development` (PRs [#6](https://github.com/Narehood/xenadmin/pull/6) and [#7](https://github.com/Narehood/xenadmin/pull/7) merged) |
| Test build | CI artifact **`drop-shell-win-x64`** from Test Builds on `development` (self-contained; no .NET SDK required) |

**Last soak-tested locally:** live connect to a private pool, infrastructure tree (pool → hosts → VMs), General summary pane, public-IP warning behavior.

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
- Preview TOFU TLS pin store (`Services/TofuCertificate*`, AppData JSON; silent pin/re-pin, no cert dialog yet)
- Public-IP warning + acknowledgement via `HostnameAddressClassifier`
  - Complete IPv4 dotted-quad only — incomplete typing (`1`, `10`, `192.168`) does not warn
- Infrastructure tree: pool → hosts → VMs (`InfrastructureTreeBuilder`, Avalonia `TreeView`)
  - Live refresh on `CachePopulated` / `XenObjectsUpdated`
- General summary for selected pool / host / VM (`GeneralSummaryBuilder`)
- Storage tab (read-only): pool/host SR list + VM disks (`StorageSummaryBuilder`)
- Network tab (read-only): networks / management PIFs / VM VIFs (`NetworkSummaryBuilder`)
- Richer General fields (UUID, uptime, OS, tools, IPs, tags, HA, IQN, …)
- Tab ScrollViewer padding so right-aligned values clear the scrollbar
- Console tab scaffold: RFB/VT100/RDP endpoints from XenModel, copy location
- Hosted RFB viewer (`XcpNgCenter.Rfb` + Avalonia fit-to-pane render) with pointer/keyboard input
- Remote cursor rendering + layout-aware keysyms (`KeySymbol` + special-key table)
- General tab hover/focus **Copy** affordance on property values
- TOFU trust dialogs for first-seen and changed certificates (no silent re-pin)
- Saved server list (address + username; passwords not stored)
- CI: self-contained `win-x64` publish uploaded as `drop-shell-win-x64`

### Key shell layout

```
XcpNgCenter.Rfb/     RfbClient (from VNCStream), IRfbFramebuffer, RfbStream
XcpNgCenter.Shell/
  Services/          Bootstrap, TOFU, tree builders, HostedConsoleSession, AvaloniaRfbFramebuffer
  ViewModels/        MainViewModel, InfraTreeNode, ServerNode, …
  Views/MainWindow   Welcome + tree rail + General/Storage/Network/Console tabs
```

Shell references **`XenModel` + `XenCenterLib` + `XcpNgCenter.Rfb`** (not WinForms `XenAdmin`).  
Startup wiring: `ShellBootstrap` → `InvokeHelper` + `IXenAdminConfigProvider` + cert callback.  
Console connect: `DuplicateSession` + `HTTPHelper.CONNECT` → `RfbClient` → `WriteableBitmap`.

## Next (priority order)

1. **Credentials policy** — optional secure password vault (today: saved host + username only; TOFU pins separate).
2. **Copy affordance on Storage/Network sections** — extend General-style hover Copy to other property lists.
3. **RDP strategy** — decide ActiveX-free path or keep RDP WinForms-only.
4. **Linux desktop soak** — after Windows preview is solid (`net8.0` already; validate Drawing.Common paths).

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

## Non-goals for the preview

- Feature parity with every WinForms wizard
- Reviving `origin/avalonia` as-is
- Dropping WinForms before soak testing the net8 client
- Blocking the rewrite on Linux until Windows Storage/Network/Console slices land
