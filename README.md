# XCP-ng Center

Windows and Linux management client for [XCP-ng](https://xcp-ng.org) environments — manage hosts, pools, storage, networks, and virtual machines.

The published app is **XCP-ng Center Shell** (codename Awa), an Avalonia client for Windows and Linux. The classic WinForms client, `XenAdmin`, remains in this repository for Windows. The official graphical client for XCP-ng is [Xen Orchestra](https://xen-orchestra.com). XCP-ng Center is maintained by community members and hosted by the XCP-ng project.

![XCP-ng Center Shell](branding-xcp-ng/Images/XCP-ng_Center_Screenshot.png)

## Get the app

Download the latest stable release from [GitHub Releases](https://github.com/Narehood/xenadmin/releases/latest). Tags look like `v2026.9.22.2`. Each release has two portable archives:

| Platform | Archive |
|----------|---------|
| Windows x64 | `XcpNgCenter.Shell-win-x64-<version>.zip` |
| Linux x64 | `XcpNgCenter.Shell-linux-x64-<version>.tar.gz` |

There is no setup wizard. The archive includes the .NET runtime. Extract the whole archive into a folder you can write to, and run it from there. Leave the shell files at the root of that folder, next to `INSTALL.TXT`.

In-app updates replace files in that folder once the installed shell includes the current updater. A shell from before that updater still runs its own update code, so extract a current release into the same folder once. Later updates can replace files in place. A folder under Program Files works, and each update will ask for administrator approval.

The in-app updater defaults to regular releases. Enable **Settings → About → Receive beta updates** to include manually published beta prereleases. Drafts are always ignored, and an update is offered only when its `year.month.day.revision` exceeds the installed version. See [beta channels and manual releases](docs/beta-updates.md).

### Windows

1. Create a folder you own, for example `%USERPROFILE%\Apps\XCP-ng Center Shell`.
2. Extract the entire zip into that folder. The files belong at the root of the folder, alongside `INSTALL.TXT`.
3. Run `XcpNgCenter.Shell.exe`.

Keep the extracted folder intact. If antivirus quarantines the unsigned build, allow the install folder and `%LOCALAPPDATA%\XCP-ng\XCP-ng Center Shell\Updates`.

### Linux

A normal desktop session already has the libraries Awa needs. On a minimal system, install the [Avalonia desktop libraries](https://docs.avaloniaui.net/docs/deployment/linux) `libx11-6`, `libice6`, `libsm6`, and `libfontconfig1` (Debian/Ubuntu names; other distributions ship the same libraries under their own package names).

1. Extract into a directory you own:

   ```bash
   mkdir -p ~/Apps/xcp-ng-center-shell
   tar -xzf XcpNgCenter.Shell-linux-x64-*.tar.gz -C ~/Apps/xcp-ng-center-shell
   chmod +x ~/Apps/xcp-ng-center-shell/XcpNgCenter.Shell
   ~/Apps/xcp-ng-center-shell/XcpNgCenter.Shell
   ```

2. Leave the extracted files together. Updates keep the executable bit on `XcpNgCenter.Shell`.

### Where your data lives

Settings, saved servers, certificate pins, and the optional main password stay in your user profile, outside the application folder.

| | Settings | Update downloads |
|--|----------|------------------|
| Windows | `%APPDATA%\XCP-ng\XCP-ng Center Shell\` | `%LOCALAPPDATA%\XCP-ng\XCP-ng Center Shell\Updates` |
| Linux | `$XDG_CONFIG_HOME/XCP-ng/XCP-ng Center Shell/` or `~/.config/XCP-ng/XCP-ng Center Shell/` | `$XDG_CACHE_HOME/XCP-ng/XCP-ng Center Shell/Updates` or `~/.cache/XCP-ng/XCP-ng Center Shell/Updates` |

If the shell fails before the window opens, check `startup-crash.log`. On Windows it is in `%LOCALAPPDATA%\XCP-ng\XCP-ng Center Shell\`. On Linux it is in `$XDG_DATA_HOME/XCP-ng/XCP-ng Center Shell/` when that variable is set, and `~/.local/share/XCP-ng/XCP-ng Center Shell/` otherwise.

## What’s included

Awa connects to pools and hosts, with a prompt the first time a management certificate is seen and again if that certificate changes. From there you can work with VMs, storage, networks (including VLANs and VM interfaces), the RFB console, alerts, performance graphs, and logs.

Day-to-day operations in the shell include power actions, snapshots, clone, copy, migrate, move, delete, new VM and storage, and XVA/OVF import and export. **Paste text…** on a console types a reviewed clipboard draft as keystrokes. Settings cover connection and proxy options, Dark/Light/System appearance with a custom accent, security prompts, confirmations, and privacy masking. Saved passwords can be protected by Windows DPAPI, a per-user AES key file (`device.key`) in the settings directory, or an optional main password. On Linux that key file is limited to user read and write.

The classic WinForms client remains the supported Windows client and includes RDP, HA, Active Directory, and disaster recovery. The shell now includes NIC bonding, host IP configuration, and SR-IOV provisioning; see the [platform acceptance checklist](docs/platform-acceptance.md) before using these disruptive operations on a live pool. CI publishes that client as the `drop-release` and `drop-debug` artifacts on the Test Builds workflow. Shell CI artifacts are `drop-shell-win-x64` and `drop-shell-linux-x64`.

Shell and WinForms settings are separate. Installing Awa does not import an older WinForms profile.

## Building from source

Install the .NET 10 SDK selected by `global.json` (10.0.401 or a later patch in the same feature band). Shared libraries target `net481` and `net10.0`. WinForms is `net10.0-windows`. The shell is `net10.0`. Integration happens on the `development` branch.

`XenAdmin.sln` includes the WinForms app, so build it on Windows:

```bash
dotnet build XenAdmin.sln -c Release -p:BuildRevision=1
dotnet test XcpNgCenter.Shell.Tests/XcpNgCenter.Shell.Tests.csproj -c Release
dotnet test XenCenterLib.Tests/XenCenterLib.Tests.csproj -c Release
```

On Linux, build and test the shell and the shared library without the WinForms app. The shared tests use `net10.0` there; `net481` is the Windows target.

```bash
dotnet test XcpNgCenter.Shell.Tests/XcpNgCenter.Shell.Tests.csproj -c Release
dotnet test XenCenterLib.Tests/XenCenterLib.Tests.csproj -c Release -f net10.0
```

To produce the same portable layout as a release:

```bash
dotnet publish XcpNgCenter.Shell -c Release -r win-x64 --self-contained true -p:BuildRevision=1 -o artifacts/shell-win-x64
dotnet publish XcpNgCenter.Shell -c Release -r linux-x64 --self-contained true -p:BuildRevision=1 -o artifacts/shell-linux-x64
```

Contributor notes: [`CONTRIBUTING.md`](./CONTRIBUTING.md), [`UI_REWRITE.md`](./UI_REWRITE.md), and [`MODERNIZATION.md`](./MODERNIZATION.md). The [Building wiki](https://github.com/xcp-ng/xenadmin/wiki/Building) covers the full WinForms toolchain.

## Reporting bugs

Use the issue tracker. For the shell, attach `startup-crash.log` from the log directory above when the window never opens. Leave the settings directory out of the report: it holds saved servers, certificate pins, and password material. If a settings file is needed, send that one file after removing passwords, certificate data, and other credentials. For the WinForms client, attach `XCP-ng Center.log` from `%APPDATA%\XCP-ng\XCP-ng Center\logs\` when you have it.

## Contributions

Fork on GitHub and open a pull request against **`development`**. Discussion also happens on the [XCP-ng forum](https://xcp-ng.org/forum).

## License

BSD 2-Clause. See [LICENSE](LICENSE).

## Maintainers

See [MAINTAINERS.md](./MAINTAINERS.md) and [CREDITS.md](./CREDITS.md).
