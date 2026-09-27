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
   action's preparatory domain leave. For join or credentialed leave, enter the
   directory account name before review. The leave plan states whether directory
   machine accounts will be disabled or left for separate cleanup.
4. Apply the reviewed plan. Domain credentials are needed only for join or
   optional directory machine-account cleanup during leave. Leaving with empty
   credentials disables host external authentication but requires separate
   directory account cleanup.
5. Inspect actual host/subject state and verify a new login with the intended
   account. Keep root access until this succeeds.

Editing the draft invalidates its approval. Review pins the trimmed, nonsecret
directory account name and the leave machine-account cleanup choice. Changing
either requires another review; changing only the password does not alter the
reviewed operation. Passwords never enter the request, review fingerprint or
confirmation text. Both the editor and worker use the trimmed username when
requiring a complete username/password pair.

The worker checks the exact pool,
member identities, domain state, live-host capabilities, subjects, role
definitions and fresh session permissions against both cache and server. It
resolves a newly added subject again and pins the reviewed directory identifier
inside the shared subject action. It checks pool/domain/role definitions again
after subject creation and before granting roles. A changed object, permission
or identity stops execution. The final role check compares existing subjects
with the immutable reviewed identity and roles, so cache events during that
check cannot replace the approved baseline. Newly created subjects use their
captured post-create identity and default roles instead. Separate reads and
writes cannot provide a transaction across competing administrators; coordinate
access changes.

The execution paths use `EnableAdAction`, `DisableAdAction`,
`AddRemoveSubjectsAction` and `AddRemoveRolesAction`. New roles are added before
old roles are removed; existing sessions for the subject are then logged out so
fresh login permissions take effect. For a group, the worker additionally
enumerates active external session identities and checks their transitive group
membership. It logs out the distinct matching identities, including users whose
primary session subject was a different group. This also runs after assigning
roles to a newly created group. Removing access logs out the direct subject
before deleting it, then performs the group-member sweep after successful
deletion. Enumeration, membership lookup or logout failure stops the operation
and reports a partial outcome rather than declaring revocation complete.

The server excludes local-root sessions from enumeration and protects them from
forced logout. Enumeration can include both authenticated user SIDs and primary
group SIDs; the membership check handles both. These are separate server reads
and logout calls: a concurrent login or directory membership change can escape
the sweep, so this is not an atomic guarantee that every affected session was
revoked. Verify logout and fresh-login permissions, and coordinate access changes.
Other directory group grants can still authorize the same user.
Disabling AD does not promise to remove stored subject entries.

The shared join action declares both nested mutation permissions, and the shell
stops if its preparatory leave is unconfirmed. WinForms retains its previous
best-effort preparation behavior. Both clients now clear directory credential
fields from completed/cancelled join/leave actions and discard raw server error
details before action logging, since a directory provider can echo submitted
credentials. The transport logs method names only. Passwords are excluded from
review records, history descriptions and settings; password fields and transient
credential holders are cleared after apply, cancelled confirmation or close.
Managed strings cannot be guaranteed erased from process memory immediately.

## Reconcile partial or unknown outcomes

There is no automatic retry, rollback or durable recovery journal. A lost
response can mean a server mutation succeeded even though the client reports
failure. After a mutation or unknown outcome, the editor blocks another attempt
until it is reopened. An explicit worker validation failure before any mutation
reports that no server changes were attempted and permits editing and reviewing
again; stale-inventory messages can still require reopening to load fresh state.
The preparatory leave during join counts as a mutation even if joining never starts.

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
directory SID, concurrent cache/server role changes during final revalidation,
and role/domain changes between creation and assignment.
`AdEditorTests` cover draft invalidation, restricted sessions, confirmation,
credential release, reviewed leave cleanup/account choices, trimmed credential
pairs, password exclusion, cancelled review, busy/close guards and the distinction
between no-write validation and partial failure. `AdSessionRevocationTests`
exercise direct/transitive membership, unrelated identities, deduplication,
required permissions and interrupted logout sweeps through real loopback RPC.
The actual-window probe checks production Avalonia bindings,
layout and interaction without touching a pool.

These synthetic checks do not verify an actual domain join, domain outage,
restricted directory login or mixed-host recovery. Before deployment, use a
disposable multi-host pool and directory to exercise every operation, DNS/time
failure, nested groups, expired credentials, member outages and an interrupted
role change. Verify independent root recovery on every member. Those live gates
remain pending in [platform acceptance](platform-acceptance.md).

API contracts: [subject operations and minimum roles](https://xapi-project.github.io/xen-api/classes/subject.html)
and [directory identity and recursive group lookup](https://xapi-project.github.io/xen-api/classes/auth.html).
The server's direct logout matching and local-root protection are visible in
[XAPI's session implementation](https://github.com/xapi-project/xen-api/blob/master/ocaml/xapi/xapi_session.ml).
