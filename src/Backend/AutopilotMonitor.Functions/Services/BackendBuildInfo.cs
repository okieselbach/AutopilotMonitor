using System.Globalization;
using System.Linq;
using System.Reflection;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Exposes the backend build identity (version, commit hash, build time) surfaced
    /// via /api/health and /api/health/detailed. Values come from the assembly's
    /// InformationalVersion attribute which MSBuild populates from &lt;Version&gt; and
    /// -p:SourceRevisionId (the latter is set by the deploy workflow to github.sha).
    /// <para>
    /// <b>BuildUtc</b> reads the <c>BuildTimestampUtc</c> AssemblyMetadata attribute
    /// baked in by the .csproj at build time. The previous implementation used
    /// <c>File.GetLastWriteTimeUtc(asm.Location)</c>, which on Azure Functions reports
    /// the zip-extract / cold-start time of the running container — not the build
    /// moment. The metadata attribute travels inside the DLL and is therefore stable
    /// across deploy / cold-start.
    /// </para>
    /// </summary>
    public sealed class BackendBuildInfo
    {
        internal const string BuildTimestampMetadataKey = "BuildTimestampUtc";

        public string Version { get; }
        public string CommitHash { get; }
        public DateTime BuildUtc { get; }

        public BackendBuildInfo()
        {
            var asm = typeof(BackendBuildInfo).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "0.0.0";

            (Version, CommitHash) = ParseInformationalVersion(info);
            BuildUtc = ResolveBuildUtc(asm);
        }

        /// <summary>Test seam: a build identity with a chosen build time.</summary>
        internal BackendBuildInfo(string version, string commitHash, DateTime buildUtc)
        {
            Version = version;
            CommitHash = commitHash;
            BuildUtc = buildUtc;
        }

        /// <summary>
        /// Reads the <c>BuildTimestampUtc</c> AssemblyMetadata attribute. Falls back to
        /// <c>DateTime.UtcNow</c> only when the attribute is missing or unparseable —
        /// the file mtime fallback is intentionally gone because it lies on Azure
        /// Functions zip-deploys.
        /// </summary>
        private static DateTime ResolveBuildUtc(Assembly asm)
        {
            var raw = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => string.Equals(a.Key, BuildTimestampMetadataKey, StringComparison.Ordinal))?.Value;

            if (!string.IsNullOrEmpty(raw)
                && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            return DateTime.UtcNow;
        }

        /// <summary>
        /// Splits an InformationalVersion ("1.0.0+abc1234567...") into a semver part
        /// and a 7-char short commit hash. No '+' → empty commit hash.
        /// </summary>
        internal static (string Version, string CommitHash) ParseInformationalVersion(string informationalVersion)
        {
            if (string.IsNullOrWhiteSpace(informationalVersion))
            {
                return ("0.0.0", string.Empty);
            }

            var plus = informationalVersion.IndexOf('+');
            if (plus < 0)
            {
                return (informationalVersion, string.Empty);
            }

            var version = informationalVersion.Substring(0, plus);
            var sha = informationalVersion.Substring(plus + 1);
            var shortSha = sha.Length > 7 ? sha.Substring(0, 7) : sha;
            return (version, shortSha);
        }
    }
}
