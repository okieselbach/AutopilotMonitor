using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Session a4537c36 (2026-07-10) — reads the ESP's own app-tracking lists from
    /// <c>HKLM\SOFTWARE\Microsoft\Windows\Autopilot\EnrollmentStatusTracking\ESPTrackingInfo\Diagnostics</c>.
    /// These are the packages the Enrollment Status Page actually tracks/blocks on — as opposed
    /// to the full set of required app assignments IME processes. Surfacing them lets
    /// session-debug distinguish "app is ESP-blocking" from "app is merely required"
    /// (an app pending at ESP exit is expected, not anomalous, when it is not in these lists).
    /// <para>
    /// Registry layout (documented in Microsoft's <c>Get-AutopilotDiagnostics.ps1</c>, PSGallery,
    /// and the ESP troubleshooting docs): under <c>Diagnostics</c> the device-scope category
    /// subkeys <c>ExpectedMSIAppPackages</c> (LocURI value names carrying MSI ProductCodes),
    /// <c>ExpectedModernAppPackages</c> (PackageFamilyNames) and <c>Sidecar</c> (Win32/IME apps —
    /// LocURIs carrying Intune app GUIDs); the user scope repeats the same structure under
    /// <c>S-&lt;SID&gt;</c> subkeys with <c>./User</c> LocURIs. Each category holds timestamped
    /// subkeys (one per CSP status write) whose value NAMES are the LocURIs — hence the
    /// dedupe-across-subkeys sets below.
    /// </para>
    /// <para>
    /// Identifier namespaces: only the Sidecar GUIDs match <c>AppPackageState.Id</c> (the Intune
    /// app GUID from the IME log) — they are normalized to lowercase dashed form for that
    /// purpose. MSI ProductCodes and PFNs live in different namespaces and are surfaced raw.
    /// </para>
    /// <para>
    /// Fail-soft like <see cref="EspSkipConfigurationProbe"/>: any failure is Debug-logged and
    /// yields <see cref="EspTrackingInfoSnapshot.Empty"/>; a missing Diagnostics key (non-Autopilot
    /// device, old OS build) is not an error.
    /// </para>
    /// </summary>
    internal static class EspTrackingInfoProbe
    {
        internal const string DiagnosticsKeyPath =
            @"SOFTWARE\Microsoft\Windows\Autopilot\EnrollmentStatusTracking\ESPTrackingInfo\Diagnostics";

        /// <summary>
        /// Cap per emitted list — bounds the esp_config_detected event size. Counts on the
        /// snapshot always carry the uncapped distinct totals.
        /// </summary>
        internal const int MaxIdsPerCategory = 50;

        private const string MsiCategoryKeyName = "ExpectedMSIAppPackages";
        private const string ModernCategoryKeyName = "ExpectedModernAppPackages";
        private const string SidecarCategoryKeyName = "Sidecar";

        private const string DashedGuidPattern =
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

        private static readonly Regex DashedGuidRegex = new Regex(DashedGuidPattern, RegexOptions.Compiled);

        private static readonly Regex BracedGuidRegex = new Regex(
            "\\{" + DashedGuidPattern + "\\}", RegexOptions.Compiled);

        /// <summary>
        /// Test seam — when non-null, <see cref="Read"/> delegates to this func instead of
        /// touching the live registry. Use <see cref="ScopedOverride"/> to guarantee cleanup.
        /// </summary>
        internal static Func<AgentLogger, EspTrackingInfoSnapshot> TestOverride;

        /// <summary>
        /// Reads the ESP tracking lists. <see cref="EspTrackingInfoSnapshot.HasData"/> is
        /// <c>true</c> when the Diagnostics key existed and was readable (even with empty
        /// lists); <see cref="EspTrackingInfoSnapshot.Empty"/> otherwise.
        /// </summary>
        /// <param name="logger">Optional logger — Debug-level trace only; no warn/error.</param>
        public static EspTrackingInfoSnapshot Read(AgentLogger logger = null)
        {
            var probe = TestOverride;
            if (probe != null) return probe(logger);

            try
            {
                using (var diagKey = Registry.LocalMachine.OpenSubKey(DiagnosticsKeyPath))
                {
                    if (diagKey == null)
                    {
                        logger?.Debug("EspTrackingInfoProbe: ESPTrackingInfo\\Diagnostics registry key not found");
                        return EspTrackingInfoSnapshot.Empty;
                    }

                    var msi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var modern = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var win32 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var userWin32 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    CollectScope(diagKey, userScope: false, msi, modern, win32, userWin32, logger);

                    foreach (var sub in diagKey.GetSubKeyNames())
                    {
                        // User scope repeats the category structure under the signed-in user's SID.
                        if (!sub.StartsWith("S-", StringComparison.OrdinalIgnoreCase)) continue;
                        using (var sidKey = diagKey.OpenSubKey(sub))
                        {
                            if (sidKey != null)
                                CollectScope(sidKey, userScope: true, msi, modern, win32, userWin32, logger);
                        }
                    }

                    return new EspTrackingInfoSnapshot(
                        msiProductCodes: Capped(msi),
                        modernAppPfns: Capped(modern),
                        win32AppIds: Capped(win32),
                        userWin32AppIds: Capped(userWin32),
                        msiCount: msi.Count,
                        modernCount: modern.Count,
                        win32Count: win32.Count);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug($"EspTrackingInfoProbe: read threw: {ex.Message}");
                return EspTrackingInfoSnapshot.Empty;
            }
        }

        private static void CollectScope(
            RegistryKey scopeKey,
            bool userScope,
            HashSet<string> msi,
            HashSet<string> modern,
            HashSet<string> win32,
            HashSet<string> userWin32,
            AgentLogger logger)
        {
            CollectCategory(scopeKey, MsiCategoryKeyName, logger, locUri =>
            {
                if (TryExtractMsiProductCode(locUri, out var code)) msi.Add(code);
            });
            CollectCategory(scopeKey, ModernCategoryKeyName, logger, locUri =>
            {
                if (TryExtractModernAppPfn(locUri, out var pfn)) modern.Add(pfn);
            });
            CollectCategory(scopeKey, SidecarCategoryKeyName, logger, locUri =>
            {
                if (!TryExtractSidecarAppId(locUri, out var appId, out var uriIsUserScoped)) return;
                win32.Add(appId);
                if (userScope || uriIsUserScoped) userWin32.Add(appId);
            });
        }

        private static void CollectCategory(
            RegistryKey scopeKey,
            string categoryKeyName,
            AgentLogger logger,
            Action<string> onLocUri)
        {
            try
            {
                using (var categoryKey = scopeKey.OpenSubKey(categoryKeyName))
                {
                    if (categoryKey == null) return;

                    // Value names may sit directly on the category key or (normally) on the
                    // timestamped per-status-write subkeys — read both, dedupe at the caller.
                    foreach (var name in categoryKey.GetValueNames())
                        onLocUri(name);

                    foreach (var stamp in categoryKey.GetSubKeyNames())
                    {
                        using (var stampKey = categoryKey.OpenSubKey(stamp))
                        {
                            if (stampKey == null) continue;
                            foreach (var name in stampKey.GetValueNames())
                                onLocUri(name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug($"EspTrackingInfoProbe: category '{categoryKeyName}' read threw: {ex.Message}");
            }
        }

        // Internal for direct unit-testing of the cap/sort contract (InternalsVisibleTo).
        internal static IReadOnlyList<string> Capped(HashSet<string> set) =>
            set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Take(MaxIdsPerCategory).ToList();

        /// <summary>
        /// Extracts the MSI ProductCode (braced GUID, also URL-encoded <c>%7B…%7D</c>) from an
        /// <c>EnterpriseDesktopAppManagement</c> LocURI value name. Returns the code in braced
        /// uppercase-GUID form (the MSI-native identity).
        /// </summary>
        internal static bool TryExtractMsiProductCode(string locUri, out string productCode)
        {
            productCode = null;
            if (string.IsNullOrEmpty(locUri)) return false;
            if (locUri.IndexOf("EnterpriseDesktopAppManagement", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            var decoded = locUri.Replace("%7B", "{").Replace("%7D", "}");
            var match = BracedGuidRegex.Match(decoded);
            if (!match.Success)
            {
                // Some writes carry the bare product GUID without braces.
                var bare = DashedGuidRegex.Match(decoded);
                if (!bare.Success) return false;
                productCode = "{" + bare.Value.ToUpperInvariant() + "}";
                return true;
            }

            productCode = match.Value.ToUpperInvariant();
            return true;
        }

        /// <summary>
        /// Extracts the PackageFamilyName from an <c>EnterpriseModernAppManagement</c> LocURI
        /// value name (the segment following <c>AppStore</c>, e.g.
        /// <c>…/AppManagement/AppStore/Publisher.App_hash/…</c>).
        /// </summary>
        internal static bool TryExtractModernAppPfn(string locUri, out string pfn)
        {
            pfn = null;
            if (string.IsNullOrEmpty(locUri)) return false;
            if (locUri.IndexOf("EnterpriseModernAppManagement", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            var segments = locUri.Split('/');
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!string.Equals(segments[i], "AppStore", StringComparison.OrdinalIgnoreCase)) continue;
                var candidate = segments[i + 1];
                // PFNs are "Name_publisherhash"; skip CSP verbs that can follow AppStore directly.
                if (candidate.IndexOf('_') > 0)
                {
                    pfn = candidate;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Extracts the Intune app GUID from a Sidecar (Win32/IME) LocURI value name and
        /// normalizes it to lowercase dashed form — the same shape as
        /// <c>AppPackageState.Id</c> in the IME tracker, so the two sides string-match
        /// (prepared for the espBlocking annotation follow-up).
        /// <paramref name="isUserScoped"/> is derived from a <c>./User</c> LocURI prefix.
        /// </summary>
        internal static bool TryExtractSidecarAppId(string locUri, out string appId, out bool isUserScoped)
        {
            appId = null;
            isUserScoped = false;
            if (string.IsNullOrEmpty(locUri)) return false;

            var match = DashedGuidRegex.Match(locUri);
            if (!match.Success) return false;

            appId = match.Value.ToLowerInvariant();
            isUserScoped = locUri.StartsWith("./User", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        /// <summary>
        /// Disposable scope that sets <see cref="TestOverride"/> for the lifetime of the scope
        /// and restores the previous value on Dispose. Nestable. Internal test-only helper.
        /// </summary>
        internal sealed class ScopedOverride : IDisposable
        {
            private readonly Func<AgentLogger, EspTrackingInfoSnapshot> _previous;
            private int _disposed;

            public ScopedOverride(Func<AgentLogger, EspTrackingInfoSnapshot> probe)
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
    /// Snapshot of the ESP tracking lists under <c>ESPTrackingInfo\Diagnostics</c>. Lists are
    /// distinct, sorted, and capped at <see cref="EspTrackingInfoProbe.MaxIdsPerCategory"/>;
    /// the counts carry the uncapped distinct totals. <see cref="HasData"/> is <c>false</c>
    /// only when the Diagnostics key was missing or unreadable.
    /// </summary>
    internal readonly struct EspTrackingInfoSnapshot
    {
        public static readonly EspTrackingInfoSnapshot Empty = default;

        public EspTrackingInfoSnapshot(
            IReadOnlyList<string> msiProductCodes,
            IReadOnlyList<string> modernAppPfns,
            IReadOnlyList<string> win32AppIds,
            IReadOnlyList<string> userWin32AppIds,
            int msiCount,
            int modernCount,
            int win32Count)
        {
            HasData = true;
            MsiProductCodes = msiProductCodes ?? Array.Empty<string>();
            ModernAppPfns = modernAppPfns ?? Array.Empty<string>();
            Win32AppIds = win32AppIds ?? Array.Empty<string>();
            UserWin32AppIds = userWin32AppIds ?? Array.Empty<string>();
            MsiCount = msiCount;
            ModernCount = modernCount;
            Win32Count = win32Count;
        }

        /// <summary><c>true</c> when the Diagnostics key existed and was readable.</summary>
        public bool HasData { get; }

        /// <summary>MSI ProductCodes (braced GUIDs) from <c>ExpectedMSIAppPackages</c>, device + user scope merged.</summary>
        public IReadOnlyList<string> MsiProductCodes { get; }

        /// <summary>PackageFamilyNames from <c>ExpectedModernAppPackages</c>, device + user scope merged.</summary>
        public IReadOnlyList<string> ModernAppPfns { get; }

        /// <summary>Intune app GUIDs (lowercase dashed) from <c>Sidecar</c>, device + user scope merged.</summary>
        public IReadOnlyList<string> Win32AppIds { get; }

        /// <summary>
        /// User-scoped subset of <see cref="Win32AppIds"/> — the apps the User-ESP apps gate
        /// actually blocks on. Often empty when read before sign-in (the <c>S-&lt;SID&gt;</c>
        /// scope only appears once the user session exists).
        /// </summary>
        public IReadOnlyList<string> UserWin32AppIds { get; }

        /// <summary>Uncapped distinct total behind <see cref="MsiProductCodes"/>.</summary>
        public int MsiCount { get; }

        /// <summary>Uncapped distinct total behind <see cref="ModernAppPfns"/>.</summary>
        public int ModernCount { get; }

        /// <summary>Uncapped distinct total behind <see cref="Win32AppIds"/>.</summary>
        public int Win32Count { get; }
    }
}
