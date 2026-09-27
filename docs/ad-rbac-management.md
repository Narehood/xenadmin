# Directory access and roles

The Avalonia preview exposes **Directory access** on the General tab and context
menu for connected pools and hosts. The editor reads current host domain state,
users/groups and server-defined roles before showing a draft. One reviewed
operation is applied at a time: join a domain, leave a domain, add a user/group,
replace a subject's roles, or remove its access. Reopen the editor after applying
to inspect the resulting server state.

Join and leave require a connection authenticated as local root. Subject and
role management also work for an authorized directory pool administrator.
Restricted sessions can inspect entries, but review/apply require every API
permission needed by the selected operation. Root itself is not an editable
directory subject. The editor rejects changes to the directory user's own
subject and to any recursively containing group; reconnect as local root for
those operations. These guards preserve an independent recovery route without
preventing administrators from managing other users.

## Review and execution

1. Verify local root sign-in on every member, record management addresses, and
   keep a root session available. Confirm this recovery preparation in the editor.
2. Select the operation. Join requires the domain's full DNS name. Add accepts
   one domain-qualified user or group and explicitly selected roles. To edit or
   remove an existing entry, select it in the subject list. Unknown/internal
   roles require server-administrator investigation before replacement.
3. Review. Subject lookup resolves a name to its stable directory identifier;
   compare the displayed identity and grants. Check each host's DNS, time and
   directory connectivity before joining. Join's plan explains the shared
   action's preparatory domain leave.
4. Apply the reviewed plan. Domain credentials are needed only for join or
   optional directory machine-account cleanup during leave. Leaving with empty
   credentials disables host external authentication but requires separate
   directory account cleanup.
5. Inspect actual host/subject state and verify a new login with the intended
   account. Keep root access until this succeeds.

Editing the draft invalidates its approval. The worker checks the exact pool,
member identities, domain state, live-host capabilities, subjects, role
definitions and fresh session permissions against both cache and server. It
resolves a newly added subject again and pins the reviewed directory identifier
inside the shared subject action. It checks pool/domain/role definitions again
after subject creation and before granting roles. A changed object, permission
or identity stops execution. Separate reads and writes cannot provide a
transaction across competing administrators; coordinate access changes.

The execution paths use `EnableAdAction`, `DisableAdAction`,
`AddRemoveSubjectsAction` and `AddRemoveRolesAction`. New roles are added before
old roles are removed; existing sessions for the subject are then logged out so
fresh login permissions take effect. Removing access logs out the subject before
deleting it. Other directory group grants can still authorize the same user.
Disabling AD does not promise to remove stored subject entries.

The shared join action declares both nested mutation permissions, and the shell
stops if its preparatory leave is unconfirmed. WinForms retains its previous
best-effort preparation behavior. Both clients now clear directory credential
fields from completed/cancelled join/leave actions and discard raw server error
details before action logging, since a directory provider can echo submitted
credentials. The transport logs method names only. Credentials are excluded from
review records, history descriptions and settings; password fields and transient
credential holders are cleared after apply, cancelled confirmation or close.
Managed strings cannot be guaranteed erased from process memory immediately.

## Reconcile partial or unknown outcomes

There is no automatic retry, rollback or durable recovery journal. A lost
response can mean a server mutation succeeded even though the client reports
failure. The editor blocks another attempt until it is reopened.

| Interrupted step | Inspect using local root before deciding what to do |
| --- | --- |
| Preparatory leave or domain join | Inspect external authentication type/domain on every member; verify which computer accounts exist in the directory. Repair mixed state before a new join. |
| Domain leave | Verify each member's local-root access and remaining domain state. Clean up directory computer accounts separately if credentialed cleanup was omitted or unconfirmed. |
| Subject creation | Find the exact reviewed SID, not just its display name. A created subject may still have server-default roles. Confirm those grants before changing roles or removing the entry. |
| Adding/removing roles | Read the complete current role list. Added roles may coexist with old roles when a later removal failed. Compare actual grants with the reviewed plan. |
| Session revocation | Role values may be correct while an old session remains authorized. Confirm logout/re-login behavior before declaring access revoked. |
| Subject removal | Sessions may have been logged out before deletion failed. Check whether the subject still exists and whether other group membership grants access. |

An unavailable host can make leave partial. The recovery operation remains
available despite reduced AD/RBAC licensing or mixed external-auth state;
the server remains responsible for individual-host errors. Review/apply are
blocked during a pool upgrade, secret rotation or current host/pool operation.

## Validation and remaining acceptance

`AdManagementTests` exercise the real shared actions through a loopback JSON-RPC
server: successful join/leave/create/grant/remove, stale cache/server identity,
fresh nested permissions, root recovery gates, recursive own-group protection,
partial step failures, credential echo redaction, cancellation cleanup, changed
directory SID and role/domain changes between creation and assignment.
`AdEditorTests` cover draft invalidation, restricted sessions, confirmation,
credential release, cancelled review, busy/close guards and mandatory reopen
after failure. The actual-window probe checks production Avalonia bindings,
layout and interaction without touching a pool.

These synthetic checks do not verify an actual domain join, domain outage,
restricted directory login or mixed-host recovery. Before deployment, use a
disposable multi-host pool and directory to exercise every operation, DNS/time
failure, nested groups, expired credentials, member outages and an interrupted
role change. Verify independent root recovery on every member. Those live gates
remain pending in [platform acceptance](platform-acceptance.md).

API contracts: [subject operations and minimum roles](https://xapi-project.github.io/xen-api/classes/subject.html)
and [directory identity and recursive group lookup](https://xapi-project.github.io/xen-api/classes/auth.html).
