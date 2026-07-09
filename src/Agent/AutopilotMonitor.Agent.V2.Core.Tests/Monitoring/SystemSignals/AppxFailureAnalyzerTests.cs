using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// Pure scoring/verdict coverage for the session-2bc884b6 AppX enrichment. No event log,
    /// no tracker — records go in, scored candidates and an honest verdict come out.
    /// </summary>
    public sealed class AppxFailureAnalyzerTests
    {
        private static readonly DateTime FailureAt = new DateTime(2026, 7, 9, 12, 48, 57, DateTimeKind.Utc);
        private static readonly DateTime WindowStart = FailureAt.AddMinutes(-30);

        private const string PkgA = "Contoso.LineOfBusiness_1.2.3.4_x64__8wekyb3d8bbwe";
        private const string PkgB = "Fabrikam.Tools_2.0.0.0_neutral__abcdefghjkmnp";

        private static AppxFailureScanRequest Request(string? espErrorCode = "0x80073cf9")
            => new AppxFailureScanRequest(WindowStart, FailureAt, espErrorCode, "AccountSetup", "Apps");

        private static AppxLogRecord Record(
            string? message,
            DateTime? timeUtc = null,
            int eventId = 401,
            params string[] properties)
        {
            return new AppxLogRecord
            {
                TimeCreatedUtc = timeUtc ?? FailureAt.AddMinutes(-1),
                EventId = eventId,
                Level = 2,
                Message = message,
                PropertyStrings = properties.ToList()
            };
        }

        private static AppxFailureScanResult ScanWith(params AppxLogRecord[] records)
            => new AppxFailureScanResult { Records = records.ToList() };

        // ------------------------------------------------------------------
        // Scoring
        // ------------------------------------------------------------------

        [Fact]
        public void HresultMatch_OutranksTimeProximity()
        {
            // PkgB failed earlier with the exact ESP HRESULT; PkgA has a recent error without one.
            var scan = ScanWith(
                Record($"Deployment of package {PkgA} was blocked.", FailureAt.AddMinutes(-1)),
                Record($"Deployment operation on {PkgB} failed with error 0x80073CF9.", FailureAt.AddMinutes(-20)));

            var analysis = AppxFailureAnalyzer.Analyze(Request(), scan);

            Assert.Equal(PkgB, analysis.Candidates[0].PackageFullName);
            Assert.Equal(AppxFailureAnalyzer.MatchTypeHresultMatch, analysis.Candidates[0].MatchType);
            Assert.Equal(AppxFailureAnalyzer.MatchTypeTimeProximity, analysis.Candidates[1].MatchType);
            Assert.True(analysis.Candidates[0].Score > analysis.Candidates[1].Score);
        }

        [Fact]
        public void RecencyBonus_AppliedWithinTenMinutesOfFailure()
        {
            var recent = ScanWith(Record($"error 0x80073cf9 for {PkgA}", FailureAt.AddMinutes(-2)));
            var old = ScanWith(Record($"error 0x80073cf9 for {PkgA}", FailureAt.AddMinutes(-25)));

            var recentScore = AppxFailureAnalyzer.Analyze(Request(), recent).Candidates[0].Score;
            var oldScore = AppxFailureAnalyzer.Analyze(Request(), old).Candidates[0].Score;

            Assert.Equal(AppxFailureAnalyzer.ScoreHresultMatch + AppxFailureAnalyzer.ScoreRecencyBonus, recentScore);
            Assert.Equal(AppxFailureAnalyzer.ScoreHresultMatch, oldScore);
        }

        // ------------------------------------------------------------------
        // Extraction
        // ------------------------------------------------------------------

        [Fact]
        public void PackageExtraction_FromMessage()
        {
            var packages = AppxFailureAnalyzer.ExtractPackageFullNames(
                Record($"AppX Deployment operation failed for package {PkgA} with error 0x80073CF9."));
            Assert.Equal(new[] { PkgA }, packages);
        }

        [Fact]
        public void PackageExtraction_FromProperties_WhenFormatDescriptionFailed()
        {
            // Under SYSTEM/OOBE FormatDescription often throws → Message null; the raw event
            // properties still carry the PackageFullName.
            var record = Record(message: null, properties: new[] { PkgB, "some other property" });
            var packages = AppxFailureAnalyzer.ExtractPackageFullNames(record);
            Assert.Equal(new[] { PkgB }, packages);
        }

        [Fact]
        public void NegativeDecimalProperty_ConvertsToHresult()
        {
            // -2147009287 == unchecked((int)0x80073CF9) — the form event properties use.
            var record = Record(message: null, properties: new[] { "-2147009287" });
            var hresults = AppxFailureAnalyzer.ExtractHresults(record);
            Assert.Equal(new[] { "0x80073cf9" }, hresults);
        }

        [Fact]
        public void HresultExtraction_NormalizesToLowercase_AndDedupes()
        {
            var record = Record($"error 0x80073CF9 then again 0x80073cf9", properties: new[] { "-2147009287" });
            var hresults = AppxFailureAnalyzer.ExtractHresults(record);
            Assert.Equal(new[] { "0x80073cf9" }, hresults);
        }

        // ------------------------------------------------------------------
        // Aggregation
        // ------------------------------------------------------------------

        [Fact]
        public void PerPackageDedupe_CountsOccurrences_KeepsLatestTimestamp()
        {
            var scan = ScanWith(
                Record($"retry 1 failed for {PkgA} 0x80073cf9", FailureAt.AddMinutes(-9)),
                Record($"retry 2 failed for {PkgA} 0x80073cf9", FailureAt.AddMinutes(-5)),
                Record($"retry 3 failed for {PkgA} 0x80073cf9", FailureAt.AddMinutes(-7)));

            var analysis = AppxFailureAnalyzer.Analyze(Request(), scan);

            var candidate = Assert.Single(analysis.Candidates);
            Assert.Equal(3, candidate.Occurrences);
            Assert.Equal(FailureAt.AddMinutes(-5), candidate.TimeUtc);
            Assert.Equal(1, analysis.TotalCandidateCount);
        }

        [Fact]
        public void CandidatesCappedAtFive_TotalCountPreserved()
        {
            var records = Enumerable.Range(1, 7)
                .Select(i => Record($"failed Vendor{i}.App{i}_1.0.0.{i}_x64__8wekyb3d8bbwe with 0x80073cf9"))
                .ToArray();

            var analysis = AppxFailureAnalyzer.Analyze(Request(), ScanWith(records));

            Assert.Equal(AppxFailureAnalyzer.MaxCandidatesInPayload, analysis.Candidates.Count);
            Assert.Equal(7, analysis.TotalCandidateCount);
        }

        [Fact]
        public void MessageExcerpt_Truncated_AndNewlinesFlattened()
        {
            var longMessage = $"failure of {PkgA}\r\nline2 " + new string('x', 500);
            var analysis = AppxFailureAnalyzer.Analyze(Request(null), ScanWith(Record(longMessage)));

            var excerpt = analysis.Candidates[0].MessageExcerpt;
            Assert.Equal(AppxFailureAnalyzer.MessageExcerptLength, excerpt.Length);
            Assert.DoesNotContain("\n", excerpt);
        }

        // ------------------------------------------------------------------
        // Verdicts / confidence
        // ------------------------------------------------------------------

        [Fact]
        public void Verdict_CandidateIdentified_HighConfidence_WhenEspCodeIsAppxFamily()
        {
            var analysis = AppxFailureAnalyzer.Analyze(
                Request("0x80073cf9"),
                ScanWith(Record($"deployment of {PkgA} failed with 0x80073CF9")));

            Assert.Equal(AppxFailureAnalyzer.VerdictCandidateIdentified, analysis.Verdict);
            Assert.Equal("high", analysis.Confidence);
        }

        [Fact]
        public void Verdict_CandidateIdentified_MediumConfidence_WhenEspCodeNotAppxFamily()
        {
            var analysis = AppxFailureAnalyzer.Analyze(
                Request("0x87d1041c"),
                ScanWith(Record($"deployment of {PkgA} failed with 0x87d1041c")));

            Assert.Equal(AppxFailureAnalyzer.VerdictCandidateIdentified, analysis.Verdict);
            Assert.Equal("medium", analysis.Confidence);
        }

        [Fact]
        public void Verdict_ActivityNoHresultMatch_WhenErrorsDontMatchEspCode()
        {
            var analysis = AppxFailureAnalyzer.Analyze(
                Request("0x80073cf9"),
                ScanWith(Record($"deployment of {PkgA} failed with 0x80073d02")));

            Assert.Equal(AppxFailureAnalyzer.VerdictActivityNoHresultMatch, analysis.Verdict);
            Assert.Equal("low", analysis.Confidence);
            Assert.Contains("0x80073d02", analysis.OtherHresultsSeen);
        }

        [Fact]
        public void Verdict_ActivityNoHresultMatch_MediumConfidence_WhenEspCodeNull_AndFamilyErrorsSeen()
        {
            var analysis = AppxFailureAnalyzer.Analyze(
                Request(espErrorCode: null),
                ScanWith(Record($"deployment of {PkgA} failed with 0x80073cff")));

            Assert.Equal(AppxFailureAnalyzer.VerdictActivityNoHresultMatch, analysis.Verdict);
            Assert.Equal("medium", analysis.Confidence);
        }

        [Fact]
        public void Verdict_ErrorsNoPackage_WhenHresultsButNoPackageName()
        {
            var analysis = AppxFailureAnalyzer.Analyze(
                Request(),
                ScanWith(Record("operation failed with 0x80073cf9 (no package in text)")));

            Assert.Equal(AppxFailureAnalyzer.VerdictErrorsNoPackage, analysis.Verdict);
            Assert.Empty(analysis.Candidates);
        }

        [Fact]
        public void Verdict_NoCandidates_WhenWindowEmpty()
        {
            var analysis = AppxFailureAnalyzer.Analyze(Request(), ScanWith());

            Assert.Equal(AppxFailureAnalyzer.VerdictNoCandidates, analysis.Verdict);
            Assert.Equal("medium", analysis.Confidence);
            Assert.Equal(0, analysis.ErrorEventCount);
        }

        [Fact]
        public void Verdict_NoCandidates_LowConfidence_WhenScanTruncated()
        {
            var scan = ScanWith();
            scan.Truncated = true;
            var analysis = AppxFailureAnalyzer.Analyze(Request(), scan);

            Assert.Equal(AppxFailureAnalyzer.VerdictNoCandidates, analysis.Verdict);
            Assert.Equal("low", analysis.Confidence);
        }

        [Fact]
        public void Verdict_ScanUnavailable_WhenChannelMissing()
        {
            var scan = new AppxFailureScanResult { ChannelAvailable = false, UnavailableReason = "channel_not_found" };
            var analysis = AppxFailureAnalyzer.Analyze(Request(), scan);

            Assert.Equal(AppxFailureAnalyzer.VerdictScanUnavailable, analysis.Verdict);
            Assert.Equal("channel_not_found", analysis.ScanUnavailableReason);
        }

        [Theory]
        [InlineData("0x80073cf9", true)]
        [InlineData("0x80073CF0", true)]
        [InlineData("0x80073d02", true)]
        [InlineData("0x87d1041c", false)]
        [InlineData("0x80070002", false)]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("0x8007", false)]
        public void IsAppxFamilyHresult_MatchesOnlyAppxDeploymentRange(string hresult, bool expected)
        {
            Assert.Equal(expected, AppxFailureAnalyzer.IsAppxFamilyHresult(hresult));
        }
    }
}
