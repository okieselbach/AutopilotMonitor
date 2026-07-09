using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// Test double for the settle-window AppX enrichment scan. Returns a canned result and
    /// records every request — tests never touch the real event log (project rule: mock ALL
    /// system calls).
    /// </summary>
    internal sealed class FakeAppxDeploymentFailureScanner : IAppxDeploymentFailureScanner
    {
        public AppxFailureScanResult Result { get; set; } = new AppxFailureScanResult();
        public Exception? ThrowOnScan { get; set; }
        public List<AppxFailureScanRequest> Requests { get; } = new List<AppxFailureScanRequest>();

        public AppxFailureScanResult Scan(AppxFailureScanRequest request)
        {
            Requests.Add(request);
            if (ThrowOnScan != null) throw ThrowOnScan;
            return Result;
        }
    }

    /// <summary>
    /// Session 2bc884b6 coverage: an ESP Apps-subcategory failure (e.g. 0x80073cf9 from an
    /// MSIX/Store app invisible to ImeLogTracker) must trigger exactly one AppX event-log scan
    /// inside the settle window and emit <c>esp_appx_failure_analysis</c> with flat RuleEngine
    /// fields — without disturbing the settle window's own EspFailureDetected flow.
    /// </summary>
    public sealed class AppxFailureScanTriggerTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 7, 9, 12, 48, 57, DateTimeKind.Utc);

        private const string InProgressJson = @"{
            ""categorySucceeded"": null,
            ""AppsSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Apps (Working)""}
        }";
        private const string AppsFailedJson = @"{
            ""categorySucceeded"": null,
            ""AppsSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Apps (0x80073cf9)""}
        }";

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public FakeSignalIngressSink Sink { get; } = new FakeSignalIngressSink();
            public FakeAppxDeploymentFailureScanner Scanner { get; } = new FakeAppxDeploymentFailureScanner();
            public List<EspFailureDetectedEventArgs> EspFailures { get; } = new List<EspFailureDetectedEventArgs>();
            public ProvisioningStatusTracker Tracker { get; }

            public Fixture()
            {
                var clock = new VirtualClock(Fixed);
                var post = new InformationalEventPost(Sink, clock);
                var logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Tracker = new ProvisioningStatusTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: post,
                    logger: logger,
                    appxScanner: Scanner,
                    backgroundDispatcher: action => action()); // synchronous — deterministic tests
                Tracker.EspFailureDetected += (_, args) => EspFailures.Add(args);
            }

            public IReadOnlyList<(string EventType, string Severity, IReadOnlyDictionary<string, object> Data)> CapturedEvents()
            {
                return Sink.Posted
                    .Where(p => p.Kind == DecisionSignalKind.InformationalEvent && p.Payload != null)
                    .Select(p =>
                    {
                        var eventType = p.Payload!.TryGetValue(SignalPayloadKeys.EventType, out var et) ? et : "";
                        var severity = p.Payload!.TryGetValue(SignalPayloadKeys.Severity, out var sev) ? sev : "";
                        var data = p.TypedPayload as IReadOnlyDictionary<string, object>
                                   ?? (IReadOnlyDictionary<string, object>)new Dictionary<string, object>();
                        return (eventType, severity, data);
                    })
                    .ToList();
            }

            public (string EventType, string Severity, IReadOnlyDictionary<string, object> Data) AnalysisEvent()
                => CapturedEvents().SingleOrDefault(e => e.EventType == "esp_appx_failure_analysis");

            public void Dispose()
            {
                Tracker.Dispose();
                Tmp.Dispose();
            }
        }

        private static AppxLogRecord FailedPackageRecord(string packageFullName, string hresult)
            => new AppxLogRecord
            {
                TimeCreatedUtc = Fixed.AddMinutes(-2),
                EventId = 401,
                Level = 2,
                Message = $"Deployment operation on {packageFullName} failed with error {hresult}.",
            };

        [Fact]
        public void AppsFailure_TriggersScan_WithEspContext_AndEmitsFlatFields()
        {
            using var f = new Fixture();
            f.Scanner.Result = new AppxFailureScanResult
            {
                Records = { FailedPackageRecord("Contoso.LineOfBusiness_1.2.3.4_x64__8wekyb3d8bbwe", "0x80073CF9") },
                ScanDurationMs = 42
            };

            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", InProgressJson);
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", AppsFailedJson);

            var request = Assert.Single(f.Scanner.Requests);
            Assert.Equal("0x80073cf9", request.EspErrorCode);
            Assert.Equal("AccountSetup", request.EspCategory);
            Assert.Equal("Apps", request.FailedSubcategory);
            Assert.True(request.WindowStartUtc < request.WindowEndUtc);

            var analysis = f.AnalysisEvent();
            Assert.NotEqual(default, analysis);
            Assert.Equal("Warning", analysis.Severity);
            Assert.Equal("appx_candidate_identified", (string)analysis.Data["verdict"]);
            Assert.Equal("high", (string)analysis.Data["confidence"]);
            Assert.Equal("0x80073cf9", (string)analysis.Data["espErrorCode"]);
            Assert.Equal("AccountSetup", (string)analysis.Data["espCategory"]);
            Assert.Equal("Contoso.LineOfBusiness_1.2.3.4_x64__8wekyb3d8bbwe", (string)analysis.Data["topCandidatePackage"]);
            Assert.Equal("Contoso.LineOfBusiness", (string)analysis.Data["topCandidatePackageName"]);
            Assert.Equal("0x80073cf9", (string)analysis.Data["topCandidateHresult"]);
            Assert.Equal("hresult_match", (string)analysis.Data["topCandidateMatchType"]);
            Assert.Equal(1, (int)analysis.Data["candidateCount"]);
            Assert.True(analysis.Data.ContainsKey("candidates"));
        }

        [Fact]
        public void EnrichmentPrecedesSettleExpiry_AndSettleStillFires()
        {
            using var f = new Fixture();

            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", InProgressJson);
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", AppsFailedJson);

            // Analysis event is already emitted while the settle window is still pending.
            Assert.NotEqual(default, f.AnalysisEvent());
            Assert.Empty(f.EspFailures);

            f.Tracker.TriggerSettleTimerForTest("AccountSetupCategory.Status");

            var fired = Assert.Single(f.EspFailures);
            Assert.Equal("Provisioning_AccountSetup_Apps_Failed", fired.FailureType);
            Assert.Equal("0x80073cf9", fired.ErrorCode);
        }

        [Fact]
        public void NonAppsFailure_DoesNotScan()
        {
            using var f = new Fixture();

            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""SecurityPoliciesSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Working""}
            }");
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""SecurityPoliciesSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Failed (0x800705b4)""}
            }");

            Assert.Empty(f.Scanner.Requests);
            Assert.Equal(default, f.AnalysisEvent());
        }

        [Fact]
        public void SecondAppsFailure_InOtherCategory_DoesNotRescan()
        {
            using var f = new Fixture();

            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", InProgressJson);
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", AppsFailedJson);
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", InProgressJson);
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", AppsFailedJson);

            // Both categories arm their own settle window, but the scan is fire-once.
            Assert.Single(f.Scanner.Requests);
            Assert.Equal("DeviceSetup", f.Scanner.Requests[0].EspCategory);
        }

        [Fact]
        public void ScannerThrows_EmitsCollectorDegraded_SettleWindowUnaffected()
        {
            using var f = new Fixture();
            f.Scanner.ThrowOnScan = new InvalidOperationException("boom");

            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", InProgressJson);
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", AppsFailedJson);

            var degraded = f.CapturedEvents().SingleOrDefault(e => e.EventType == "collector_degraded");
            Assert.NotEqual(default, degraded);
            Assert.Equal("AppxDeploymentFailureScanner", (string)degraded.Data["collector"]);
            Assert.Equal("scan_failed", (string)degraded.Data["reason"]);
            Assert.Equal(default, f.AnalysisEvent());

            f.Tracker.TriggerSettleTimerForTest("AccountSetupCategory.Status");
            Assert.Single(f.EspFailures);
        }

        [Fact]
        public void NoCandidates_EmitsInfoVerdict_NegativeEvidence()
        {
            using var f = new Fixture();
            // Empty scan result — no AppX errors in the window.

            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", InProgressJson);
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", AppsFailedJson);

            var analysis = f.AnalysisEvent();
            Assert.NotEqual(default, analysis);
            Assert.Equal("Info", analysis.Severity);
            Assert.Equal("no_appx_candidates", (string)analysis.Data["verdict"]);
            Assert.Equal(0, (int)analysis.Data["candidateCount"]);
            Assert.False(analysis.Data.ContainsKey("topCandidatePackage"));
        }

        [Fact]
        public void AppsFailureWithoutHresult_OmitsEspErrorCodeKey()
        {
            using var f = new Fixture();

            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", InProgressJson);
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Apps (Error)""}
            }");

            var request = Assert.Single(f.Scanner.Requests);
            Assert.Null(request.EspErrorCode);

            var analysis = f.AnalysisEvent();
            Assert.NotEqual(default, analysis);
            Assert.False(analysis.Data.ContainsKey("espErrorCode"));
        }
    }
}
