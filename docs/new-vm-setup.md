# New VM setup in the Avalonia preview

The New VM wizard includes installation media and firmware between placement
and CPU/memory configuration. It passes these choices to the shared
`CreateVMAction` before starting the VM:

- Optional ISO image from a pool ISO repository, or the placement host's
  physical DVD drive. Only media with an attached storage connection to that
  host appears. Choose **None** for an empty drive; a custom template without
  a drive keeps no drive. Local workstation ISO upload
  is not part of this picker.
- BIOS, UEFI, or UEFI with Secure Boot, subject to the template's firmware
  recommendations and the pool's advertised capabilities. Template defaults
  are retained; installed custom templates cannot switch firmware.
- Virtual TPM 2.0 for supported UEFI guests. A TPM supplied by a template is
  retained without requesting a duplicate. BIOS disables optional vTPM.
- Startup and maximum vCPU counts, cores per socket, and the calculated socket
  count. Maximum vCPUs must be divisible by cores per socket; template limits,
  the host's cores per socket, and the shared socket-count limit are checked.

The confirmation page includes these settings. Back navigation preserves them;
changing placement removes media that the new host cannot access. Creation
rechecks media availability and the selected security capabilities.
Creation runs in the background and reports errors in Logs. A rejected vTPM
request fails creation instead of reporting success without the requested TPM;
the newly created VM is retained halted for inspection if attachment fails.

These additions cover the missing media, firmware, security, and CPU-topology
settings requested from Xen Orchestra's creation flow. They do **not** establish
complete Xen Orchestra feature parity. The preview still has a single disk and
network adapter in this wizard; network-install URLs/PXE selection, multiple
disks/NICs, cloud-init, GPU/PCI passthrough and advanced CPU policies remain
separate work. The WinForms wizard retains its existing broader setup flow.

Reference: [XCP-ng advanced VM settings](https://docs.xcp-ng.org/vms/advanced/)
and [guest UEFI Secure Boot](https://docs.xcp-ng.org/guides/guest-UEFI-Secure-Boot/).
Enabling Secure Boot requests enforcement; actual enforcement also depends on
the host's guest UEFI certificate provisioning. Verify it in the guest after
the first boot. This change does not install or change pool certificates.
An empty `Pool.uefi_certificates` field does not establish that certificates
are missing: current XCP-ng `varstored` packages can supply bundled defaults.
Older hosts still require pool certificate configuration.

For an isolated Windows UI check, run:

```powershell
dotnet run --project tools/AdvancedNetworking.UiProbe -c Release -p:RestoreLockedMode=true -- --new-vm --evidence-directory artifacts/new-vm-ui
```

This opens the production wizard offscreen using synthetic inventory, exercises
its bound controls and Back navigation. It renders the media/firmware/TPM page
at normal and minimum sizes and the CPU page at minimum size. It loads no saved
profile and starts no pool actions.
