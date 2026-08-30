using System;

namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Snapshot of a single RealmJoin package install observed via the
    /// <c>HKLM\SOFTWARE\RealmJoin\Packages\&lt;packageId&gt;</c> (machine scope) or
    /// <c>HKEY_USERS\&lt;sid&gt;\SOFTWARE\RealmJoin\Packages\&lt;packageId&gt;</c> (user scope)
    /// registry hierarchy. Held inside <see cref="RealmJoinFacts.Packages"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="DisplayName"/> is truncated to <see cref="MaxDisplayNameLength"/> characters
    /// by the adapter before being recorded. <see cref="CompletedUtc"/> / <see cref="Success"/>
    /// / <see cref="LastExitCode"/> are null while the package is still installing.
    /// </remarks>
    public sealed class RealmJoinPackageFact
    {
        public const int MaxDisplayNameLength = FactStringBounds.MaxLength;

        /// <summary>Machine-scope registration (<c>HKLM\SOFTWARE\RealmJoin\Packages</c>).</summary>
        public const string ScopeMachine = "machine";

        /// <summary>User-scope registration (<c>HKU\&lt;sid&gt;\SOFTWARE\RealmJoin\Packages</c>).</summary>
        public const string ScopeUser = "user";

        /// <summary>
        /// Map a payload scope string onto <see cref="ScopeMachine"/> / <see cref="ScopeUser"/>.
        /// The agent adapter only ever emits those two; anything else (missing, forged,
        /// arbitrarily long) is treated as machine scope so the value stored into state is
        /// always one of the two known constants.
        /// </summary>
        public static string NormalizeScope(string? scope)
        {
            return string.Equals(scope, ScopeUser, StringComparison.Ordinal) ? ScopeUser : ScopeMachine;
        }

        public RealmJoinPackageFact(
            string packageId,
            string displayName,
            string? version,
            string scope,
            DateTime startedUtc,
            DateTime? completedUtc,
            bool? success,
            int? lastExitCode)
        {
            if (string.IsNullOrEmpty(packageId)) throw new ArgumentException("PackageId is mandatory.", nameof(packageId));
            if (string.IsNullOrEmpty(scope)) throw new ArgumentException("Scope is mandatory.", nameof(scope));

            PackageId = packageId;
            DisplayName = displayName ?? string.Empty;
            Version = version;
            Scope = scope;
            StartedUtc = startedUtc;
            CompletedUtc = completedUtc;
            Success = success;
            LastExitCode = lastExitCode;
        }

        public string PackageId { get; }

        public string DisplayName { get; }

        public string? Version { get; }

        /// <summary>Either <see cref="ScopeMachine"/> or <see cref="ScopeUser"/>.</summary>
        public string Scope { get; }

        public DateTime StartedUtc { get; }

        public DateTime? CompletedUtc { get; }

        public bool? Success { get; }

        public int? LastExitCode { get; }

        public RealmJoinPackageFact WithCompletion(DateTime completedUtc, bool success, int lastExitCode) =>
            new RealmJoinPackageFact(
                packageId: PackageId,
                displayName: DisplayName,
                version: Version,
                scope: Scope,
                startedUtc: StartedUtc,
                completedUtc: completedUtc,
                success: success,
                lastExitCode: lastExitCode);
    }
}
