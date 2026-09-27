using System;
using System.Collections.Generic;
using System.Linq;
using XenAdmin.Network;
using XenAPI;

namespace XenAdmin.Actions
{
    /// <summary>A directory RPC failure containing only allowlisted codes and locally resolved host identity.</summary>
    public sealed class DirectoryActionFailure : Failure
    {
        private static readonly HashSet<string> SafeCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            RBAC_PERMISSION_DENIED, HOST_OFFLINE, HOST_STILL_BOOTING, HOST_UNKNOWN_TO_MASTER,
            SESSION_INVALID, SESSION_AUTHENTICATION_FAILED, OTHER_OPERATION_IN_PROGRESS,
            "OPERATION_NOT_ALLOWED", "LICENSE_RESTRICTION", "AUTH_ALREADY_ENABLED", "POOL_AUTH_ALREADY_ENABLED",
            AUTH_ENABLE_FAILED, "AUTH_ENABLE_FAILED_DOMAIN_LOOKUP_FAILED", "AUTH_ENABLE_FAILED_INVALID_ACCOUNT",
            "AUTH_ENABLE_FAILED_INVALID_OU", "AUTH_ENABLE_FAILED_PERMISSION_DENIED", "AUTH_ENABLE_FAILED_UNAVAILABLE",
            "AUTH_ENABLE_FAILED_WRONG_CREDENTIALS", "AUTH_DISABLE_FAILED", "AUTH_IS_DISABLED",
            "POOL_AUTH_ENABLE_FAILED", "POOL_AUTH_ENABLE_FAILED_DOMAIN_LOOKUP_FAILED",
            "POOL_AUTH_ENABLE_FAILED_DUPLICATE_HOSTNAME", "POOL_AUTH_ENABLE_FAILED_INVALID_ACCOUNT",
            "POOL_AUTH_ENABLE_FAILED_INVALID_OU", "POOL_AUTH_ENABLE_FAILED_PERMISSION_DENIED",
            "POOL_AUTH_ENABLE_FAILED_UNAVAILABLE", POOL_AUTH_ENABLE_FAILED_WRONG_CREDENTIALS,
            "POOL_AUTH_DISABLE_FAILED", "POOL_AUTH_DISABLE_FAILED_INVALID_ACCOUNT",
            "POOL_AUTH_DISABLE_FAILED_PERMISSION_DENIED", "POOL_AUTH_DISABLE_FAILED_WRONG_CREDENTIALS"
        };

        private DirectoryActionFailure(List<string> description, string safeDiagnostic) : base(description)
            => SafeDiagnostic = safeDiagnostic;

        public string SafeDiagnostic { get; }
        public override string Message => ErrorDescription[0] == RBAC_PERMISSION_DENIED_FRIENDLY
            ? SafeDiagnostic + "\n" + base.Message : SafeDiagnostic;

        internal static DirectoryActionFailure Create(Exception error, IXenConnection connection, string operation, string attemptedMethod)
        {
            var failure = error as Failure;
            var code = failure != null && failure.ErrorDescription.Count > 0 && SafeCodes.Contains(failure.ErrorDescription[0])
                ? failure.ErrorDescription[0] : null;
            // Only these errors define their first parameter as the failed host.
            // Never copy an unresolved reference or provider-supplied host text.
            var hasHost = code != null && (code.StartsWith("POOL_AUTH_ENABLE_FAILED", StringComparison.Ordinal)
                || code.StartsWith("POOL_AUTH_DISABLE_FAILED", StringComparison.Ordinal)
                || code == HOST_OFFLINE || code == HOST_STILL_BOOTING || code == HOST_UNKNOWN_TO_MASTER);
            var host = hasHost && failure.ErrorDescription.Count > 1
                ? connection.Cache.Hosts.FirstOrDefault(candidate => candidate.opaque_ref == failure.ErrorDescription[1]) : null;
            var diagnostic = operation + " was not confirmed."
                + (code == null ? "" : " API error: " + code + ".")
                + (host == null ? "" : " Host: '" + host.Name() + "'.")
                + " Server error details were omitted to protect credentials.";
            // AsyncAction still recognizes RBAC and formats the required roles.
            // Its method argument comes from the call site, never provider text.
            var description = new List<string>
            {
                code ?? "DIRECTORY_OPERATION_UNCONFIRMED",
                code == RBAC_PERMISSION_DENIED ? attemptedMethod : host == null ? "" : host.Name(),
                ""
            };
            return new DirectoryActionFailure(description, diagnostic);
        }
    }
}
