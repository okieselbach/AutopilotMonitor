#nullable enable
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
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// Measurement telemetry of the OOBE update page (D-310): <c>oobe_update_page</c> per
    /// update-page record and <c>oobe_update_state</c> for the registry behind it — Debug, bounded
    /// per run, the state only when a key holds values and the content changed.
    /// </summary>
    public sealed class OobeUpdateTelemetryTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink = new FakeSignalIngressSink();
        private IReadOnlyList<OobeUpdateRegistrySnapshot.KeyState> _registry = Array.Empty<OobeUpdateRegistrySnapshot.KeyState>();

        public void Dispose() => _tmp.Dispose();

        private OobeUpdateTelemetry Build(Func<IReadOnlyList<OobeUpdateRegistrySnapshot.KeyState>>? reader = null) =>
            new OobeUpdateTelemetry("sess-oobe", "tenant-oobe",
                new InformationalEventPost(_sink, new VirtualClock(At)),
                new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                reader ?? (() => _registry));

        private IReadOnlyList<FakeSignalIngressSink.PostedSignal> ByType(string eventType) =>
            _sink.Posted.Where(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == eventType).ToList();

        private static IReadOnlyDictionary<string, object> Data(FakeSignalIngressSink.PostedSignal posted) =>
            (IReadOnlyDictionary<string, object>)posted.TypedPayload!;

        private static OobeUpdateRegistrySnapshot.KeyState Key(string alias, string? line) =>
            line == null
                ? new OobeUpdateRegistrySnapshot.KeyState(alias, $@"SOFTWARE\{alias}", exists: false, hasValues: false, line: null)
                : new OobeUpdateRegistrySnapshot.KeyState(alias, $@"SOFTWARE\{alias}", exists: true, hasValues: true, line: line);

        // ------------------------------------------------------------- oobe_update_page ----

        [Fact]
        public void Page_IsReported_WithItsEventTime_AndFields()
        {
            Build().ReportPage(
                new OobeUpdatePageRecord(OobeUpdatePageRecord.PageStopped, 62405, page: "OobeNDUP", result: "success"),
                At.AddMinutes(-3), isBackfill: true);

            var page = Assert.Single(ByType(Constants.EventTypes.OobeUpdatePage));
            Assert.Equal(At.AddMinutes(-3), page.OccurredAtUtc);
            Assert.Equal("Debug", page.Payload![SignalPayloadKeys.Severity]);
            var data = Data(page);
            Assert.Equal("page_stopped", data["cxhEvent"]);
            Assert.Equal("OobeNDUP", data["page"]);
            Assert.Equal("success", data["result"]);
            Assert.Equal(true, data["backfill"]);
            Assert.Equal(1, data["occurrence"]);
            Assert.False(data.ContainsKey("value"));
        }

        [Fact]
        public void EventName_CarriesItsSafeValue()
        {
            Build().ReportPage(
                new OobeUpdatePageRecord(OobeUpdatePageRecord.EventName, 62407, name: "ExpeditedUpdate_isNDUPAllowedByCSPSucceeded", value: "true"),
                At, isBackfill: false);

            var data = Data(Assert.Single(ByType(Constants.EventTypes.OobeUpdatePage)));
            Assert.Equal("ExpeditedUpdate_isNDUPAllowedByCSPSucceeded", data["name"]);
            Assert.Equal("true", data["value"]);
        }

        [Fact]
        public void SamePageOrName_IsReportedAtMostThreeTimes()
        {
            var telemetry = Build();
            for (var i = 0; i < 10; i++)
            {
                telemetry.ReportPage(new OobeUpdatePageRecord(OobeUpdatePageRecord.EventName, 62406, name: "ExpeditedUpdate_progress"), At.AddSeconds(i), isBackfill: false);
            }
            telemetry.ReportPage(new OobeUpdatePageRecord(OobeUpdatePageRecord.PageStopped, 62405, page: "OobeNDUP", result: "success"), At.AddMinutes(1), isBackfill: false);

            var pages = ByType(Constants.EventTypes.OobeUpdatePage);
            Assert.Equal(OobeUpdateTelemetry.MaxPageEventsPerKey + 1, pages.Count);
            Assert.Equal(new object[] { 1, 2, 3 }, pages.Take(3).Select(p => Data(p)["occurrence"]).ToArray());
            // The repeats do not crowd out the page end.
            Assert.Equal("page_stopped", Data(pages.Last())["cxhEvent"]);
        }

        [Fact]
        public void PageEvents_AreCappedPerRun_AndTheCapIsMarkedOnce()
        {
            var telemetry = Build();
            for (var i = 0; i < OobeUpdateTelemetry.MaxPageEventsPerRun + 20; i++)
            {
                telemetry.ReportPage(new OobeUpdatePageRecord(OobeUpdatePageRecord.EventName, 62406, name: $"ExpeditedUpdate_step{i}"), At.AddSeconds(i), isBackfill: false);
            }

            var pages = ByType(Constants.EventTypes.OobeUpdatePage);
            Assert.Equal(OobeUpdateTelemetry.MaxPageEventsPerRun + 1, pages.Count);
            // A name missing after the marker proves nothing — the rules check for it.
            var marker = Data(pages.Last());
            Assert.Equal(OobeUpdatePageRecord.NamesCapped, marker["cxhEvent"]);
            Assert.Equal(false, marker["backfill"]);
            Assert.Equal(OobeUpdateTelemetry.MaxPageEventsPerRun, marker["limit"]);
            Assert.Equal(At.AddSeconds(OobeUpdateTelemetry.MaxPageEventsPerRun), pages.Last().OccurredAtUtc); // the first name over the budget
        }

        [Fact]
        public void BackfilledNames_HaveTheirOwnBudget()
        {
            // The records from before the agent started cannot spend the live page's budget.
            var telemetry = Build();
            for (var i = 0; i < OobeUpdateTelemetry.MaxBackfillPageEventsPerRun + 5; i++)
            {
                telemetry.ReportPage(new OobeUpdatePageRecord(OobeUpdatePageRecord.EventName, 62406, name: $"ExpeditedUpdate_old{i}"), At.AddMinutes(-30), isBackfill: true);
            }

            Assert.True(telemetry.ReportPage(new OobeUpdatePageRecord(OobeUpdatePageRecord.EventName, 62406, name: "ExpeditedUpdate_live"), At, isBackfill: false));

            var markers = ByType(Constants.EventTypes.OobeUpdatePage)
                .Where(p => (string)Data(p)["cxhEvent"] == OobeUpdatePageRecord.NamesCapped).ToList();
            var marker = Assert.Single(markers);
            Assert.Equal(true, Data(marker)["backfill"]);
            Assert.Equal(OobeUpdateTelemetry.MaxBackfillPageEventsPerRun, Data(marker)["limit"]);
        }

        [Fact]
        public void PageStartAndStop_AreReported_AfterTheNameBudgetIsSpent()
        {
            // The time attribution reads the update's span from the page start and stop.
            var telemetry = Build();
            for (var i = 0; i < OobeUpdateTelemetry.MaxPageEventsPerRun; i++)
            {
                Assert.True(telemetry.ReportPage(new OobeUpdatePageRecord(OobeUpdatePageRecord.EventName, 62406, name: $"ExpeditedUpdate_step{i}"), At, isBackfill: false));
            }

            Assert.False(telemetry.ReportPage(new OobeUpdatePageRecord(OobeUpdatePageRecord.EventName, 62406, name: "ExpeditedUpdate_late"), At, isBackfill: false));
            Assert.False(telemetry.ReportPage(new OobeUpdatePageRecord(OobeUpdatePageRecord.EventName, 62406, name: "ExpeditedUpdate_later"), At, isBackfill: false));
            Assert.True(telemetry.ReportPage(new OobeUpdatePageRecord(OobeUpdatePageRecord.PageStarted, 62404, page: "RebootNDUP"), At.AddMinutes(1), isBackfill: false));
            Assert.True(telemetry.ReportPage(new OobeUpdatePageRecord(OobeUpdatePageRecord.PageStopped, 62405, page: "RebootNDUP", result: "success"), At.AddMinutes(2), isBackfill: false));

            var pages = ByType(Constants.EventTypes.OobeUpdatePage);
            Assert.Equal(OobeUpdateTelemetry.MaxPageEventsPerRun + 3, pages.Count);
            Assert.Equal(new object[] { OobeUpdatePageRecord.NamesCapped, "page_started", "page_stopped" },
                pages.Skip(OobeUpdateTelemetry.MaxPageEventsPerRun).Select(p => Data(p)["cxhEvent"]).ToArray());
        }

        [Fact]
        public void SamePageStart_IsReportedAtMostThreeTimes()
        {
            // Outside the run budget, but a page that restarts in a loop still cannot flood.
            var telemetry = Build();
            var reported = Enumerable.Range(0, 10)
                .Count(i => telemetry.ReportPage(new OobeUpdatePageRecord(OobeUpdatePageRecord.PageStarted, 62404, page: "OobeNDUP"), At.AddMinutes(i), isBackfill: false));

            Assert.Equal(OobeUpdateTelemetry.MaxPageEventsPerKey, reported);
        }

        // ------------------------------------------------------------ oobe_update_state ----

        [Fact]
        public void State_WithoutValues_IsNotReported()
        {
            _registry = new[] { Key("setupOobeNdup", null), Key("policiesOobe", null) };

            Build().ReportState(OobeUpdateTelemetry.Moments.AgentStart);

            Assert.Empty(ByType(Constants.EventTypes.OobeUpdateState));
        }

        [Fact]
        public void State_ReportsOnlyKeysWithValues_UnderTheirAlias()
        {
            _registry = new[]
            {
                Key("setupOobeNdup", "EnableExpeditedUpdate=1, EnableExpeditedUpdateSyncDownloadCompleted=1"),
                Key("ndup", null),
                new OobeUpdateRegistrySnapshot.KeyState("policyManagerSystem", "x", exists: true, hasValues: false, line: "(no OOBE values)"),
                Key("espSetupPolicy", "InstallQualityUpdates=1"),
            };

            Build().ReportState(OobeUpdateTelemetry.Moments.UpdatePageStopped, "OobeNDUP");

            var state = Assert.Single(ByType(Constants.EventTypes.OobeUpdateState));
            Assert.Equal("Debug", state.Payload![SignalPayloadKeys.Severity]);
            var data = Data(state);
            Assert.Equal("update_page_stopped", data["moment"]);
            Assert.Equal("OobeNDUP", data["page"]);
            Assert.Equal(2, data["keysWithValues"]);
            Assert.Equal("EnableExpeditedUpdate=1, EnableExpeditedUpdateSyncDownloadCompleted=1", data["setupOobeNdup"]);
            Assert.Equal("InstallQualityUpdates=1", data["espSetupPolicy"]);
            Assert.False(data.ContainsKey("ndup"));
            Assert.False(data.ContainsKey("policyManagerSystem"));
        }

        [Fact]
        public void UnchangedState_IsReportedOnce_AChangeAgain()
        {
            var telemetry = Build();
            _registry = new[] { Key("espSetupPolicy", "InstallQualityUpdates=1") };

            telemetry.ReportState(OobeUpdateTelemetry.Moments.AgentStart);
            telemetry.ReportState(OobeUpdateTelemetry.Moments.UpdatePageStarted, "OobeNDUP");
            _registry = new[] { Key("espSetupPolicy", "InstallQualityUpdates=1"), Key("setupOobeNdup", "EnableExpeditedUpdate=1") };
            telemetry.ReportState(OobeUpdateTelemetry.Moments.UpdatePageStopped, "OobeNDUP");
            telemetry.ReportState(OobeUpdateTelemetry.Moments.AgentStop);

            var states = ByType(Constants.EventTypes.OobeUpdateState);
            Assert.Equal(new object[] { "agent_start", "update_page_stopped" }, states.Select(s => Data(s)["moment"]).ToArray());
        }

        [Fact]
        public void StateEvents_AreCappedPerRun()
        {
            var telemetry = Build();
            for (var i = 0; i < OobeUpdateTelemetry.MaxStateEventsPerRun + 4; i++)
            {
                _registry = new[] { Key("setupOobeNdup", $"ExpeditedUpdateStatus={i}") };
                telemetry.ReportState(OobeUpdateTelemetry.Moments.UpdatePageStarted, "OobeNDUP");
            }

            Assert.Equal(OobeUpdateTelemetry.MaxStateEventsPerRun, ByType(Constants.EventTypes.OobeUpdateState).Count);
        }

        [Fact]
        public void LongLines_AreTruncated()
        {
            _registry = new[] { Key("ndupUpdates", new string('x', 3000)) };

            Build().ReportState(OobeUpdateTelemetry.Moments.AgentStart);

            var line = (string)Data(Assert.Single(ByType(Constants.EventTypes.OobeUpdateState)))["ndupUpdates"];
            Assert.Equal(OobeUpdateTelemetry.MaxStateLineLength + 1, line.Length);
            Assert.EndsWith("…", line);
        }

        [Fact]
        public void FailingRegistryRead_ReportsNothing_AndDoesNotThrow()
        {
            Build(() => throw new UnauthorizedAccessException("denied")).ReportState(OobeUpdateTelemetry.Moments.AgentStart);

            Assert.Empty(_sink.Posted);
        }
    }
}
