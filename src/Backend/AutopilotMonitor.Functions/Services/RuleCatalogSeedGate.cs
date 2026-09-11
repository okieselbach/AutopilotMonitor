using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// The one decision in front of the three embedded self-seeds: may the deployed binary's
    /// catalog be applied to a non-empty global partition? Not while a GitHub reseed is newer
    /// than the build — the embedded catalog is then the older source and would resurrect
    /// deleted rows and revert changed ones (see <see cref="RuleCatalogStamp"/>). Without build
    /// info (tests) the gate is open and storage is not consulted.
    /// </summary>
    internal static class RuleCatalogSeedGate
    {
        public static async Task<bool> AllowedAsync(IRuleRepository repo, BackendBuildInfo? buildInfo, string kind, ILogger logger)
        {
            if (buildInfo == null) return true;

            var stamp = await repo.GetRuleCatalogStampAsync(kind);
            if (RuleCatalogStamp.EmbeddedSeedAllowed(stamp, buildInfo.BuildUtc)) return true;

            logger.LogInformation(
                "Embedded {Kind} catalog (build {BuildUtc:u}) is older than the GitHub reseed of {StampedAt:u} — self-seed skipped",
                kind, buildInfo.BuildUtc, stamp!.StampedAt);
            return false;
        }

        public static RuleCatalogStamp Stamp(string kind, string source, int count) => new()
        {
            Kind = kind,
            Source = source,
            StampedAt = DateTime.UtcNow,
            Count = count,
        };
    }
}
