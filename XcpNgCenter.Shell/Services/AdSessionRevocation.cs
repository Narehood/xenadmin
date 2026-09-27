using XenAPI;

namespace XcpNgCenter.Shell.Services;

/// <summary>Revokes discovered directory sessions affected by a confirmed subject or role change.</summary>
internal static class AdSessionRevocation
{
    internal static void Revoke(Session session, string identifier, bool isGroup, bool targetAlreadyLoggedOut = false)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new AdValidationException("The changed directory subject has no confirmed identifier. Inspect active sessions before declaring access revoked.");

        if (!targetAlreadyLoggedOut)
            Session.logout_subject_identifier(session, identifier);
        if (!isGroup) return;

        // xapi's direct logout matches the authenticated user or primary subject,
        // but does not examine all transitive group grants. Enumeration includes
        // user SIDs and primary group SIDs; the membership API accepts both.
        // Local-root sessions are excluded by this server API and protected by logout.
        foreach (var candidate in Session.get_all_subject_identifiers(session)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate) && candidate != identifier)
            .Distinct(StringComparer.Ordinal))
        {
            var groups = Auth.get_group_membership(session, candidate);
            if (groups.Contains(identifier, StringComparer.Ordinal))
                Session.logout_subject_identifier(session, candidate);
        }
        // Any lookup/logout failure propagates: role changes can already be
        // committed, and an incomplete revocation must not report success.
    }
}
