# PR #62 review fixes

Reviewed Cursor's changes-requested review and both inline threads against
`6f586ecad7e43f3689f35b9c6a7ae115c485324e`. Both production findings are valid.
Implementation: `2523d9f846dab68292e3384d16e43686e6d1564f`, in
[PR #62](https://github.com/Narehood/xenadmin/pull/62).

| Comment | Verification and correction |
| --- | --- |
| [Inventory failure drops later connections](https://github.com/Narehood/xenadmin/pull/62#discussion_r4162115370) | A throwing first rebuild abandoned the rest of the drained batch. Each failure is now caught and logged; the remaining connections continue. Failed work remains pending until a later notification schedules a turn. Newer notifications take precedence, and persistent failures do not post automatic retry callbacks. |
| [Mapped RDP spelling and unreviewed normalization](https://github.com/Narehood/xenadmin/pull/62#discussion_r4162115376) | On the pinned .NET 10 runtime, the proposed padded/hex IPv4 examples do not parse, but alternate mapped hex, expanded and upper-case spellings do parse and bypassed the canonical check. The check now covers mapped IPv6 before conversion. Canonical `::ffff:a.b.c.d` remains supported. The dialog displays the exact client authority, rewrites accepted address/port normalization into the fields and requires another confirmation before returning an endpoint. |

The initial regression revision reproduces **10 failures out of 45 focused
cases** against the previous production code. With the fixes and destination
notification/edit coverage, all **47 focused cases** pass. Three new scheduler
cases cover failed-first/later-connection progress and recovery, persistent
failure without starvation or a retry loop, and newer server notifications
during a failure. Eleven new RDP cases cover mapped variants, normalization
confirmation, live destination preview and editing an already normalized target.

Full integrated local acceptance passes all 24 automated checks: locked restore,
Release/Debug solution builds, **977 shell tests per configuration**, **73 shared
tests on each of net481/net10.0**, 333 WinForms lifecycle/designer and 23 archive
checks per configuration, 32,138 resources in 289 sets, proxy authentication,
all six UI modes, and Windows self-contained package validation/startup.
The actual RDP dialog passes **12 checks**, including visible exact authority,
normalization without closing, field/preview updates after edits and final
confirmation; screenshots render at 100%, 150% and 200% scales.

Portable lockfiles are unchanged. Local WinForms builds reuse trusted RDP
interop; hosted Windows generates it. Ignored evidence is under
`artifacts/pr62-review/` and `artifacts/pr62-review-acceptance-20261001/`.
No external RDP client, saved profile or live pool is used by the probes.
Current-head Windows/Linux and CodeQL checks must pass before merge. CodeRabbit
review remains unavailable under its file/capacity limits; its success status
is not an approval. Physical desktop, Visual Studio, updater/UAC, actual RDP
guest login and live-pool acceptance remain pending. Cursor's review sign-off
belongs to the reviewer; addressing and resolving threads does not dismiss the
changes-requested review.
