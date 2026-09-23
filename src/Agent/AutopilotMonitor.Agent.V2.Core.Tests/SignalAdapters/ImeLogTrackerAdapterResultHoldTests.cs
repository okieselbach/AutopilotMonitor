using System;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    /// <summary>
    /// A held platform-script completion (IME result read before the executor end block) is
    /// emitted at the end of a later pass, when the tracker's "last matched" line is an unrelated
    /// one — the event stays bound to the result line's timestamp and pattern.
    /// </summary>
    public sealed class ImeLogTrackerAdapterResultHoldTests
    {
        private static readonly DateTime ClockNow = new DateTime(2026, 9, 10, 22, 13, 40, DateTimeKind.Utc);

        [Fact]
        public void Held_completion_is_bound_to_the_result_line_not_to_the_last_matched_one()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var startTs = ClockNow.AddSeconds(-95);
            var resultTs = ClockNow.AddSeconds(-3);

            f.Tracker.LastMatchedLogTimestamp = startTs;
            f.Tracker.HandlePlatformScriptStarted("376ee51a");

            f.Tracker.LastMatchedLogTimestamp = resultTs;
            f.Tracker.LastMatchedPatternId = "PS-SCRIPT-RESULT";
            f.Tracker.CompletePlatformScriptFromImeResultForTesting("376ee51a", "Success", resultTs);
            Assert.Empty(f.InfoEvents(SharedEventTypes.ScriptCompleted));

            // Other lines were matched before the end block was read and the pass ended.
            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddSeconds(-1);
            f.Tracker.LastMatchedPatternId = "IME-DOWNLOADING";
            f.Tracker.RecordPlatformScriptExitCodeForTesting("376ee51a", 0, resultTs.AddMilliseconds(-8));
            f.Tracker.FlushPendingPlatformScriptResults(f.Clock.UtcNow);

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            Assert.Equal(resultTs, info.OccurredAtUtc);
            Assert.Equal("PS-SCRIPT-RESULT", info.Payload!["patternId"]);
            Assert.Equal("0", info.Payload["exitCode"]);
            Assert.Equal("ime_policy_result", info.Payload["resultSource"]);
            Assert.Equal("92.00", info.Payload["durationSeconds"]);
        }

        [Fact]
        public void Held_completion_carries_the_provenance_of_its_result_line()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var startTs = ClockNow.AddSeconds(-95);
            var resultTs = ClockNow.AddSeconds(-3);

            // Start and result lines both self-anchored (+120).
            f.Tracker.LastMatchedLogTimestamp = startTs;
            f.Tracker.LastMatchedSourceLocalTimestamp = DateTime.SpecifyKind(startTs.AddMinutes(120), DateTimeKind.Unspecified);
            f.Tracker.LastMatchedSourceOffsetOrigin = CmTraceOffsetOrigin.LineAnchored;
            f.Tracker.LastMatchedSourceOffsetMinutes = 120;
            f.Tracker.HandlePlatformScriptStarted("376ee51a");

            f.Tracker.LastMatchedLogTimestamp = resultTs;
            f.Tracker.LastMatchedSourceLocalTimestamp = DateTime.SpecifyKind(resultTs.AddMinutes(120), DateTimeKind.Unspecified);
            f.Tracker.LastMatchedPatternId = "PS-SCRIPT-RESULT";
            f.Tracker.CompletePlatformScriptFromImeResultForTesting("376ee51a", "Success", resultTs);

            // By the time the held completion is emitted, the last matched line is an unrelated
            // one resolved in the reader's zone.
            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddSeconds(-1);
            f.Tracker.LastMatchedSourceLocalTimestamp = DateTime.SpecifyKind(ClockNow.AddSeconds(-1).AddMinutes(60), DateTimeKind.Unspecified);
            f.Tracker.LastMatchedSourceOffsetOrigin = CmTraceOffsetOrigin.None;
            f.Tracker.LastMatchedSourceOffsetMinutes = 60;
            f.Tracker.LastMatchedPatternId = "IME-DOWNLOADING";
            f.Tracker.RecordPlatformScriptExitCodeForTesting("376ee51a", 0, resultTs.AddMilliseconds(-8));
            f.Tracker.FlushPendingPlatformScriptResults(f.Clock.UtcNow);

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            Assert.Equal("line-anchored", info.Payload!["sourceOffsetOrigin"]);
            Assert.Equal("120", info.Payload["sourceOffsetMinutes"]);
            Assert.Equal(resultTs.AddMinutes(120).ToString("yyyy-MM-ddTHH:mm:ss.fffffff"), info.Payload["sourceLocalTs"]);
            Assert.Equal("line-anchored", info.Payload["startSourceOffsetOrigin"]);
            Assert.Equal("120", info.Payload["startSourceOffsetMinutes"]);
            Assert.Equal("92.00", info.Payload["durationSeconds"]);
            Assert.Equal("verified", info.Payload["durationProvenance"]);
        }
    }
}
