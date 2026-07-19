# UI rewrite (Avalonia preview)

This track replaces the Windows 7–era WinForms chrome with a new Avalonia shell.
It is **additive**: the supported production client remains `XenAdmin` until the
rewrite reaches feature parity for your environment.

## Status (handoff)

| Item | State |
|------|--------|
| Branch | `cursor/ui-overhaul-d7bd` |
| PR | https://github.com/Narehood/xenadmin/pull/7 (draft; base `development`) |
| Stacked on | PR #6 `cursor/plugins-net8-windows-d7bd` (net8 WinForms + plugin DoEvents) — merge #6 first when ready, then rebase #7 |
| Test build | CI artifact **`drop-shell-win-x64`** (self-contained; no .NET SDK required) |

**Last soak-tested locally:** live connect to a private pool, infrastructure tree (pool → hosts → VMs), General summary pane, public-IP warning behavior.

## Branch basis

Built on top of the modernization stack (CI, security, `HttpClient`, Import Wizard
fixes, plugin DoEvents removal, and `XenAdmin` → `net8.0-windows`).

## Projects

| Project | Role |
|---------|------|
| `XenAdmin` | Current supported WinForms client (`net8.0-windows`) |
| `XcpNgCenter.Shell` | Avalonia preview shell (`net8.0`, Windows-first; Linux later) |

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
- CI: self-contained `win-x64` publish uploaded as `drop-shell-win-x64`

### Key shell layout

```
XcpNgCenter.Shell/
  Services/          Bootstrap, TOFU, config stub, tree + General builders
  ViewModels/        MainViewModel, InfraTreeNode, ServerNode, …
  Views/MainWindow   Welcome + tree rail + General detail pane
```

Shell references **`XenModel` + `XenCenterLib` only** (not WinForms `XenAdmin`).  
Startup wiring: `ShellBootstrap` → `InvokeHelper` + `IXenAdminConfigProvider` + cert callback.

## Next (priority order)

1. **Storage tab** — SRs / VDIs for the selected pool or host (read-only summary first).
2. **Network tab** — networks / PIFs / VIFs summary for selected object.
3. **Richer General** — match more WinForms General fields; multi-select / empty states.
4. **Console last** — VNC first; RDP strategy TBD on Avalonia (do not block Storage/Network on Console).
5. **TOFU UX** — cert changed / first-seen dialogs (replace silent re-pin for production readiness).
6. **Persistence** — saved server list / credentials policy (today: session-only + TOFU pins).
7. **Linux desktop soak** — after Windows preview is solid (`net8.0` already; validate Drawing.Common paths).

## How to try the preview

**Preferred (no SDK install):** download the CI artifact `drop-shell-win-x64` from the
[Test Builds](https://github.com/Narehood/xenadmin/actions/workflows/test-builds.yml?query=branch%3Acursor%2Fui-overhaul-d7bd)
run for this branch, unzip, and run `XcpNgCenter.Shell.exe`.

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
