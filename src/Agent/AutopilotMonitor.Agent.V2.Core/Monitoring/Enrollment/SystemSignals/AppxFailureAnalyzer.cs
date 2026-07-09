using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Aggregated candidate: one MSIX/Store package that failed in the scan window,
    /// scored by how strongly its log records correlate with the ESP failure.
    /// </summary>
    internal sealed class AppxFailureCandidate
    {
        public string PackageFullName { get; set; }
        /// <summary>Name segment of the PackageFullName (before the first underscore).</summary>
        public string PackageName { get; set; }
        public int Score { get; set; }
        /// <summary>hresult_match | appx_family_error | time_proximity</summary>
        public string MatchType { get; set; }
        /// <summary>Distinct HRESULTs seen for this package (capped).</summary>
        public List<string> Hresults { get; set; } = new List<string>();
        /// <summary>EventId of the best-scoring record.</summary>
        public int EventId { get; set; }
        /// <summary>Latest occurrence in the window.</summary>
        public DateTime TimeUtc { get; set; }
        public int Occurrences { get; set; }
        /// <summary>Truncated message (or property dump) of the best-scoring record.</summary>
        public string MessageExcerpt { get; set; }
    }

    /// <summary>Analysis outcome — verdict + scored candidates, ready for event emission.</summary>
    internal sealed class AppxFailureAnalysis
    {
        public string Verdict { get; set; }
        /// <summary>high | medium | low — honesty marker, never certainty.</summary>
        public string Confidence { get; set; }
        /// <summary>Sorted descending by score, capped at <see cref="AppxFailureAnalyzer.MaxCandidatesInPayload"/>.</summary>
        public List<AppxFailureCandidate> Candidates { get; set; } = new List<AppxFailureCandidate>();
        /// <summary>Total distinct failing packages before the payload cap.</summary>
        public int TotalCandidateCount { get; set; }
        /// <summary>Error/critical records considered in the window.</summary>
        public int ErrorEventCount { get; set; }
        /// <summary>HRESULTs seen in the window that differ from the ESP error code (capped).</summary>
        public List<string> OtherHresultsSeen { get; set; } = new List<string>();
        /// <summary>Set when verdict is scan_unavailable.</summary>
        public string ScanUnavailableReason { get; set; }
    }

    /// <summary>
    /// Pure scoring/verdict logic for the settle-window AppX enrichment — no event log, no
    /// tracker state, fully unit-testable. Careful time correlation is the point: the
    /// AppXDeploymentServer channel also logs unrelated background servicing failures, so
    /// candidates are scored (exact-HRESULT match &gt; AppX-family error &gt; time proximity)
    /// and the verdict/confidence express suspicion, not certainty.
    /// </summary>
    internal static class AppxFailureAnalyzer
    {
        internal const int MaxCandidatesInPayload = 5;
        internal const int MaxHresultsPerCandidate = 3;
        internal const int MaxOtherHresults = 5;
        internal const int MessageExcerptLength = 300;
        internal const int RecencyBonusWindowMinutes = 10;

        internal const int ScoreHresultMatch = 100;
        internal const int ScoreAppxFamilyError = 70;
        internal const int ScoreTimeProximity = 40;
        internal const int ScoreRecencyBonus = 10;

        internal const string MatchTypeHresultMatch = "hresult_match";
        internal const string MatchTypeAppxFamilyError = "appx_family_error";
        internal const string MatchTypeTimeProximity = "time_proximity";

        internal const string VerdictCandidateIdentified = "appx_candidate_identified";
        internal const string VerdictActivityNoHresultMatch = "appx_activity_no_hresult_match";
        internal const string VerdictErrorsNoPackage = "appx_errors_no_package";
        internal const string VerdictNoCandidates = "no_appx_candidates";
        internal const string VerdictScanUnavailable = "scan_unavailable";

        private static readonly Regex HResultPattern = new Regex(
            @"\b0[xX][0-9a-fA-F]{8}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // PackageFullName: Name_Version_Arch_ResourceId_PublisherHash.
        // Version is dotted-quad; arch is a fixed set; the publisher hash is 13 chars of the
        // Crockford-base32 subset Windows uses (no i/l/o/u). ResourceId may be empty.
        private static readonly Regex PackageFullNamePattern = new Regex(
            @"\b(?<name>[A-Za-z0-9][A-Za-z0-9.\-]{2,})_(?<ver>\d+(\.\d+){3})_(?<arch>x86|x64|arm|arm64|neutral)_(?<res>[A-Za-z0-9.\-~]*)_(?<hash>[a-hjkmnp-tv-z0-9]{13})\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>True for HRESULTs in the AppX/MSIX deployment error family (0x80073c##/0x80073d##).</summary>
        internal static bool IsAppxFamilyHresult(string hresult)
        {
            if (string.IsNullOrEmpty(hresult) || hresult.Length != 10) return false;
            return hresult.StartsWith("0x80073c", StringComparison.OrdinalIgnoreCase)
                || hresult.StartsWith("0x80073d", StringComparison.OrdinalIgnoreCase);
        }

        public static AppxFailureAnalysis Analyze(AppxFailureScanRequest request, AppxFailureScanResult scan)
        {
            var analysis = new AppxFailureAnalysis();

            if (scan == null || !scan.ChannelAvailable)
            {
                analysis.Verdict = VerdictScanUnavailable;
                analysis.Confidence = "low";
                analysis.ScanUnavailableReason = scan?.UnavailableReason ?? "no_result";
                return analysis;
            }

            analysis.ErrorEventCount = scan.Records.Count;

            var byPackage = new Dictionary<string, AppxFailureCandidate>(StringComparer.OrdinalIgnoreCase);
            var otherHresults = new List<string>();
            bool anyHresultSeen = false;

            foreach (var record in scan.Records)
            {
                var hresults = ExtractHresults(record);
                var packages = ExtractPackageFullNames(record);
                if (hresults.Count > 0) anyHresultSeen = true;

                foreach (var hr in hresults)
                {
                    if (!string.Equals(hr, request.EspErrorCode, StringComparison.OrdinalIgnoreCase)
                        && !otherHresults.Contains(hr, StringComparer.OrdinalIgnoreCase)
                        && otherHresults.Count < MaxOtherHresults)
                    {
                        otherHresults.Add(hr);
                    }
                }

                if (packages.Count == 0)
                    continue;

                // Score this record once; every package named in it inherits the score.
                string matchType;
                int score;
                if (request.EspErrorCode != null
                    && hresults.Any(h => string.Equals(h, request.EspErrorCode, StringComparison.OrdinalIgnoreCase)))
                {
                    matchType = MatchTypeHresultMatch;
                    score = ScoreHresultMatch;
                }
                else if (hresults.Any(IsAppxFamilyHresult))
                {
                    matchType = MatchTypeAppxFamilyError;
                    score = ScoreAppxFamilyError;
                }
                else
                {
                    matchType = MatchTypeTimeProximity;
                    score = ScoreTimeProximity;
                }

                if (record.TimeCreatedUtc >= request.WindowEndUtc.AddMinutes(-RecencyBonusWindowMinutes))
                    score += ScoreRecencyBonus;

                foreach (var pkg in packages)
                {
                    if (!byPackage.TryGetValue(pkg, out var candidate))
                    {
                        candidate = new AppxFailureCandidate
                        {
                            PackageFullName = pkg,
                            PackageName = pkg.Split('_')[0],
                            Score = -1,
                        };
                        byPackage[pkg] = candidate;
                    }

                    candidate.Occurrences++;
                    if (record.TimeCreatedUtc > candidate.TimeUtc)
                        candidate.TimeUtc = record.TimeCreatedUtc;
                    foreach (var hr in hresults)
                    {
                        if (candidate.Hresults.Count < MaxHresultsPerCandidate
                            && !candidate.Hresults.Contains(hr, StringComparer.OrdinalIgnoreCase))
                        {
                            candidate.Hresults.Add(hr);
                        }
                    }

                    if (score > candidate.Score)
                    {
                        candidate.Score = score;
                        candidate.MatchType = matchType;
                        candidate.EventId = record.EventId;
                        candidate.MessageExcerpt = BuildExcerpt(record);
                    }
                }
            }

            var sorted = byPackage.Values
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.TimeUtc)
                .ToList();

            analysis.TotalCandidateCount = sorted.Count;
            analysis.Candidates = sorted.Take(MaxCandidatesInPayload).ToList();
            analysis.OtherHresultsSeen = otherHresults;

            // Verdict + confidence — suspicion levels, kept honest.
            if (analysis.Candidates.Count > 0)
            {
                var top = analysis.Candidates[0];
                if (top.MatchType == MatchTypeHresultMatch)
                {
                    analysis.Verdict = VerdictCandidateIdentified;
                    analysis.Confidence = IsAppxFamilyHresult(request.EspErrorCode) ? "high" : "medium";
                }
                else
                {
                    analysis.Verdict = VerdictActivityNoHresultMatch;
                    analysis.Confidence = request.EspErrorCode == null && top.MatchType == MatchTypeAppxFamilyError
                        ? "medium"
                        : "low";
                }
            }
            else if (anyHresultSeen)
            {
                analysis.Verdict = VerdictErrorsNoPackage;
                analysis.Confidence = "low";
            }
            else
            {
                // Negative evidence: no AppX deployment errors in the window → the ESP Apps
                // failure most likely came from the Win32/IME pipeline after all.
                analysis.Verdict = VerdictNoCandidates;
                analysis.Confidence = scan.Truncated ? "low" : "medium";
            }

            return analysis;
        }

        /// <summary>
        /// Extracts normalized HRESULTs (lower-case 0x-hex) from the rendered message and from
        /// raw property values, including the negative-decimal form event properties often use
        /// (e.g. <c>-2147009543</c> → <c>0x80073cf9</c>).
        /// </summary>
        internal static List<string> ExtractHresults(AppxLogRecord record)
        {
            var result = new List<string>();

            void AddNormalized(string hex)
            {
                var normalized = "0x" + hex.Substring(2).ToLowerInvariant();
                if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    result.Add(normalized);
            }

            if (!string.IsNullOrEmpty(record.Message))
            {
                foreach (Match m in HResultPattern.Matches(record.Message))
                    AddNormalized(m.Value);
            }

            foreach (var prop in record.PropertyStrings)
            {
                foreach (Match m in HResultPattern.Matches(prop))
                    AddNormalized(m.Value);

                // Whole-property negative decimal → HRESULT (high bit always set for negatives).
                if (prop.Length <= 12 && prop.StartsWith("-", StringComparison.Ordinal)
                    && int.TryParse(prop, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec)
                    && dec < 0)
                {
                    AddNormalized("0x" + ((uint)dec).ToString("x8", CultureInfo.InvariantCulture));
                }
            }

            return result;
        }

        /// <summary>Union of PackageFullNames found in message and property strings.</summary>
        internal static List<string> ExtractPackageFullNames(AppxLogRecord record)
        {
            var result = new List<string>();

            void Scan(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                foreach (Match m in PackageFullNamePattern.Matches(text))
                {
                    if (!result.Contains(m.Value, StringComparer.OrdinalIgnoreCase))
                        result.Add(m.Value);
                }
            }

            Scan(record.Message);
            foreach (var prop in record.PropertyStrings)
                Scan(prop);

            return result;
        }

        private static string BuildExcerpt(AppxLogRecord record)
        {
            var text = !string.IsNullOrEmpty(record.Message)
                ? record.Message
                : string.Join("; ", record.PropertyStrings);
            if (string.IsNullOrEmpty(text))
                return $"EventID {record.EventId}";
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= MessageExcerptLength ? text : text.Substring(0, MessageExcerptLength);
        }
    }
}
