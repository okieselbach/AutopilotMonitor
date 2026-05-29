using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;
using static AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers.SoftwareInventoryNormalization;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers
{
    /// <summary>
    /// Collects installed software inventory from the Windows registry, normalizes
    /// vendor/product/version strings, and emits structured events for vulnerability correlation.
    ///
    /// Lifecycle:
    ///   AnalyzeAtStartup()  — baseline inventory snapshot (before enrollment installs)
    ///   AnalyzeAtShutdown() — final inventory + delta (what was installed during enrollment)
    ///
    /// The actual CVE/KEV correlation happens server-side after the events are ingested.
    /// </summary>
    public class SoftwareInventoryAnalyzer : IAgentAnalyzer
    {
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;

        // Captured at startup for delta detection at shutdown
        private List<SoftwareEntry> _startupInventory;

        // Idempotency guard — the WhiteGlove-Part-1 shutdown snapshot is fired by TWO
        // independent paths: WhiteGloveInventoryTrigger (preferred, fires on Windows
        // Event 62407 immediately) and EnrollmentTerminationHandler.RunShutdown(wgPart=1)
        // (fallback when the WG-success event never arrives). The two paths can race —
        // both check the guard, both pass it, both emit duplicate snapshots. The fix is
        // an atomic Interlocked.CompareExchange that wins-or-loses up-front, BEFORE the
        // work starts. On unhandled work failure the flag is reset so the second path
        // can still serve as the fallback (registry-read errors, transient I/O, etc.)
        // — clean failure semantics: a successful emit locks the flag forever, a
        // failed attempt frees it.
        // Encoded as int (0/1) instead of bool because Interlocked.CompareExchange has
        // no bool overload.
        private int _part1ShutdownSnapshotEmitted;
        private int _part2ShutdownSnapshotEmitted;

        public string Name => "SoftwareInventoryAnalyzer";

        // Maximum items per event chunk to stay within Table Storage property size limits (~64 KB)
        private const int ChunkSize = 75;

        // -----------------------------------------------------------------------
        // Registry paths for installed software
        // -----------------------------------------------------------------------

        private static readonly string[] UninstallRegistryPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        private const string UninstallRegistryPathHkcu =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        private const string ProfileListPath =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

        private const string AppxAllUserStorePath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications";

        // Publisher map, exclusion patterns, AppX whitelist, and normalization
        // regexes live in SoftwareInventoryNormalization.cs — edit that file to
        // add new vendor mappings or filter rules.

        public SoftwareInventoryAnalyzer(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId  = tenantId  ?? throw new ArgumentNullException(nameof(tenantId));
            _post      = post      ?? throw new ArgumentNullException(nameof(post));
            _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
        }

        public void AnalyzeAtStartup()
        {
            _logger.Info($"{Name}: Running startup analysis (baseline inventory)");
            try
            {
                _startupInventory = CollectAndNormalize();
                EmitInventoryEvents("startup", EnrollmentPhase.Unknown, _startupInventory, null);
            }
            catch (Exception ex)
            {
                _logger.Error($"{Name}: Startup analysis failed", ex);
            }
        }

        public void AnalyzeAtShutdown()
        {
            AnalyzeAtShutdown(whiteGlovePart: null);
        }

        /// <summary>
        /// Shutdown analysis with optional WhiteGlove part tag.
        /// Phase is always Unknown — analyzer events are NOT phase-declaration events.
        /// Only explicit phase-transition events (esp_phase_changed, agent_started) may carry
        /// a non-Unknown phase. Context is conveyed via DataJson (triggered_at, whiteglove_part).
        /// </summary>
        public void AnalyzeAtShutdown(int? whiteGlovePart)
        {
            // Per-WG-phase atomic claim. Set the flag BEFORE the work via CompareExchange
            // so two concurrent callers (e.g. WhiteGloveInventoryTrigger and
            // EnrollmentTerminationHandler firing nearly simultaneously) cannot both pass
            // the guard. The loser logs and returns. Non-WG shutdowns (whiteGlovePart=null)
            // skip the claim because there is exactly one such call per agent lifetime by
            // construction.
            if (whiteGlovePart == 1)
            {
                if (Interlocked.CompareExchange(ref _part1ShutdownSnapshotEmitted, 1, 0) != 0)
                {
                    _logger.Info($"{Name}: AnalyzeAtShutdown(whiteGlovePart=1) skipped — Part-1 snapshot already emitted (likely by WhiteGloveInventoryTrigger)");
                    return;
                }
            }
            else if (whiteGlovePart == 2)
            {
                if (Interlocked.CompareExchange(ref _part2ShutdownSnapshotEmitted, 1, 0) != 0)
                {
                    _logger.Info($"{Name}: AnalyzeAtShutdown(whiteGlovePart=2) skipped — Part-2 snapshot already emitted");
                    return;
                }
            }

            _logger.Info($"{Name}: Running shutdown analysis (delta detection, whiteGlovePart={whiteGlovePart?.ToString() ?? "none"})");
            try
            {
                var currentInventory = CollectAndNormalize();
                var newInstalls = ComputeDelta(_startupInventory ?? new List<SoftwareEntry>(), currentInventory);
                EmitInventoryEvents("shutdown", EnrollmentPhase.Unknown, currentInventory, newInstalls, whiteGlovePart);
            }
            catch (Exception ex)
            {
                _logger.Error($"{Name}: Shutdown analysis failed", ex);

                // Reset the flag so the fallback path (e.g. EnrollmentTerminationHandler
                // running after a Trigger-side failure) can still emit the snapshot.
                // Successful emits keep the flag set forever — duplicate suppression wins.
                if (whiteGlovePart == 1) Interlocked.Exchange(ref _part1ShutdownSnapshotEmitted, 0);
                else if (whiteGlovePart == 2) Interlocked.Exchange(ref _part2ShutdownSnapshotEmitted, 0);
            }
        }

        // -----------------------------------------------------------------------
        // Collection
        // -----------------------------------------------------------------------

        private List<SoftwareEntry> CollectAndNormalize()
        {
            var entries = new List<SoftwareEntry>();

            // HKLM 64-bit and WOW6432Node (32-bit)
            foreach (var path in UninstallRegistryPaths)
            {
                var source = path.Contains("WOW6432Node") ? "HKLM_32" : "HKLM_64";
                CollectFromKey(Registry.LocalMachine, path, source, entries);
            }

            // HKCU (may be empty during OOBE when running as SYSTEM)
            try
            {
                CollectFromKey(Registry.CurrentUser, UninstallRegistryPathHkcu, "HKCU", entries);
            }
            catch (Exception ex)
            {
                _logger.Debug($"{Name}: HKCU read skipped (expected during OOBE): {ex.Message}");
            }

            // HKU per-user Uninstall keys — catches per-user installs (VS Code user, Teams Classic, Spotify, etc.)
            CollectFromAllUserProfiles(entries);

            // AppX/MSIX packages — catches modern packaged apps (Company Portal, new Teams, etc.)
            CollectAppxPackages(entries);

            // Normalize all entries
            foreach (var entry in entries)
            {
                NormalizeEntry(entry);
            }

            _logger.Info($"{Name}: Collected {entries.Count} software entries");
            return entries;
        }

        private void CollectFromKey(RegistryKey rootKey, string subKeyPath, string source, List<SoftwareEntry> results)
        {
            // rootKey.Name yields the canonical hive name (HKEY_LOCAL_MACHINE / HKEY_USERS / …),
            // so the log line is a real registry path. `source` is a separate telemetry tag
            // (e.g. HKLM_64, HKU_<RID>) used as RegistrySource on the SoftwareEntry.
            try
            {
                using (var key = rootKey.OpenSubKey(subKeyPath, writable: false))
                {
                    if (key == null)
                    {
                        _logger.Debug($"{Name}: Registry key not found: {rootKey.Name}\\{subKeyPath} (source={source})");
                        return;
                    }

                    var subKeyNames = key.GetSubKeyNames();
                    foreach (var subKeyName in subKeyNames)
                    {
                        try
                        {
                            using (var subKey = key.OpenSubKey(subKeyName, writable: false))
                            {
                                if (subKey == null)
                                    continue;

                                var displayName = subKey.GetValue("DisplayName")?.ToString();
                                var systemComponent = subKey.GetValue("SystemComponent");
                                var parentKeyName = subKey.GetValue("ParentKeyName")?.ToString();

                                if (ShouldExclude(displayName, systemComponent, parentKeyName))
                                    continue;

                                results.Add(new SoftwareEntry
                                {
                                    DisplayName = displayName,
                                    DisplayVersion = subKey.GetValue("DisplayVersion")?.ToString(),
                                    Publisher = subKey.GetValue("Publisher")?.ToString(),
                                    InstallDate = subKey.GetValue("InstallDate")?.ToString(),
                                    InstallLocation = subKey.GetValue("InstallLocation")?.ToString(),
                                    UninstallString = subKey.GetValue("UninstallString")?.ToString(),
                                    IsWindowsInstaller = Convert.ToInt32(subKey.GetValue("WindowsInstaller") ?? 0) == 1,
                                    RegistrySource = source
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug($"{Name}: Error reading subkey {subKeyName}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"{Name}: Failed to read {rootKey.Name}\\{subKeyPath} (source={source}): {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // HKU per-user profile enumeration
        // -----------------------------------------------------------------------

        private void CollectFromAllUserProfiles(List<SoftwareEntry> results)
        {
            try
            {
                using (var profileList = Registry.LocalMachine.OpenSubKey(ProfileListPath, writable: false))
                {
                    if (profileList == null)
                    {
                        _logger.Debug($"{Name}: ProfileList key not found");
                        return;
                    }

                    // Track SIDs we already read via HKCU to avoid duplicates
                    var currentUserSid = GetCurrentUserSid();

                    foreach (var sid in profileList.GetSubKeyNames())
                    {
                        // Real user profiles only:
                        //   S-1-5-21-*  classic local/AD user accounts
                        //   S-1-12-1-*  Microsoft Account / Azure AD cloud SIDs (Win10 1903+ AAD-joined devices)
                        // Skip well-known service SIDs (S-1-5-18/19/20) and everything else.
                        if (!sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) &&
                            !sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Skip current user SID — already covered by HKCU read above
                        if (string.Equals(sid, currentUserSid, StringComparison.OrdinalIgnoreCase))
                            continue;

                        try
                        {
                            // HKU\<SID> is only available if the user's hive is loaded
                            var hkuPath = $@"{sid}\{UninstallRegistryPathHkcu}";
                            CollectFromKey(Registry.Users, hkuPath, $"HKU_{sid.Substring(sid.LastIndexOf('-') + 1)}", results);
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug($"{Name}: HKU read skipped for {sid}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"{Name}: Per-user profile enumeration failed: {ex.Message}");
            }
        }

        private static string GetCurrentUserSid()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    return identity.User?.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        // -----------------------------------------------------------------------
        // AppX/MSIX package enumeration
        // -----------------------------------------------------------------------

        private void CollectAppxPackages(List<SoftwareEntry> results)
        {
            try
            {
                using (var appxKey = Registry.LocalMachine.OpenSubKey(AppxAllUserStorePath, writable: false))
                {
                    if (appxKey == null)
                    {
                        _logger.Debug($"{Name}: AppX AllUserStore key not found");
                        return;
                    }

                    int appxCount = 0;

                    foreach (var subKeyName in appxKey.GetSubKeyNames())
                    {
                        try
                        {
                            if (ShouldExcludeAppx(subKeyName))
                                continue;

                            var parsed = ParseAppxPackageName(subKeyName);
                            if (parsed == null)
                                continue;

                            results.Add(parsed);
                            appxCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug($"{Name}: Error parsing AppX entry {subKeyName}: {ex.Message}");
                        }
                    }

                    _logger.Info($"{Name}: Collected {appxCount} AppX packages");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"{Name}: AppX enumeration failed: {ex.Message}");
            }
        }

        private static bool ShouldExcludeAppx(string packageFullName)
        {
            // Extract the package identity (everything before the first underscore)
            var underscoreIndex = packageFullName.IndexOf('_');
            var packageId = underscoreIndex > 0
                ? packageFullName.Substring(0, underscoreIndex)
                : packageFullName;

            // Strict whitelist — only explicitly listed packages pass through
            return !AppxWhitelist.Contains(packageId);
        }

        private static SoftwareEntry ParseAppxPackageName(string packageFullName)
        {
            // Format: {Publisher.PackageName}_{Version}_{Arch}__{PublisherHash}
            // Example: Microsoft.CompanyPortal_5.0.6155.0_x64__8wekyb3d8bbwe
            var match = AppxPackagePattern.Match(packageFullName);
            if (!match.Success)
                return null;

            var packageId = match.Groups[1].Value;   // e.g. "Microsoft.CompanyPortal"
            var version = match.Groups[2].Value;     // e.g. "5.0.6155.0"

            // Split publisher from product: "Microsoft.CompanyPortal" → ("Microsoft", "CompanyPortal")
            var dotIndex = packageId.IndexOf('.');
            string publisher;
            string productName;
            if (dotIndex > 0)
            {
                publisher = packageId.Substring(0, dotIndex);
                productName = packageId.Substring(dotIndex + 1);
            }
            else
            {
                publisher = packageId;
                productName = packageId;
            }

            // Make display name human-readable: "CompanyPortal" → "Company Portal"
            var displayName = SpacePascalCase(productName);

            return new SoftwareEntry
            {
                DisplayName = displayName,
                DisplayVersion = version,
                Publisher = publisher,
                RegistrySource = "AppX",
                IsWindowsInstaller = false
            };
        }

        private static string SpacePascalCase(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // Insert space before uppercase letters that follow lowercase letters
            // "CompanyPortal" → "Company Portal", but "MSTeams" → "MS Teams"
            var result = new System.Text.StringBuilder(input.Length + 4);
            result.Append(input[0]);
            for (int i = 1; i < input.Length; i++)
            {
                if (char.IsUpper(input[i]) && char.IsLower(input[i - 1]))
                    result.Append(' ');
                result.Append(input[i]);
            }
            return result.ToString();
        }

        // -----------------------------------------------------------------------
        // Filtering
        // -----------------------------------------------------------------------

        private static bool ShouldExclude(string displayName, object systemComponent, string parentKeyName)
        {
            // No display name = not a user-visible application
            if (string.IsNullOrWhiteSpace(displayName))
                return true;

            // System component flag
            if (systemComponent != null)
            {
                try
                {
                    if (Convert.ToInt32(systemComponent) == 1)
                        return true;
                }
                catch { /* non-integer value, ignore */ }
            }

            // Child component / update (has a parent)
            if (!string.IsNullOrEmpty(parentKeyName))
                return true;

            // KB updates (Windows Updates)
            if (KbUpdatePattern.IsMatch(displayName))
                return true;

            // Known noise patterns (contains)
            foreach (var pattern in ExcludeContains)
            {
                if (displayName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            // Known noise patterns (starts with)
            foreach (var pattern in ExcludeStartsWith)
            {
                if (displayName.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        // -----------------------------------------------------------------------
        // Normalization
        // -----------------------------------------------------------------------

        private void NormalizeEntry(SoftwareEntry entry)
        {
            entry.NormalizedPublisher = NormalizePublisher(entry.Publisher);
            entry.NormalizedName = NormalizeName(entry.DisplayName);
            entry.NormalizedVersion = NormalizeVersion(entry.DisplayVersion);
            AssessConfidence(entry);
        }

        private static string NormalizePublisher(string publisher)
        {
            if (string.IsNullOrWhiteSpace(publisher))
                return string.Empty;

            var trimmed = publisher.Trim();

            // Check known publisher map
            if (PublisherMap.TryGetValue(trimmed, out var mapped))
                return mapped;

            // Fallback: lowercase + strip common suffixes
            var lower = trimmed.ToLowerInvariant();
            foreach (var suffix in PublisherSuffixes)
            {
                if (lower.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    lower = lower.Substring(0, lower.Length - suffix.Length).TrimEnd();
                    break; // strip only one suffix
                }
            }

            return lower;
        }

        private static string NormalizeName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                return string.Empty;

            var name = displayName.Trim().ToLowerInvariant();

            // Strip architecture markers: (x64), (x86), (64-bit), - x64, etc.
            name = ArchitecturePattern.Replace(name, "");

            // Strip trailing version-like suffix: "chrome 134.0.6998.89" → "chrome"
            name = TrailingVersionPattern.Replace(name, "");

            return name.Trim();
        }

        private static string NormalizeVersion(string displayVersion)
        {
            if (string.IsNullOrWhiteSpace(displayVersion))
                return string.Empty;

            var match = VersionExtractPattern.Match(displayVersion.Trim());
            return match.Success ? match.Groups[1].Value : displayVersion.Trim();
        }

        private void AssessConfidence(SoftwareEntry entry)
        {
            bool publisherKnown = !string.IsNullOrEmpty(entry.Publisher) &&
                                  PublisherMap.ContainsKey(entry.Publisher.Trim());
            bool versionClean = !string.IsNullOrEmpty(entry.DisplayVersion) &&
                                VersionExtractPattern.IsMatch(entry.DisplayVersion);

            if (publisherKnown && versionClean)
                entry.NormalizationConfidence = "high";
            else if (publisherKnown || versionClean)
                entry.NormalizationConfidence = "medium";
            else
                entry.NormalizationConfidence = "low";
        }

        // -----------------------------------------------------------------------
        // Delta computation
        // -----------------------------------------------------------------------

        private static List<SoftwareEntry> ComputeDelta(List<SoftwareEntry> baseline, List<SoftwareEntry> current)
        {
            var baselineKeys = new HashSet<string>(
                baseline.Select(e => MakeKey(e)),
                StringComparer.OrdinalIgnoreCase);

            return current.Where(e => !baselineKeys.Contains(MakeKey(e))).ToList();
        }

        private static string MakeKey(SoftwareEntry e)
        {
            return $"{e.RegistrySource}|{e.DisplayName}|{e.DisplayVersion ?? ""}";
        }

        // -----------------------------------------------------------------------
        // Event emission (with chunking for large inventories)
        // -----------------------------------------------------------------------

        private void EmitInventoryEvents(
            string trigger,
            EnrollmentPhase phase,
            List<SoftwareEntry> inventory,
            List<SoftwareEntry> newInstalls,
            int? whiteGlovePart = null)
        {
            int totalChunks = Math.Max(1, (int)Math.Ceiling(inventory.Count / (double)ChunkSize));

            var highCount = inventory.Count(e => e.NormalizationConfidence == "high");
            var medCount = inventory.Count(e => e.NormalizationConfidence == "medium");
            var lowCount = inventory.Count(e => e.NormalizationConfidence == "low");

            for (int i = 0; i < totalChunks; i++)
            {
                var chunk = inventory.Skip(i * ChunkSize).Take(ChunkSize).ToList();

                var data = new Dictionary<string, object>
                {
                    { "triggered_at", trigger },
                    { "total_count", inventory.Count },
                    { "chunk_index", i },
                    { "chunk_count", totalChunks },
                    { "inventory", chunk.Select(SerializeEntry).ToList() }
                };

                // White Glove part tag (1 = pre-provisioning, 2 = user enrollment)
                if (whiteGlovePart.HasValue)
                {
                    data["whiteglove_part"] = whiteGlovePart.Value;
                }

                // Confidence summary only in first chunk
                if (i == 0)
                {
                    data["confidence_summary"] = new Dictionary<string, object>
                    {
                        { "high", highCount },
                        { "medium", medCount },
                        { "low", lowCount }
                    };
                }

                // New installs only in last chunk
                if (i == totalChunks - 1 && newInstalls != null)
                {
                    data["new_installs_during_enrollment"] = newInstalls.Select(SerializeEntry).ToList();
                    data["new_installs_count"] = newInstalls.Count;
                }

                var message = i == 0
                    ? (trigger == "startup"
                        ? $"{Name}: Baseline inventory ({inventory.Count} items, {highCount} high-confidence)"
                        : $"{Name}: Shutdown inventory ({inventory.Count} items, {newInstalls?.Count ?? 0} new during enrollment)")
                    : $"{Name}: Inventory chunk {i + 1}/{totalChunks}";

                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = Constants.EventTypes.SoftwareInventoryAnalysis,
                    Severity = EventSeverity.Info,
                    Source = Name,
                    Phase = phase,
                    Message = message,
                    Data = data
                });
            }
        }

        private static Dictionary<string, object> SerializeEntry(SoftwareEntry e)
        {
            return new Dictionary<string, object>
            {
                { "displayName", e.DisplayName ?? "" },
                { "displayVersion", e.DisplayVersion ?? "" },
                { "publisher", e.Publisher ?? "" },
                { "installDate", e.InstallDate ?? "" },
                { "registrySource", e.RegistrySource ?? "" },
                { "normalizedPublisher", e.NormalizedPublisher ?? "" },
                { "normalizedName", e.NormalizedName ?? "" },
                { "normalizedVersion", e.NormalizedVersion ?? "" },
                { "normalizationConfidence", e.NormalizationConfidence ?? "low" }
            };
        }

        // -----------------------------------------------------------------------
        // Private model
        // -----------------------------------------------------------------------

        private class SoftwareEntry
        {
            public string DisplayName { get; set; }
            public string DisplayVersion { get; set; }
            public string Publisher { get; set; }
            public string InstallDate { get; set; }
            public string InstallLocation { get; set; }
            public string UninstallString { get; set; }
            public bool IsWindowsInstaller { get; set; }
            public string RegistrySource { get; set; }

            // Normalized fields
            public string NormalizedPublisher { get; set; }
            public string NormalizedName { get; set; }
            public string NormalizedVersion { get; set; }
            public string NormalizationConfidence { get; set; }
        }
    }
}
