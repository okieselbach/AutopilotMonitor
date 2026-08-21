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
    /// SystemTimelineTracker — clock steps (Kernel-General 1) and completed sleep episodes
    /// (Power-Troubleshooter 1 / Kernel-Power 507) from the System channel. Drives the primitive
    /// <see cref="SystemTimelineTracker.ProcessEvent"/> test seam (no real EventRecord, which is
    /// abstract + Windows-only), mirroring the WindowsUpdateTracker test pattern.
    /// </summary>
    public sealed class SystemTimelineTrackerTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink;
        private readonly SystemTimelineTracker _tracker;
        private long _nextRecordId = 1;

        public SystemTimelineTrackerTests()
        {
            _sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(_sink, new VirtualClock(At));
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            _tracker = new SystemTimelineTracker(
                sessionId: "sess-stl",
                tenantId: "tenant-stl",
                post: post,
                logger: logger,
                backfillEnabled: false,
                stateDirectory: null); // in-memory dedup for most tests
        }

        public void Dispose() => _tmp.Dispose();

        // -------------------------------------------------------------------
        // Emission helpers
        // -------------------------------------------------------------------

        private void EmitClockChange(
            DateTime? oldTime, DateTime? newTime, string? rawDeltaMs = null,
            int reason = 1, long? recordId = null, bool isBackfill = false, DateTime? timeCreatedUtc = null)
        {
            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (oldTime.HasValue) data["OldTime"] = oldTime.Value.ToString("o");
            if (newTime.HasValue) data["NewTime"] = newTime.Value.ToString("o");
            if (rawDeltaMs != null) data["TimeDeltaInMs"] = rawDeltaMs;
            data["Reason"] = reason.ToString();
            data["ProcessName"] = @"\Device\HarddiskVolume3\Windows\System32\svchost.exe";
            data["ProcessID"] = "6892";

            _tracker.ProcessEvent(
                providerName: SystemTimelineTracker.ProviderKernelGeneral,
                eventId: SystemTimelineTracker.EventId_ClockChange,
                recordId: recordId ?? _nextRecordId++,
                timeCreatedUtc: timeCreatedUtc ?? At,
                eventData: data,
                isBackfill: isBackfill);
        }

        private void EmitClassicResume(
            DateTime sleepTime, DateTime wakeTime, string? effectiveState = "1",
            string? wakeSourceText = null, long? recordId = null, bool isBackfill = false, DateTime? timeCreatedUtc = null)
        {
            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SleepTime"] = sleepTime.ToString("o"),
                ["WakeTime"] = wakeTime.ToString("o"),
                ["WakeSourceType"] = "0",
            };
            if (effectiveState != null) data["EffectiveState"] = effectiveState;
            if (wakeSourceText != null) data["WakeSourceText"] = wakeSourceText;

            _tracker.ProcessEvent(
                providerName: SystemTimelineTracker.ProviderPowerTroubleshooter,
                eventId: SystemTimelineTracker.EventId_ClassicResume,
                recordId: recordId ?? _nextRecordId++,
                timeCreatedUtc: timeCreatedUtc ?? wakeTime,
                eventData: data,
                isBackfill: isBackfill);
        }

        private void EmitModernStandbyExit(
            long sleepDurationUs, long durationUs, string powerStateAc = "true",
            long? recordId = null, bool isBackfill = false, DateTime? timeCreatedUtc = null)
        {
            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SleepDurationInUs"] = sleepDurationUs.ToString(),
                ["DurationInUs"] = durationUs.ToString(),
                ["Reason"] = "32",
                ["PowerStateAc"] = powerStateAc,
                ["BatteryRemainingCapacityOnExit"] = "34180",
            };

            _tracker.ProcessEvent(
                providerName: SystemTimelineTracker.ProviderKernelPower,
                eventId: SystemTimelineTracker.EventId_ModernStandbyExit,
                recordId: recordId ?? _nextRecordId++,
                timeCreatedUtc: timeCreatedUtc ?? At,
                eventData: data,
                isBackfill: isBackfill);
        }

        private IReadOnlyList<FakeSignalIngressSink.PostedSignal> ByType(string eventType) =>
            _sink.Posted.Where(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == eventType).ToList();

        private static IReadOnlyDictionary<string, object> Data(FakeSignalIngressSink.PostedSignal s) =>
            (IReadOnlyDictionary<string, object>)s.TypedPayload!;

        // -------------------------------------------------------------------
        // system_clock_changed
        // -------------------------------------------------------------------

        [Fact]
        public void ClockStep_AboveThreshold_EmitsInfo_WithSignedDeltaAndNormalizedTimes()
        {
            var oldTime = At;
            var newTime = At.AddSeconds(10);
            EmitClockChange(oldTime, newTime, reason: 1);

            var s = Assert.Single(ByType(Constants.EventTypes.SystemClockChanged));
            Assert.Equal("Info", s.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("false", s.Payload![SignalPayloadKeys.ImmediateUpload]);

            var data = Data(s);
            Assert.Equal(10_000L, data["timeDeltaMs"]);
            Assert.Equal("application_set", data["reasonText"]);
            Assert.Equal(1, data["reason"]);
            Assert.Equal(oldTime.ToString("o"), data["oldTime"]);
            Assert.Equal(newTime.ToString("o"), data["newTime"]);
            Assert.Equal(6892, data["processId"]);
            Assert.Contains("forward", (string)s.Payload![SignalPayloadKeys.Message]);
            Assert.Contains("svchost.exe", (string)s.Payload![SignalPayloadKeys.Message]);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(1999)]
        public void ClockMicroStep_BelowThreshold_Suppressed(int deltaMs)
        {
            EmitClockChange(At, At.AddMilliseconds(deltaMs));
            Assert.Empty(ByType(Constants.EventTypes.SystemClockChanged));
        }

        [Fact]
        public void ClockStep_Backward_EmitsNegativeDelta()
        {
            EmitClockChange(oldTime: At, newTime: At.AddSeconds(-3));

            var s = Assert.Single(ByType(Constants.EventTypes.SystemClockChanged));
            Assert.Equal(-3000L, Data(s)["timeDeltaMs"]);
            Assert.Contains("backward", (string)s.Payload![SignalPayloadKeys.Message]);
        }

        [Fact]
        public void ClockStep_AtLeastFiveMinutes_WarningAndImmediateUpload()
        {
            EmitClockChange(At, At.AddMinutes(5), reason: 2);

            var s = Assert.Single(ByType(Constants.EventTypes.SystemClockChanged));
            Assert.Equal("Warning", s.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("true", s.Payload![SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal("hardware_clock_sync", Data(s)["reasonText"]);
        }

        [Fact]
        public void ClockStep_NoUsableTimesOrDelta_Skipped()
        {
            EmitClockChange(oldTime: null, newTime: null, rawDeltaMs: "not-a-number");
            Assert.Empty(ByType(Constants.EventTypes.SystemClockChanged));
        }

        [Fact]
        public void ClockStep_FallsBackToTimeDeltaInMs_WhenTimesUnparseable()
        {
            // Only the raw delta parses — the direction information of NewTime/OldTime is gone,
            // but the magnitude still gates and emits.
            EmitClockChange(oldTime: null, newTime: null, rawDeltaMs: "7200000");

            var s = Assert.Single(ByType(Constants.EventTypes.SystemClockChanged));
            Assert.Equal(7_200_000L, Data(s)["timeDeltaMs"]);
        }

        // -------------------------------------------------------------------
        // system_sleep_episode — classic (Power-Troubleshooter 1)
        // -------------------------------------------------------------------

        [Fact]
        public void ClassicResume_EffectiveState5_EmitsHibernateEpisode()
        {
            var sleepAt = At.AddHours(-2);
            EmitClassicResume(sleepAt, At, effectiveState: "5", wakeSourceText: "Power Button");

            var s = Assert.Single(ByType(Constants.EventTypes.SystemSleepEpisode));
            Assert.Equal("Info", s.Payload![SignalPayloadKeys.Severity]);

            var data = Data(s);
            Assert.Equal("hibernate", data["kind"]);
            Assert.Equal(7200L, data["durationSeconds"]);
            Assert.Equal(sleepAt.ToString("o"), data["enteredAt"]);
            Assert.Equal(At.ToString("o"), data["exitedAt"]);
            Assert.Equal("Power Button", data["wakeSourceText"]);
            Assert.Contains("hibernate", (string)s.Payload![SignalPayloadKeys.Message]);
        }

        [Fact]
        public void ClassicResume_NonHibernateState_EmitsSleepKind()
        {
            EmitClassicResume(At.AddMinutes(-30), At, effectiveState: "4");
            Assert.Equal("sleep", Data(Assert.Single(ByType(Constants.EventTypes.SystemSleepEpisode)))["kind"]);
        }

        [Fact]
        public void ClassicResume_ShortEpisode_StillEmits()
        {
            // No duration floor for classic sleep: the event only exists for genuinely completed
            // S3/S4 transitions, so a 30 s real sleep is still signal.
            EmitClassicResume(At.AddSeconds(-30), At);

            var s = Assert.Single(ByType(Constants.EventTypes.SystemSleepEpisode));
            Assert.Equal(30L, Data(s)["durationSeconds"]);
        }

        // -------------------------------------------------------------------
        // system_sleep_episode — Modern Standby (Kernel-Power 507)
        // -------------------------------------------------------------------

        [Fact]
        public void ModernStandbyExit_EmitsEpisode_WithDerivedEnterTime()
        {
            // Values from a live 507 on this dev machine: 38 min scenario, ~38 min actual sleep.
            EmitModernStandbyExit(sleepDurationUs: 2_324_095_807, durationUs: 2_330_201_652);

            var s = Assert.Single(ByType(Constants.EventTypes.SystemSleepEpisode));
            var data = Data(s);
            Assert.Equal("modern_standby", data["kind"]);
            Assert.Equal(2330L, data["durationSeconds"]);
            Assert.Equal(2324L, data["sleepDurationSeconds"]);
            Assert.Equal(true, data["onAcPower"]);
            Assert.Equal(34180L, data["batteryRemainingCapacityOnExit"]);
            Assert.Equal(At.ToString("o"), data["exitedAt"]);
            Assert.Equal(At.AddMilliseconds(-2_330_201.652).ToString("o"), data["enteredAt"]);
            Assert.Contains("Modern Standby", (string)s.Payload![SignalPayloadKeys.Message]);
        }

        [Fact]
        public void ModernStandbyExit_SleepBelowFloor_Suppressed()
        {
            EmitModernStandbyExit(sleepDurationUs: 30_000_000, durationUs: 90_000_000); // 30 s sleep
            Assert.Empty(ByType(Constants.EventTypes.SystemSleepEpisode));
        }

        [Fact]
        public void ModernStandbyExit_ScreenOffWithoutSleep_Suppressed()
        {
            // SleepEntered=false scenarios carry SleepDurationInUs 0 — the single duration gate
            // suppresses them without a separate branch.
            EmitModernStandbyExit(sleepDurationUs: 0, durationUs: 6_106_696);
            Assert.Empty(ByType(Constants.EventTypes.SystemSleepEpisode));
        }

        [Fact]
        public void ModernStandbyExit_ScenarioShorterThanSleep_ClampsToSleepDuration()
        {
            EmitModernStandbyExit(sleepDurationUs: 120_000_000, durationUs: 0);

            var data = Data(Assert.Single(ByType(Constants.EventTypes.SystemSleepEpisode)));
            Assert.Equal(120L, data["durationSeconds"]);
            Assert.Equal(120L, data["sleepDurationSeconds"]);
        }

        // -------------------------------------------------------------------
        // Backfill semantics
        // -------------------------------------------------------------------

        [Fact]
        public void BackfilledEvent_UsesEventTimeForTimelineTimestamp_AndFlagsData()
        {
            // A pre-agent standby episode (record created before the agent started) must land on
            // the timeline where the device actually woke, not at agent start.
            var wakeTime = At.AddHours(-3);
            EmitClassicResume(wakeTime.AddHours(-1), wakeTime, isBackfill: true, timeCreatedUtc: wakeTime);

            var s = Assert.Single(ByType(Constants.EventTypes.SystemSleepEpisode));
            Assert.Equal(wakeTime, s.OccurredAtUtc);
            Assert.Equal(true, Data(s)["backfilled"]);
        }

        // -------------------------------------------------------------------
        // Dedup / watermark
        // -------------------------------------------------------------------

        [Fact]
        public void SameRecordId_ProcessedTwice_EmitsOnce()
        {
            EmitClockChange(At, At.AddMinutes(1), recordId: 42);
            EmitClockChange(At, At.AddMinutes(1), recordId: 42);

            Assert.Single(ByType(Constants.EventTypes.SystemClockChanged));
        }

        [Fact]
        public void BackfillEventWithLowerRecordId_AfterLiveEvent_IsStillEmitted()
        {
            // The live watcher is armed BEFORE backfill runs, so a live record with a higher
            // RecordId can be processed first. An older, never-emitted backfill record must NOT be
            // suppressed — pre-agent episodes are the whole point (high-water-mark trap).
            EmitClockChange(At, At.AddMinutes(1), recordId: 100);                       // live, high
            EmitClockChange(At, At.AddMinutes(2), recordId: 50, isBackfill: true);      // backfill, older

            Assert.Equal(2, ByType(Constants.EventTypes.SystemClockChanged).Count);
        }

        [Fact]
        public void Watermark_PersistsAcrossTrackerInstances()
        {
            var post = new InformationalEventPost(_sink, new VirtualClock(At));
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            var clockData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["OldTime"] = At.ToString("o"),
                ["NewTime"] = At.AddMinutes(10).ToString("o"),
                ["Reason"] = "1",
            };

            var first = new SystemTimelineTracker("s", "t", post, logger, backfillEnabled: false, stateDirectory: _tmp.Path);
            first.ProcessEvent(SystemTimelineTracker.ProviderKernelGeneral, SystemTimelineTracker.EventId_ClockChange,
                recordId: 500, timeCreatedUtc: At, eventData: clockData, isBackfill: false);

            // A fresh tracker (agent restart) must load the watermark and skip the already-emitted
            // record when the backfill re-reads it.
            var second = new SystemTimelineTracker("s", "t", post, logger, backfillEnabled: false, stateDirectory: _tmp.Path);
            second.LoadWatermark();
            second.ProcessEvent(SystemTimelineTracker.ProviderKernelGeneral, SystemTimelineTracker.EventId_ClockChange,
                recordId: 500, timeCreatedUtc: At, eventData: clockData, isBackfill: true);

            Assert.Single(ByType(Constants.EventTypes.SystemClockChanged));
        }

        [Fact]
        public void UnknownProviderOrEventId_Ignored()
        {
            _tracker.ProcessEvent("Microsoft-Windows-Kernel-Power", eventId: 566, recordId: _nextRecordId++,
                timeCreatedUtc: At, eventData: new Dictionary<string, string>(), isBackfill: false);
            _tracker.ProcessEvent("Some-Other-Provider", eventId: 1, recordId: _nextRecordId++,
                timeCreatedUtc: At, eventData: new Dictionary<string, string>(), isBackfill: false);

            Assert.Empty(_sink.Posted);
        }

        // -------------------------------------------------------------------
        // XPath + parsing
        // -------------------------------------------------------------------

        [Fact]
        public void BuildXPath_ContainsAllThreeProviderIdPairs_AndNo506()
        {
            var xpath = SystemTimelineTracker.BuildXPath();
            Assert.Contains("Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=1", xpath);
            Assert.Contains("Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1", xpath);
            Assert.Contains("Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=507", xpath);
            Assert.DoesNotContain("506", xpath);
        }

        [Fact]
        public void BuildBackfillXPath_AppendsTimediffClause()
        {
            var xpath = SystemTimelineTracker.BuildBackfillXPath(86_400_000);
            Assert.Contains("TimeCreated[timediff(@SystemTime) <= 86400000]", xpath);
            Assert.Contains("EventID=507", xpath);
        }

        [Fact]
        public void ParseEventData_RealKernelGeneralPayload_YieldsAuthoritativeFields()
        {
            // Verbatim (values shortened) from a live Kernel-General 1 record on a 26220 dev machine.
            const string xml =
                "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>" +
                "<System><Provider Name='Microsoft-Windows-Kernel-General'/><EventID>1</EventID></System>" +
                "<EventData>" +
                "<Data Name='NewTime'>2026-08-21T09:40:02.1987028Z</Data>" +
                "<Data Name='OldTime'>2026-08-21T09:40:02.1973028Z</Data>" +
                "<Data Name='TimeDeltaInMs'>1</Data>" +
                "<Data Name='Reason'>1</Data>" +
                "<Data Name='ProcessName'>\\Device\\HarddiskVolume3\\Windows\\System32\\svchost.exe</Data>" +
                "<Data Name='ProcessID'>6892</Data>" +
                "</EventData></Event>";

            var data = SystemTimelineTracker.ParseEventData(xml);
            Assert.Equal("1", data["TimeDeltaInMs"]);
            Assert.Equal("2026-08-21T09:40:02.1987028Z", data["newtime"]); // case-insensitive keys
            Assert.NotNull(SystemTimelineTracker.TryParseUtc(data["OldTime"]));
        }

        [Fact]
        public void FormatDuration_CompactForms()
        {
            Assert.Equal("2h 05m", SystemTimelineTracker.FormatDuration(TimeSpan.FromMinutes(125)));
            Assert.Equal("56m 12s", SystemTimelineTracker.FormatDuration(TimeSpan.FromSeconds(56 * 60 + 12)));
            Assert.Equal("45s", SystemTimelineTracker.FormatDuration(TimeSpan.FromSeconds(45)));
        }

        [Fact]
        public void ProcessLeafName_StripsDevicePath()
        {
            Assert.Equal("svchost.exe",
                SystemTimelineTracker.ProcessLeafName(@"\Device\HarddiskVolume3\Windows\System32\svchost.exe"));
            Assert.Null(SystemTimelineTracker.ProcessLeafName(null));
            Assert.Null(SystemTimelineTracker.ProcessLeafName(@"\Device\HarddiskVolume3\"));
        }
    }
}
