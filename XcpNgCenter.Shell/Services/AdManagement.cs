using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XenAdmin;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.Services;

public enum AdOperation { Join, Leave, AddSubject, SetRoles, RemoveSubject }
public sealed record AdHostOption(string Reference, string Uuid, string Name, string Address, string AuthType,
    string Domain, bool AdRestricted, bool RbacRestricted, bool Live, bool Busy);
public sealed record AdRoleOption(string Reference, string Uuid, string Name, string Description, bool Assignable, string Subroles = "");
public sealed record AdSubjectOption(string Reference, string Uuid, string Identifier, string Name, bool IsGroup,
    string Roles, string RoleReferences);

/// <summary>Contains only authentication inventory and identity; never directory credentials.</summary>
public sealed class AdSnapshot
{
    public AdSnapshot(string poolReference, string poolUuid, string poolName, string coordinator,
        IReadOnlyList<AdHostOption> hosts, IReadOnlyList<AdSubjectOption> subjects, IReadOnlyList<AdRoleOption> roles,
        bool isLocalSuperuser, IReadOnlyList<string> permissions, bool poolBusy = false)
    {
        PoolReference = poolReference; PoolUuid = poolUuid; PoolName = poolName; Coordinator = coordinator;
        Hosts = Array.AsReadOnly(hosts.OrderBy(h => h.Reference, StringComparer.Ordinal).ToArray());
        Subjects = Array.AsReadOnly(subjects.OrderBy(s => s.Reference, StringComparer.Ordinal).ToArray());
        Roles = Array.AsReadOnly(roles.OrderBy(r => r.Reference, StringComparer.Ordinal).ToArray());
        IsLocalSuperuser = isLocalSuperuser; Permissions = Array.AsReadOnly(permissions.ToArray()); PoolBusy = poolBusy;
    }
    public string PoolReference { get; }
    public string PoolUuid { get; }
    public string PoolName { get; }
    public string Coordinator { get; }
    public IReadOnlyList<AdHostOption> Hosts { get; }
    public IReadOnlyList<AdSubjectOption> Subjects { get; }
    public IReadOnlyList<AdRoleOption> Roles { get; }
    public bool IsLocalSuperuser { get; }
    public IReadOnlyList<string> Permissions { get; }
    public bool PoolBusy { get; }
    public bool HasExternalAuth => Hosts.Any(h => h.AuthType.Length != 0);
    public string Fingerprint => AdManagement.Hash(new { PoolReference, PoolUuid, Coordinator, Hosts, Subjects, Roles, PoolBusy });
    internal string InfrastructureFingerprint => AdManagement.Hash(new { PoolReference, PoolUuid, Coordinator, Hosts, Roles, PoolBusy });
}

public sealed class AdRequest
{
    public AdRequest(AdOperation operation, string domain, string subjectName, string? subjectReference,
        IReadOnlyList<string> roleReferences, bool recoveryConfirmed, string credentialUsername = "")
    {
        Operation = operation; Domain = domain.Trim(); SubjectName = subjectName.Trim(); SubjectReference = subjectReference;
        RoleReferences = Array.AsReadOnly(roleReferences.Order(StringComparer.Ordinal).ToArray()); RecoveryConfirmed = recoveryConfirmed;
        CredentialUsername = operation is AdOperation.Join or AdOperation.Leave ? credentialUsername.Trim() : "";
    }
    public AdOperation Operation { get; }
    public string Domain { get; }
    public string SubjectName { get; }
    public string? SubjectReference { get; }
    public IReadOnlyList<string> RoleReferences { get; }
    public bool RecoveryConfirmed { get; }
    // The account identity and leave behavior belong to the reviewed plan. Passwords never do.
    public string CredentialUsername { get; }
    public bool LeaveMachineAccountCleanup => Operation == AdOperation.Leave && CredentialUsername.Length > 0;
    public string Fingerprint => AdManagement.Hash(new { Operation, Domain, SubjectName, SubjectReference, RoleReferences, RecoveryConfirmed,
        CredentialUsername, LeaveMachineAccountCleanup });
}

public sealed record AdReview(string SnapshotFingerprint, string RequestFingerprint, string? ResolvedIdentifier,
    string Summary, string? Error = null)
{
    public bool CanApply => Error == null;
}

/// <summary>Short-lived transfer to the action worker. It is not part of a draft, review, or saved profile.</summary>
public sealed class AdCredentials(string username, string password) : IDisposable
{
    internal string Username { get; private set; } = username.Trim();
    internal string Password { get; private set; } = password;
    public void Dispose() { Username = ""; Password = ""; }
    public override string ToString() => "[directory credentials]";
}

public static class AdManagement
{
    public const string RecoveryNotice = "Changes may be partial or the last response may have been lost. Do not repeat the operation blindly. Reconnect using local root, inspect authentication on every host and the subject's actual roles, then reopen this editor. No automatic rollback or retry was performed.";
    public const string RecoveryInstructions = "Before changing access, verify local root sign-in on every host, record their management addresses, and keep a root session available. Directory access can be interrupted. Local root is retained by these operations.";

    public static Task<AdSnapshot> LoadAsync(IXenConnection connection, string poolReference, string poolUuid) => Task.Run(() =>
    {
        RequirePool(connection, poolReference, poolUuid);
        var session = connection.DuplicateSession(60000);
        var inventory = AdInventory.Read(session, poolReference);
        if (inventory.Pool.uuid != poolUuid) throw new AdValidationException("The pool identity changed. Reopen access management.");
        var local = session.get_is_local_superuser();
        return inventory.Snapshot(local, local ? [] : Session.get_rbac_permissions(session, session.opaque_ref));
    });

    public static Task<AdReview> ReviewAsync(IXenConnection connection, AdSnapshot snapshot, AdRequest request,
        CancellationToken token = default) => Task.Run(() => Review(connection, connection.DuplicateSession(60000), snapshot, request, token), token);

    internal static AdReview Review(IXenConnection connection, Session session, AdSnapshot snapshot, AdRequest request,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        RequireCurrent(connection, snapshot);
        RequirePermissions(session, request.Operation);
        var current = AdInventory.Read(session, snapshot.PoolReference, token).Snapshot();
        RequireSnapshot(snapshot, current);
        Validate(current, request);
        ProtectCurrentAuthority(session, current, request);
        string? identifier = null;
        string summary;
        if (request.Operation == AdOperation.AddSubject)
        {
            identifier = Auth.get_subject_identifier(session, request.SubjectName);
            if (string.IsNullOrWhiteSpace(identifier)) throw new AdValidationException("The directory did not resolve a stable subject identifier.");
            var information = Auth.get_subject_information_from_identifier(session, identifier);
            if (current.Subjects.Any(s => s.Identifier == identifier)) throw new AdValidationException("This user or group already has a subject entry. Select it to edit its roles.");
            summary = $"Add {(information.GetValueOrDefault("subject-is-group") == "true" ? "group" : "user")} '{information.GetValueOrDefault(Subject.SUBJECT_NAME_KEY, request.SubjectName)}'\nDirectory identifier: {identifier}\nAssign: {RoleNames(current, request)}. The server creates the subject before roles are assigned; an interrupted assignment requires inspecting its default roles. "
                + RevocationSummary(information.GetValueOrDefault("subject-is-group") == "true");
        }
        else summary = request.Operation switch
        {
            AdOperation.Join => $"Join all {current.Hosts.Count} hosts to '{request.Domain}'. The shared join action first clears previous external authentication and then joins the domain. No user or group is added automatically.",
            AdOperation.Leave => $"Leave external authentication on all {current.Hosts.Count} hosts. Directory users will lose access. Stored subject entries may remain. "
                + (request.LeaveMachineAccountCleanup ? "Disable directory machine accounts using the supplied credentials."
                    : "Leave directory machine accounts unchanged; clean them up separately."),
            AdOperation.SetRoles => $"Replace roles for '{current.Subjects.Single(s => s.Reference == request.SubjectReference).Name}' with: {RoleNames(current, request)}. New roles are added before old roles are removed. "
                + RevocationSummary(current.Subjects.Single(s => s.Reference == request.SubjectReference).IsGroup),
            AdOperation.RemoveSubject => $"Log out and remove access for '{current.Subjects.Single(s => s.Reference == request.SubjectReference).Name}'. "
                + RevocationSummary(current.Subjects.Single(s => s.Reference == request.SubjectReference).IsGroup)
                + " Other group grants can still permit this user to sign in.",
            _ => throw new AdValidationException("Select a supported access operation.")
        };
        token.ThrowIfCancellationRequested();
        RequireCurrent(connection, snapshot);
        RequireSnapshot(snapshot, AdInventory.Read(session, snapshot.PoolReference, token).Snapshot());
        return new(snapshot.Fingerprint, request.Fingerprint, identifier, summary + "\n\n" + RecoveryInstructions);
    }

    internal static void RequireSnapshot(AdSnapshot expected, AdSnapshot actual)
    {
        if (expected.Fingerprint != actual.Fingerprint)
            throw new AdValidationException("Pool membership, domain state, subjects, roles or capabilities changed after opening this editor. Reopen and review the current configuration.");
    }
    internal static void RequireCurrent(IXenConnection connection, AdSnapshot snapshot)
    {
        var pool = RequirePool(connection, snapshot.PoolReference, snapshot.PoolUuid);
        RequireSnapshot(snapshot, AdInventory.Cached(pool).Snapshot());
    }
    private static Pool RequirePool(IXenConnection connection, string reference, string uuid)
    {
        var pool = Helpers.GetPoolOfOne(connection);
        if (!connection.IsConnected || pool == null || pool.opaque_ref != reference || pool.uuid != uuid)
            throw new AdValidationException("The pool disconnected or its identity changed. Reconnect and reopen access management.");
        if (pool.Locked) throw new AdValidationException("Another pool action is in progress. Wait and reopen access management.");
        return pool;
    }

    public static string? ValidationError(AdSnapshot snapshot, AdRequest request)
    {
        try { Validate(snapshot, request); return null; }
        catch (InvalidOperationException error) { return error.Message; }
    }
    private static void Validate(AdSnapshot snapshot, AdRequest request)
    {
        if (!Enum.IsDefined(request.Operation)) throw new AdValidationException("Select a supported access operation.");
        if (!request.RecoveryConfirmed) throw new AdValidationException("Verify and confirm the local root recovery path before making changes.");
        if (string.IsNullOrWhiteSpace(snapshot.PoolUuid) || snapshot.Hosts.Count == 0
            || snapshot.Hosts.All(h => h.Reference != snapshot.Coordinator) || snapshot.Hosts.Any(h => string.IsNullOrWhiteSpace(h.Uuid)))
            throw new AdValidationException("Pool or host identity is incomplete. Reconnect before managing access.");
        if (snapshot.PoolBusy || snapshot.Hosts.Any(h => h.Busy)) throw new AdValidationException("Complete current pool/host operations or upgrades before changing access.");
        if (request.Operation == AdOperation.Leave)
        {
            if (!snapshot.HasExternalAuth) throw new AdValidationException("External authentication is already disabled.");
            return; // Recovery is available even with mixed domains or reduced licensing.
        }
        if (snapshot.Hosts.Any(h => !h.Live || h.AdRestricted)) throw new AdValidationException("All hosts must be live and advertise Active Directory support.");
        if (request.Operation == AdOperation.Join)
        {
            if (snapshot.HasExternalAuth) throw new AdValidationException("Leave existing or mixed external authentication before joining a domain.");
            if (Uri.CheckHostName(request.Domain) != UriHostNameType.Dns || !request.Domain.Contains('.'))
                throw new AdValidationException("Enter the domain's full DNS name (for example, example.org).");
            return;
        }
        if (snapshot.Hosts.Any(h => h.AuthType != Auth.AUTH_TYPE_AD) || snapshot.Hosts.Select(h => h.Domain).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            throw new AdValidationException("Every host must be joined to the same Active Directory domain before managing subjects.");
        if (snapshot.Hosts.Any(h => h.RbacRestricted)) throw new AdValidationException("Every host must advertise role-based access control support.");
        if (request.Operation == AdOperation.AddSubject && string.IsNullOrWhiteSpace(request.SubjectName)) throw new AdValidationException("Enter one domain-qualified user or group name.");
        var subject = request.Operation == AdOperation.AddSubject ? null : snapshot.Subjects.SingleOrDefault(s => s.Reference == request.SubjectReference);
        if (request.Operation != AdOperation.AddSubject && (subject == null || string.IsNullOrWhiteSpace(subject.Uuid)))
            throw new AdValidationException("Select a current user or group.");
        if (request.Operation is AdOperation.AddSubject or AdOperation.SetRoles)
        {
            if (request.RoleReferences.Count == 0 || request.RoleReferences.Distinct().Count() != request.RoleReferences.Count
                || request.RoleReferences.Any(reference => !snapshot.Roles.Any(role => role.Reference == reference && role.Assignable && role.Uuid.Length > 0)))
                throw new AdValidationException("Select at least one supported role. Use Remove access to revoke a subject entirely.");
            if (subject != null && subject.RoleReferences.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(reference => !snapshot.Roles.Any(role => role.Reference == reference && role.Assignable)))
                throw new AdValidationException("This subject has an unknown or internal role. Review it with the server administrator before replacing its roles.");
            if (subject != null && string.Join('\n', request.RoleReferences) == subject.RoleReferences)
                throw new AdValidationException("The selected roles are unchanged.");
        }
    }

    internal static RbacMethodList Methods(AdOperation operation)
    {
        var methods = new RbacMethodList("session.get_is_local_superuser", "session.get_rbac_permissions", "pool.get_record",
            "host.get_all_records", "host_metrics.get_all_records", "subject.get_all_records", "role.get_all_records");
        methods.AddRange(operation switch
        {
            AdOperation.Join => ["pool.disable_external_auth", "pool.enable_external_auth"],
            AdOperation.Leave => ["pool.disable_external_auth"],
            AdOperation.AddSubject => ["auth.get_subject_identifier", "auth.get_subject_information_from_identifier", "subject.create", "subject.add_to_roles", "subject.remove_from_roles", "session.logout_subject_identifier", "session.get_all_subject_identifiers", "auth.get_group_membership"],
            AdOperation.SetRoles => ["subject.add_to_roles", "subject.remove_from_roles", "session.logout_subject_identifier", "session.get_all_subject_identifiers", "session.get_subject", "session.get_auth_user_sid", "auth.get_group_membership"],
            AdOperation.RemoveSubject => ["subject.destroy", "session.logout_subject_identifier", "session.get_all_subject_identifiers", "session.get_subject", "session.get_auth_user_sid", "auth.get_group_membership"],
            _ => throw new AdValidationException("Unsupported access operation.")
        });
        return methods;
    }

    internal static void RequirePermissions(Session session, AdOperation operation)
    {
        if (session.get_is_local_superuser()) return;
        if (operation is AdOperation.Join or AdOperation.Leave)
            throw new AdValidationException("Reconnect using local root to join or leave a domain. This preserves an independent administrative recovery session.");
        var permissions = Session.get_rbac_permissions(session, session.opaque_ref);
        if (Methods(operation).Any(method => !permissions.Any(permission => string.Equals(permission, method.Method, StringComparison.OrdinalIgnoreCase)
            || permission.EndsWith('*') && method.Method.StartsWith(permission[..^1], StringComparison.OrdinalIgnoreCase))))
            throw new AdValidationException("This session lacks the permissions required for the complete access operation. Use an authorized pool administrator account.");
    }

    private static void ProtectCurrentAuthority(Session session, AdSnapshot snapshot, AdRequest request)
    {
        if (request.Operation is not (AdOperation.SetRoles or AdOperation.RemoveSubject) || session.get_is_local_superuser()) return;
        var target = snapshot.Subjects.Single(s => s.Reference == request.SubjectReference);
        var self = Session.get_subject(session, session.opaque_ref);
        var sid = session.get_auth_user_sid();
        if (string.IsNullOrWhiteSpace(sid)) throw new AdValidationException("The current directory identity could not be confirmed. Reconnect using local root.");
        if (self?.opaque_ref == target.Reference || target.Identifier == sid || Auth.get_group_membership(session, sid).Contains(target.Identifier, StringComparer.Ordinal))
            throw new AdValidationException("This subject grants access to your current account. Reconnect using local root before changing your own user or group authority.");
    }

    private static string RoleNames(AdSnapshot snapshot, AdRequest request) => string.Join(", ", request.RoleReferences.Select(reference => snapshot.Roles.Single(r => r.Reference == reference).Name));
    private static string RevocationSummary(bool isGroup) => isGroup
        ? "Log out sessions for this group and discovered directory identities whose transitive membership includes it. Concurrent logins or directory changes require separate verification."
        : "Log out existing sessions for this directory identity so a new login uses its current permissions.";
    internal static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}

internal sealed class AdInventory
{
    public required Pool Pool { get; init; }
    public Host[] Hosts { get; init; } = [];
    public Host_metrics[] Metrics { get; init; } = [];
    public Subject[] Subjects { get; init; } = [];
    public Role[] Roles { get; init; } = [];
    public static AdInventory Cached(Pool pool)
    {
        var metrics = new List<Host_metrics>(); pool.Connection.Cache.AddAll(metrics, _ => true);
        return new() { Pool = pool, Hosts = pool.Connection.Cache.Hosts,
            Metrics = metrics.ToArray(), Subjects = pool.Connection.Cache.Subjects, Roles = pool.Connection.Cache.Roles };
    }
    public static AdInventory Read(Session session, string reference, CancellationToken token = default)
    {
        T[] Read<T>(Func<Session, Dictionary<XenRef<T>, T>> read) where T : XenObject<T>
        { token.ThrowIfCancellationRequested(); return read(session).Select(pair => { pair.Value.opaque_ref = pair.Key.opaque_ref; return pair.Value; }).ToArray(); }
        token.ThrowIfCancellationRequested();
        var pool = Pool.get_record(session, reference); pool.opaque_ref = reference;
        return new() { Pool = pool, Hosts = Read(Host.get_all_records), Metrics = Read(Host_metrics.get_all_records),
            Subjects = Read(Subject.get_all_records), Roles = Read(Role.get_all_records) };
    }
    public AdSnapshot Snapshot(bool local = false, IReadOnlyList<string>? permissions = null) => new(Pool.opaque_ref, Pool.uuid,
        string.IsNullOrWhiteSpace(Pool.name_label) ? Hosts.FirstOrDefault(h => h.opaque_ref == Pool.master.opaque_ref)?.Name() ?? Pool.uuid : Pool.name_label,
        Pool.master.opaque_ref,
        Hosts.Select(h => new AdHostOption(h.opaque_ref, h.uuid, h.Name(), h.address, h.external_auth_type, h.external_auth_service_name,
            Host.RestrictAD(h), Host.RestrictRBAC(h), Metrics.Any(m => m.opaque_ref == h.metrics.opaque_ref && m.live), h.current_operations.Count != 0)).ToArray(),
        Subjects.Select(s => new AdSubjectOption(s.opaque_ref, s.uuid, s.subject_identifier, s.DisplayName ?? s.SubjectName ?? s.subject_identifier,
            s.other_config.GetValueOrDefault("subject-is-group") == "true", string.Join(", ", s.roles.Select(r => Roles.SingleOrDefault(role => role.opaque_ref == r.opaque_ref)?.name_label ?? $"Unknown ({r.opaque_ref})").Order(StringComparer.Ordinal)),
            string.Join('\n', s.roles.Select(r => r.opaque_ref).Order(StringComparer.Ordinal)))).ToArray(),
        Roles.Select(r => new AdRoleOption(r.opaque_ref, r.uuid, r.name_label, r.name_description, !r.is_internal && r.subroles.Count > 0,
            string.Join('\n', r.subroles.Select(reference => reference.opaque_ref).Order(StringComparer.Ordinal)))).ToArray(),
        local, permissions ?? [], Pool.current_operations.Count != 0 || Pool.RollingUpgrade() || Pool.is_psr_pending);
}
