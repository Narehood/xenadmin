# Remote Desktop in the Avalonia preview

Select a connected, running guest VM and open its Console tab. Choose
**Remote Desktop…**, review the guest IP address and port, then choose
**Open Remote Desktop**. Valid guest-reported addresses are suggested when
privacy masking is off. You can type a different IPv4 or unscoped IPv6 address
when guest tools are absent. The default port is 3389.

The guest must enable RDP and be reachable directly from this computer. This is
a guest-network connection; the shell's hypervisor proxy and RFB tunnel do not
carry it. Paused/halted guests, templates, snapshots and host control domains
are unavailable. The client rechecks VM UUID/reference, originating connection,
current selection and running state after the dialog closes.

| Platform | Client | Requirement |
| --- | --- | --- |
| Windows | System `mstsc.exe`, endpoint plus `/prompt` | Windows Remote Desktop Connection installed |
| Linux | System `/usr/bin/remmina`, `--connect rdp://address:port` | Distribution Remmina package with its RDP plugin |

The client displays its own guest login and connection/certificate decisions.
The shell supplies no username, password, domain or hypervisor session secret.
It uses separate process arguments, disables shell execution and does not add
certificate-bypass flags. Client settings still belong to the native client.
Missing clients or process-start errors appear in the shell action status.
Flatpak/Snap client discovery and embedded RDP are outside this first adapter.

Addresses are canonicalized; command fragments, credentials/URLs, ambiguous
IPv4 shorthand, wildcard, loopback and multicast destinations are rejected.
Ports must be 1–65535. IPv6 endpoints use bracketed authorities. Scoped/link-local
IPv6 and DNS names are not currently supported; use a reachable guest address.
Privacy masking suppresses automatic address suggestions. Manually entered
addresses stay in this dialog and are not saved by the shell.

Twenty-seven tests cover endpoint/argument safety, guest-metric filtering,
changed VM identity/power/context and editable review validation. The actual
Avalonia dialog passes seven binding/action checks and is rendered at 100%,
150% and 200% scales by the isolated Windows UI probe:

```powershell
dotnet run --project tools/AdvancedNetworking.UiProbe -c Release -p:RestoreLockedMode=true -- --remote-desktop --evidence-directory artifacts/rdp-dialog-local
```

The probe never launches a native RDP client. Real guest login, certificate
handling, IPv6 reachability, missing Linux plugins and user cancellation need
Windows/Linux desktop acceptance. Native Linux CI checks portable argument and
endpoint behavior; it cannot establish that an external client is installed.

Command contracts: [Microsoft mstsc](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/mstsc),
[Remmina command options](https://remmina.gitlab.io/remminadoc.gitlab.io/remmina_8c_source.html)
and [Remmina system packages](https://remmina.org/how-to-install-remmina/).
