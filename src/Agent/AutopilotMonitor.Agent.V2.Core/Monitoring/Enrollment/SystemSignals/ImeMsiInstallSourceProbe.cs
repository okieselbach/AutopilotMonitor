using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Session 2026-08-17 — resolves where the running IME build was downloaded from.
    /// The IME is installed through the EnterpriseDesktopAppManagement CSP, which documents
    /// every MSI it enforces under
    /// <c>HKLM\SOFTWARE\Microsoft\EnterpriseDesktopAppManagement\&lt;SID&gt;\MSI\{ProductCode}</c>
    /// (values: <c>CurrentDownloadUrl</c>, <c>ProductVersion</c>, <c>ProductCode</c>, …).
    /// Given the IME version parsed from the IME log, this probe finds the matching MSI entry
    /// and returns its <c>CurrentDownloadUrl</c> so the <c>ime_agent_version</c> event can say
    /// which CDN/AFD endpoint delivered exactly that build (e.g.
    /// <c>imeswdb-afd-secondary.manage.microsoft.com</c>).
    /// <para>
    /// Matching: first by normalized <c>ProductVersion</c> equality (trailing <c>.0</c>
    /// components ignored), then — because the version line in the log and the enforced MSI can
    /// drift apart around an IME self-update — by download-URL filename
    /// <c>IntuneWindowsAgent.msi</c>. The match kind is surfaced so consumers know whether the
    /// URL is version-authoritative.
    /// </para>
    /// <para>
    /// Fail-soft like <see cref="EspTrackingInfoProbe"/>: any failure is Debug-logged and yields
    /// <see cref="ImeMsiInstallSource.Empty"/>; a missing CSP key (no MDM-enforced MSIs yet) is
    /// not an error.
    /// </para>
    /// </summary>
    internal static class ImeMsiInstallSourceProbe
    {
        internal const string CspRootKeyPath = @"SOFTWARE\Microsoft\EnterpriseDesktopAppManagement";

        private const string ImeMsiFileName = "IntuneWindowsAgent.msi";

        /// <summary>
        /// Test seam — when non-null, <see cref="Read"/> delegates to this func instead of
        /// touching the live registry. Use <see cref="ScopedOverride"/> to guarantee cleanup.
        /// </summary>
        internal static Func<string, AgentLogger, ImeMsiInstallSource> TestOverride;

        /// <summary>
        /// Finds the CSP MSI entry for <paramref name="imeVersion"/> (the version string parsed
        /// from the IME log). Returns <see cref="ImeMsiInstallSource.Empty"/> when the CSP key is
        /// missing, unreadable, or holds no plausible entry.
        /// </summary>
        /// <param name="imeVersion">IME version from the log, e.g. <c>1.104.102.0</c>.</param>
        /// <param name="logger">Optional logger — Debug-level trace only; no warn/error.</param>
        public static ImeMsiInstallSource Read(string imeVersion, AgentLogger logger = null)
        {
            var probe = TestOverride;
            if (probe != null) return probe(imeVersion, logger);

            try
            {
                var candidates = new List<MsiEntry>();
                using (var rootKey = Registry.LocalMachine.OpenSubKey(CspRootKeyPath))
                {
                    if (rootKey == null)
                    {
                        logger?.Debug("ImeMsiInstallSourceProbe: EnterpriseDesktopAppManagement registry key not found");
                        return ImeMsiInstallSource.Empty;
                    }

                    // Layout: <SID>\MSI\{ProductCode}. The IME sits under the device-scope
                    // zeros-SID, but enumerate all SIDs — layout is the same for user scope.
                    foreach (var sid in rootKey.GetSubKeyNames())
                    {
                        using (var msiKey = rootKey.OpenSubKey(sid + @"\MSI"))
                        {
                            if (msiKey == null) continue;
                            foreach (var productCode in msiKey.GetSubKeyNames())
                            {
                                using (var productKey = msiKey.OpenSubKey(productCode))
                                {
                                    if (productKey == null) continue;
                                    candidates.Add(new MsiEntry(
                                        productCode: productCode,
                                        productVersion: productKey.GetValue("ProductVersion") as string,
                                        downloadUrl: productKey.GetValue("CurrentDownloadUrl") as string));
                                }
                            }
                        }
                    }
                }

                return SelectBestMatch(candidates, imeVersion, logger);
            }
            catch (Exception ex)
            {
                logger?.Debug($"ImeMsiInstallSourceProbe: read threw: {ex.Message}");
                return ImeMsiInstallSource.Empty;
            }
        }

        /// <summary>
        /// Pure selection logic (internal for unit tests): exact/normalized ProductVersion match
        /// wins; otherwise the first entry whose download URL points at
        /// <c>IntuneWindowsAgent.msi</c>.
        /// </summary>
        internal static ImeMsiInstallSource SelectBestMatch(
            IReadOnlyList<MsiEntry> candidates, string imeVersion, AgentLogger logger = null)
        {
            if (candidates == null || candidates.Count == 0) return ImeMsiInstallSource.Empty;

            foreach (var entry in candidates)
            {
                if (VersionsEqual(entry.ProductVersion, imeVersion))
                {
                    logger?.Debug($"ImeMsiInstallSourceProbe: matched by ProductVersion {entry.ProductVersion} ({entry.ProductCode})");
                    return new ImeMsiInstallSource(entry, matchedByVersion: true);
                }
            }

            foreach (var entry in candidates)
            {
                if (IsImeMsiUrl(entry.DownloadUrl))
                {
                    logger?.Debug($"ImeMsiInstallSourceProbe: matched by URL filename, registry version {entry.ProductVersion ?? "?"} vs log {imeVersion ?? "?"} ({entry.ProductCode})");
                    return new ImeMsiInstallSource(entry, matchedByVersion: false);
                }
            }

            logger?.Debug($"ImeMsiInstallSourceProbe: no CSP MSI entry matches IME version {imeVersion ?? "?"} ({candidates.Count} candidates)");
            return ImeMsiInstallSource.Empty;
        }

        /// <summary>
        /// Component-wise numeric compare with trailing-zero tolerance, so
        /// <c>1.104.102</c> == <c>1.104.102.0</c>. Non-numeric components fall back to an
        /// ordinal-ignore-case string compare of the whole version.
        /// </summary>
        internal static bool VersionsEqual(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;

            var pa = a.Trim().Split('.');
            var pb = b.Trim().Split('.');
            var len = Math.Max(pa.Length, pb.Length);
            for (var i = 0; i < len; i++)
            {
                var sa = i < pa.Length ? pa[i] : "0";
                var sb = i < pb.Length ? pb[i] : "0";
                if (!int.TryParse(sa, out var na) || !int.TryParse(sb, out var nb)) return false;
                if (na != nb) return false;
            }
            return true;
        }

        /// <summary>URL filename equals <c>IntuneWindowsAgent.msi</c> (query string ignored).</summary>
        internal static bool IsImeMsiUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            var path = url;
            var query = path.IndexOf('?');
            if (query >= 0) path = path.Substring(0, query);
            var slash = path.LastIndexOf('/');
            var fileName = slash >= 0 ? path.Substring(slash + 1) : path;
            return string.Equals(fileName, ImeMsiFileName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Raw candidate row from one <c>MSI\{ProductCode}</c> subkey.</summary>
        internal readonly struct MsiEntry
        {
            public MsiEntry(string productCode, string productVersion, string downloadUrl)
            {
                ProductCode = productCode;
                ProductVersion = productVersion;
                DownloadUrl = downloadUrl;
            }

            public string ProductCode { get; }
            public string ProductVersion { get; }
            public string DownloadUrl { get; }
        }

        /// <summary>
        /// Disposable scope that sets <see cref="TestOverride"/> for the lifetime of the scope
        /// and restores the previous value on Dispose. Nestable. Internal test-only helper.
        /// </summary>
        internal sealed class ScopedOverride : IDisposable
        {
            private readonly Func<string, AgentLogger, ImeMsiInstallSource> _previous;
            private int _disposed;

            public ScopedOverride(Func<string, AgentLogger, ImeMsiInstallSource> probe)
            {
                _previous = TestOverride;
                TestOverride = probe;
            }

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 1) return;
                TestOverride = _previous;
            }
        }
    }

    /// <summary>
    /// Resolved install source of the running IME build. <see cref="HasData"/> is <c>false</c>
    /// when no CSP MSI entry matched. <see cref="MatchedByVersion"/> distinguishes a
    /// ProductVersion match (URL is authoritative for the reported build) from the
    /// filename fallback (URL is the currently enforced IME MSI, version may have drifted).
    /// </summary>
    internal readonly struct ImeMsiInstallSource
    {
        public static readonly ImeMsiInstallSource Empty = default;

        internal ImeMsiInstallSource(ImeMsiInstallSourceProbe.MsiEntry entry, bool matchedByVersion)
        {
            HasData = true;
            ProductCode = entry.ProductCode;
            ProductVersion = entry.ProductVersion;
            DownloadUrl = entry.DownloadUrl;
            MatchedByVersion = matchedByVersion;
        }

        public bool HasData { get; }

        /// <summary>Braced MSI ProductCode subkey name, e.g. <c>{6a7dfc50-0395-4e1e-bf84-ed1404e72051}</c>.</summary>
        public string ProductCode { get; }

        /// <summary><c>ProductVersion</c> registry value (may be null on partial writes).</summary>
        public string ProductVersion { get; }

        /// <summary><c>CurrentDownloadUrl</c> registry value (may be null on partial writes).</summary>
        public string DownloadUrl { get; }

        /// <summary><c>true</c> = matched via ProductVersion; <c>false</c> = URL-filename fallback.</summary>
        public bool MatchedByVersion { get; }
    }
}
