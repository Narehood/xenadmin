using XenAdmin.Actions;
using XenAdmin.Network;
using XenAPI;

namespace XcpNgCenter.Shell.Services;

/// <summary>Runs one reviewed operation, stops on failure, and never repeats an unconfirmed mutation.</summary>
public sealed class AdAction : AsyncAction
{
    private readonly AdSnapshot _snapshot;
    private readonly AdRequest _request;
    private readonly AdReview _review;
    private AdCredentials? _credentials;

    public AdAction(IXenConnection connection, AdSnapshot snapshot, AdRequest request, AdReview review, AdCredentials credentials)
        : base(connection, "Manage directory access")
    {
        _snapshot = snapshot; _request = request; _review = review; _credentials = credentials;
        ApiMethodsToRoleCheck.AddRange(AdManagement.Methods(request.Operation));
    }

    protected override void Run()
    {
        try
        {
            if (!_review.CanApply || _review.SnapshotFingerprint != _snapshot.Fingerprint || _review.RequestFingerprint != _request.Fingerprint)
                throw new InvalidOperationException("Review this access change successfully before applying it.");
            var currentReview = AdManagement.Review(Connection, Session, _snapshot, _request);
            if (currentReview.ResolvedIdentifier != _review.ResolvedIdentifier)
                throw new InvalidOperationException("The directory identity changed after review. Resolve and review it again.");
            var credentials = _credentials ?? throw new InvalidOperationException("Directory credentials have already been released.");
            if (_request.Operation == AdOperation.Join && (string.IsNullOrWhiteSpace(credentials.Username) || credentials.Password.Length == 0))
                throw new InvalidOperationException("Enter domain join credentials.");
            if (_request.Operation == AdOperation.Leave && (credentials.Username.Length == 0) != (credentials.Password.Length == 0))
                throw new InvalidOperationException("For directory machine-account cleanup, enter both username and password or leave both empty.");
            // Recheck session rights after directory lookups and immediately before mutation.
            AdManagement.RequirePermissions(Session, _request.Operation);
            AdManagement.RequireCurrent(Connection, _snapshot);
            try
            {
                switch (_request.Operation)
                {
                    case AdOperation.Join:
                        new EnableAdAction(Connection, _request.Domain, credentials.Username, credentials.Password, requireCleanDisable: true).RunSync(Session);
                        break;
                    case AdOperation.Leave:
                        var config = new Dictionary<string, string>();
                        if (credentials.Username.Length > 0) { config[DisableAdAction.KEY_USER] = credentials.Username; config[DisableAdAction.KEY_PASS] = credentials.Password; }
                        try { new DisableAdAction(Connection, config).RunSync(Session); }
                        finally { config.Clear(); }
                        break;
                    case AdOperation.AddSubject:
                        var create = new AddRemoveSubjectsAction(Connection, [_request.SubjectName], [],
                            new Dictionary<string, string> { [_request.SubjectName] = _review.ResolvedIdentifier! });
                        create.RunSync(Session);
                        var subjects = Subject.get_all_records(Session);
                        var created = subjects.Where(pair => pair.Value.subject_identifier == _review.ResolvedIdentifier).ToArray();
                        if (created.Length != 1) throw new InvalidOperationException("The created subject could not be identified. Inspect current access before assigning roles.");
                        created[0].Value.opaque_ref = created[0].Key.opaque_ref;
                        var createdSubject = created[0].Value;
                        SetRoles(createdSubject.opaque_ref, createdSubject.uuid, createdSubject.subject_identifier,
                            string.Join('\n', createdSubject.roles.Select(role => role.opaque_ref).Order(StringComparer.Ordinal)));
                        break;
                    case AdOperation.SetRoles:
                        var reviewedSubject = _snapshot.Subjects.Single(subject => subject.Reference == _request.SubjectReference);
                        SetRoles(reviewedSubject.Reference, reviewedSubject.Uuid, reviewedSubject.Identifier, reviewedSubject.RoleReferences);
                        break;
                    case AdOperation.RemoveSubject:
                        new AddRemoveSubjectsAction(Connection, [], [Connection.Resolve(new XenRef<Subject>(_request.SubjectReference!))]).RunSync(Session);
                        break;
                }
            }
            catch
            {
                // Credential-bearing shared actions already discard raw server
                // errors before AsyncAction can retain or log them.
                throw new InvalidOperationException(AdManagement.RecoveryNotice);
            }
            Description = "Access change completed. Reopen access management to inspect the resulting host and subject state, then verify a new sign-in using the intended account.";
        }
        finally { _credentials?.Dispose(); _credentials = null; }
    }

    private void SetRoles(string subjectReference, string subjectUuid, string subjectIdentifier, string subjectRoleReferences)
    {
        AdManagement.RequirePermissions(Session, _request.Operation);
        var inventory = AdInventory.Read(Session, _snapshot.PoolReference);
        if (_snapshot.InfrastructureFingerprint != inventory.Snapshot().InfrastructureFingerprint
            || _snapshot.InfrastructureFingerprint != AdInventory.Cached(Connection.Resolve(new XenRef<Pool>(_snapshot.PoolReference))).Snapshot().InfrastructureFingerprint)
            throw new InvalidOperationException("The pool, domain or role definitions changed before role assignment. Inspect the current subject before another attempt.");
        // Compare with immutable reviewed values (or the captured post-create
        // values), never a cache object that events can change during the reads.
        var serverSubject = inventory.Subjects.SingleOrDefault(candidate => candidate.opaque_ref == subjectReference);
        if (serverSubject == null || serverSubject.uuid != subjectUuid || serverSubject.subject_identifier != subjectIdentifier
            || string.Join('\n', serverSubject.roles.Select(role => role.opaque_ref).Order(StringComparer.Ordinal)) != subjectRoleReferences)
            throw new InvalidOperationException("The subject changed before its roles could be assigned. Inspect its current identity and roles.");
        if (serverSubject.roles.Any(reference => !inventory.Roles.Any(role => role.opaque_ref == reference.opaque_ref && !role.is_internal && role.subroles.Count > 0)))
            throw new InvalidOperationException("The created subject has an unknown or internal default role. Inspect its effective access before assigning roles.");
        // The shared action calculates its delta from this exact server subject
        // and the role definitions checked in the reviewed cache and server.
        var roles = _request.RoleReferences.Select(reference => Connection.Resolve(new XenRef<Role>(reference))
            ?? throw new InvalidOperationException("A reviewed role disappeared.")).ToList();
        new AddRemoveRolesAction(Connection, serverSubject, roles).RunSync(Session);
        Session.logout_subject_identifier(Session, serverSubject.subject_identifier);
    }

    protected override void Clean()
    {
        _credentials?.Dispose(); _credentials = null;
        base.Clean();
    }
}
