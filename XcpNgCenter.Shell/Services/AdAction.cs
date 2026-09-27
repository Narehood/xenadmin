using XenAdmin.Actions;
using XenAdmin.Network;
using XenAPI;

namespace XcpNgCenter.Shell.Services;

/// <summary>Only fixed client validation messages may use this trusted marker.</summary>
internal sealed class AdValidationException(string message) : InvalidOperationException(message);

/// <summary>A redacted action outcome; no server exception is retained as an inner exception.</summary>
public sealed class AdActionException : InvalidOperationException
{
    internal AdActionException(string safeMessage, bool mutationAttempted)
        : base(safeMessage + "\n" + (mutationAttempted ? AdManagement.RecoveryNotice : "No server changes were attempted."))
        => MutationAttempted = mutationAttempted;

    public bool MutationAttempted { get; }
}

/// <summary>Runs one reviewed operation, stops on failure, and never repeats an unconfirmed mutation.</summary>
public sealed class AdAction : AsyncAction
{
    private readonly AdSnapshot _snapshot;
    private readonly AdRequest _request;
    private readonly AdReview _review;
    private AdCredentials? _credentials;
    private bool _mutationAttempted;

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
                throw new AdValidationException("Review this access change successfully before applying it.");
            var currentReview = AdManagement.Review(Connection, Session, _snapshot, _request);
            if (currentReview.ResolvedIdentifier != _review.ResolvedIdentifier)
                throw new AdValidationException("The directory identity changed after review. Resolve and review it again.");
            var credentials = _credentials ?? throw new AdValidationException("Directory credentials have already been released.");
            if (_request.Operation is AdOperation.Join or AdOperation.Leave && credentials.Username.Trim() != _request.CredentialUsername)
                throw new AdValidationException("The directory credential username or cleanup choice changed after review. Review the updated operation again.");
            if (_request.Operation == AdOperation.Join && (string.IsNullOrWhiteSpace(credentials.Username) || credentials.Password.Length == 0))
                throw new AdValidationException("Enter domain join credentials.");
            if (_request.Operation == AdOperation.Leave && (credentials.Username.Trim().Length == 0) != (credentials.Password.Length == 0))
                throw new AdValidationException("For directory machine-account cleanup, enter both username and password or leave both empty.");
            // Recheck session rights after directory lookups and immediately before mutation.
            AdManagement.RequirePermissions(Session, _request.Operation);
            AdManagement.RequireCurrent(Connection, _snapshot);
            switch (_request.Operation)
            {
                case AdOperation.Join:
                    var join = new EnableAdAction(Connection, _request.Domain, credentials.Username.Trim(), credentials.Password, requireCleanDisable: true);
                    _mutationAttempted = true;
                    join.RunSync(Session);
                    break;
                case AdOperation.Leave:
                    var config = new Dictionary<string, string>();
                    if (_request.LeaveMachineAccountCleanup) { config[DisableAdAction.KEY_USER] = credentials.Username.Trim(); config[DisableAdAction.KEY_PASS] = credentials.Password; }
                    try
                    {
                        var leave = new DisableAdAction(Connection, config);
                        _mutationAttempted = true;
                        leave.RunSync(Session);
                    }
                    finally { config.Clear(); }
                    break;
                case AdOperation.AddSubject:
                    var create = new AddRemoveSubjectsAction(Connection, [_request.SubjectName], [],
                        new Dictionary<string, string> { [_request.SubjectName] = _review.ResolvedIdentifier! });
                    try { create.RunSync(Session); }
                    finally { _mutationAttempted |= create.MutationAttempted; }
                    var subjects = Subject.get_all_records(Session);
                    var created = subjects.Where(pair => pair.Value.subject_identifier == _review.ResolvedIdentifier).ToArray();
                    if (created.Length != 1) throw new AdValidationException("The created subject could not be identified. Inspect current access before assigning roles.");
                    created[0].Value.opaque_ref = created[0].Key.opaque_ref;
                    var createdSubject = created[0].Value;
                    SetRoles(createdSubject.opaque_ref, createdSubject.uuid, createdSubject.subject_identifier,
                        string.Join('\n', createdSubject.roles.Select(role => role.opaque_ref).Order(StringComparer.Ordinal)),
                        createdSubject.other_config.GetValueOrDefault("subject-is-group") == "true");
                    break;
                case AdOperation.SetRoles:
                    var reviewedSubject = _snapshot.Subjects.Single(subject => subject.Reference == _request.SubjectReference);
                    SetRoles(reviewedSubject.Reference, reviewedSubject.Uuid, reviewedSubject.Identifier, reviewedSubject.RoleReferences, reviewedSubject.IsGroup);
                    break;
                case AdOperation.RemoveSubject:
                    var removedSubject = _snapshot.Subjects.Single(subject => subject.Reference == _request.SubjectReference);
                    var subject = Connection.Resolve(new XenRef<Subject>(_request.SubjectReference!))
                        ?? throw new AdValidationException("The reviewed subject disappeared. Reopen access management.");
                    var remove = new AddRemoveSubjectsAction(Connection, [], [subject]);
                    try { remove.RunSync(Session); }
                    finally { _mutationAttempted |= remove.MutationAttempted; }
                    _mutationAttempted = true;
                    AdSessionRevocation.Revoke(Session, removedSubject.Identifier, removedSubject.IsGroup, targetAlreadyLoggedOut: true);
                    break;
            }
            Description = "Access change completed. Reopen access management to inspect the resulting host and subject state, then verify a new sign-in using the intended account.";
        }
        catch (Exception error)
        {
            // Only our explicit client guards and fixed shared-action exceptions
            // are trusted. RPC providers may throw any exception type, including
            // InvalidOperationException, with credential-bearing text.
            var message = error switch
            {
                AdValidationException or AddRemoveSubjectsAction.ReviewedIdentityChangedException
                    or EnableAdAction.PreparatoryLeaveFailure => error.Message,
                DirectoryActionFailure failure => failure.SafeDiagnostic,
                EnableAdAction.CredentialsFailure failure => failure.SafeDiagnostic,
                XenAdmin.CancelledException => "The directory access operation was cancelled.",
                _ => "The directory access operation was not confirmed. Server error details were omitted to protect credentials."
            };
            throw new AdActionException(message, _mutationAttempted);
        }
        finally { _credentials?.Dispose(); _credentials = null; }
    }

    private void SetRoles(string subjectReference, string subjectUuid, string subjectIdentifier, string subjectRoleReferences, bool subjectIsGroup)
    {
        AdManagement.RequirePermissions(Session, _request.Operation);
        var inventory = AdInventory.Read(Session, _snapshot.PoolReference);
        if (_snapshot.InfrastructureFingerprint != inventory.Snapshot().InfrastructureFingerprint
            || _snapshot.InfrastructureFingerprint != AdInventory.Cached(Connection.Resolve(new XenRef<Pool>(_snapshot.PoolReference))).Snapshot().InfrastructureFingerprint)
            throw new AdValidationException("The pool, domain or role definitions changed before role assignment. Inspect the current subject before another attempt.");
        // Compare with immutable reviewed values (or the captured post-create
        // values), never a cache object that events can change during the reads.
        var serverSubject = inventory.Subjects.SingleOrDefault(candidate => candidate.opaque_ref == subjectReference);
        if (serverSubject == null || serverSubject.uuid != subjectUuid || serverSubject.subject_identifier != subjectIdentifier
            || (serverSubject.other_config.GetValueOrDefault("subject-is-group") == "true") != subjectIsGroup
            || string.Join('\n', serverSubject.roles.Select(role => role.opaque_ref).Order(StringComparer.Ordinal)) != subjectRoleReferences)
            throw new AdValidationException("The subject changed before its roles could be assigned. Inspect its current identity and roles.");
        if (serverSubject.roles.Any(reference => !inventory.Roles.Any(role => role.opaque_ref == reference.opaque_ref && !role.is_internal && role.subroles.Count > 0)))
            throw new AdValidationException("The created subject has an unknown or internal default role. Inspect its effective access before assigning roles.");
        // The shared action calculates its delta from this exact server subject
        // and the role definitions checked in the reviewed cache and server.
        var roles = _request.RoleReferences.Select(reference => Connection.Resolve(new XenRef<Role>(reference))
            ?? throw new AdValidationException("A reviewed role disappeared.")).ToList();
        var changeRoles = new AddRemoveRolesAction(Connection, serverSubject, roles);
        _mutationAttempted = true;
        changeRoles.RunSync(Session);
        AdSessionRevocation.Revoke(Session, serverSubject.subject_identifier, subjectIsGroup);
    }

    protected override void Clean()
    {
        _credentials?.Dispose(); _credentials = null;
        base.Clean();
    }
}
