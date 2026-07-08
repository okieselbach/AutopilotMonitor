namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Where a global built-in / community rule row came from — the discriminator that lets the
    /// seed/sunset machinery self-maintain without an operator ever having to reason about it.
    /// <para>
    /// The problem it solves: sunset (delete a rule + GC its per-tenant RuleState overrides) is
    /// driven by "is this rule still in the catalog?". The embedded binary's catalog is only a
    /// snapshot of the repo at build time, so a rule pulled from GitHub via reseed that the deployed
    /// binary doesn't yet ship would look "removed" to the binary and get hidden + sunset — even
    /// though it is legitimately present. Provenance records WHO owns each row so each source only
    /// sunsets what its OWN source stopped shipping:
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="Embedded"/> (also the null/legacy default): seeded from the deployed
    ///     binary's embedded catalog. The automatic embedded seed may sunset it when it disappears
    ///     from that catalog (i.e. was removed from the repo and redeployed).</item>
    ///   <item><see cref="GitHubAhead"/>: imported via GitHub reseed and NOT (yet) in the deployed
    ///     binary's embedded catalog. The embedded seed and the runtime catalog filter leave it
    ///     alone, so it stays visible and is never silently deleted. Once the binary catches up
    ///     (the rule appears in the embedded catalog), the embedded update path reclaims it to
    ///     <see cref="Embedded"/>. A GitHub reseed still sunsets it if the repo drops it — because
    ///     the repo, not the binary, is authoritative for GitHub-sourced rules.</item>
    /// </list>
    /// </summary>
    public static class RuleProvenance
    {
        public const string Embedded = "embedded";
        public const string GitHubAhead = "github";

        /// <summary>Null/empty is treated as <see cref="Embedded"/> so pre-existing rows (written
        /// before this field) keep the historical "binary owns it" semantics.</summary>
        public static string Normalize(string? provenance)
            => string.IsNullOrEmpty(provenance) ? Embedded : provenance;

        /// <summary>True when the row is a GitHub-reseeded rule not in the deployed binary's catalog
        /// — i.e. exempt from the embedded catalog-based sunset and runtime filter.</summary>
        public static bool IsGitHubAhead(string? provenance)
            => Normalize(provenance) == GitHubAhead;

        /// <summary>Two provenance values are equivalent if they normalize to the same owner. Keeps
        /// the seed update-diff from rewriting every legacy null row into "embedded" on first deploy.</summary>
        public static bool AreEquivalent(string? a, string? b)
            => Normalize(a) == Normalize(b);
    }
}
