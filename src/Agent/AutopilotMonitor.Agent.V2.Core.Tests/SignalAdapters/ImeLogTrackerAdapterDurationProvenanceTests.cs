using System;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    /// <summary>
    /// A script run duration is the span between two CMTrace lines, each resolved on its own zone
    /// belief. When the beliefs differ the span is a whole number of offset-grid steps plus the run
    /// time, not the run time: session 4377911b read a 10 s script as 3610 s (start line-anchored
    /// +120, result re-read after an overwrite rewind in the reader's zone +60 and rejected as
    /// ahead of the clock), a fleet tenant read 2–9 s scripts as 17 h every day. The adapter judges
    /// the pair from the provenance bound to each line and omits the span rather than lie.
    /// </summary>
    public sealed class ImeLogTrackerAdapterDurationProvenanceTests
    {
        private const string ServiceLog = "IntuneManagementExtension.log";
        private const string ExecutorLog = "AgentExecutor.log";

        private static CmTraceLineProvenance Line(
            DateTime resolvedUtc, int offsetMinutes, CmTraceOffsetOrigin origin,
            string file = ServiceLog, string? anchor = null, int? measured = null) =>
            new CmTraceLineProvenance
            {
                // The writer's local time is the resolved value with the applied offset put back.
                SourceLocalTs = DateTime.SpecifyKind(resolvedUtc.AddMinutes(offsetMinutes), DateTimeKind.Unspecified),
                Origin = origin,
                OffsetMinutes = offsetMinutes,
                EraAnchorKind = anchor,
                MeasuredWriterOffsetMinutes = measured,
                SourceFileName = file,
            };

        private static ScriptExecutionState Platform(string policyId, DateTime startedAtUtc, CmTraceLineProvenance start,
            DateTime? resultObservedAtUtc = null, CmTraceLineProvenance? result = null, string outcome = "Success", int exitCode = 0) =>
            new ScriptExecutionState
            {
                PolicyId = policyId,
                ScriptType = "platform",
                ExitCode = exitCode,
                Result = outcome,
                ResultSource = "ime_policy_result",
                StartedAtUtc = startedAtUtc,
                StartedAtProvenance = start,
                ResultObservedAtUtc = resultObservedAtUtc,
                ResultProvenance = result,
            };

        [Fact]
        public void Rejected_result_line_against_a_line_anchored_start_omits_the_span()
        {
            // Session 4377911b, script d94468af: start 15:19:57.005 local, self-anchored (+120);
            // result 15:20:07.391 local, re-read after a rewind and resolved in the reader's
            // zone (+60) → 14:20:07Z, one hour ahead of the clock. The raw pair reads 3610 s.
            var clockNow = new DateTime(2026, 9, 17, 13, 20, 7, 437, DateTimeKind.Utc);
            using var f = new ImeLogTrackerAdapterFixture(clockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var startUtc = new DateTime(2026, 9, 17, 13, 19, 57, 5, DateTimeKind.Utc);
            var resultRaw = new DateTime(2026, 9, 17, 14, 20, 7, 391, DateTimeKind.Utc);

            adapter.TriggerScriptCompletedFromTest(Platform("d94468af",
                startUtc, Line(startUtc, 120, CmTraceOffsetOrigin.LineAnchored, measured: 120),
                resultRaw, Line(resultRaw, 60, CmTraceOffsetOrigin.None, measured: 120)));

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            Assert.Equal(clockNow, info.OccurredAtUtc);                              // stamp: clamped to the clock
            Assert.Equal("true", info.Payload!["derivedTimestamp"]);
            Assert.Equal(resultRaw.ToString("o"), info.Payload["rejectedSourceTimestamp"]);
            Assert.False(info.Payload.ContainsKey("durationSeconds"));
            Assert.False(info.Payload.ContainsKey("durationBasis"));
            Assert.Equal("rejected-endpoint", info.Payload["durationSuppressedReason"]);
            Assert.Equal("unverified", info.Payload["durationProvenance"]);
            // The end line's own provenance and the start line's, side by side.
            Assert.Equal("reader-zone-fallback", info.Payload["sourceOffsetOrigin"]);
            Assert.Equal("60", info.Payload["sourceOffsetMinutes"]);
            Assert.Equal("120", info.Payload["measuredWriterOffsetMinutes"]);
            Assert.Equal("2026-09-17T15:20:07.3910000", info.Payload["sourceLocalTs"]);
            Assert.Equal("line-anchored", info.Payload["startSourceOffsetOrigin"]);
            Assert.Equal("120", info.Payload["startSourceOffsetMinutes"]);
        }

        [Fact]
        public void Rejected_era_anchored_result_of_a_failed_script_omits_the_span_and_the_timeout_verdict()
        {
            // Fleet shape (script_failed, daily in one tenant): result era-anchored at −420 (the
            // OOBE default zone's era) on a line written under +600 → 17 h ahead of the clock;
            // the 61203 s "run" of a 3 s script fired script_timeout_suspected every day.
            var clockNow = new DateTime(2026, 9, 21, 1, 30, 13, DateTimeKind.Utc);
            using var f = new ImeLogTrackerAdapterFixture(clockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var startUtc = clockNow.AddSeconds(-8);
            var resultRaw = clockNow.AddSeconds(-5).AddHours(17);

            adapter.TriggerScriptCompletedFromTest(Platform("88d406eb",
                startUtc, Line(startUtc, 600, CmTraceOffsetOrigin.LineAnchored),
                resultRaw, Line(resultRaw, -420, CmTraceOffsetOrigin.EraAnchored, anchor: "bootstrap-policy-result", measured: 600),
                outcome: "Failed"));

            var failed = f.InfoEvent(SharedEventTypes.ScriptFailed);
            Assert.Equal("rejected-endpoint", failed.Payload!["durationSuppressedReason"]);
            Assert.False(failed.Payload.ContainsKey("durationSeconds"));
            Assert.Equal("era-anchored", failed.Payload["sourceOffsetOrigin"]);
            Assert.Equal("bootstrap-policy-result", failed.Payload["sourceOffsetAnchor"]);
            Assert.Empty(f.InfoEvents(SharedEventTypes.ScriptTimeoutSuspected));
        }

        [Fact]
        public void Mixed_pair_on_the_offset_grid_omits_the_span_even_when_nothing_was_rejected()
        {
            // Session cbaed57b: start from AgentExecutor.log in the reader's zone (+120, two
            // hours too early), result era-anchored at 0 in the service log → 7205 s.
            var clockNow = new DateTime(2026, 9, 11, 8, 48, 25, DateTimeKind.Utc);
            using var f = new ImeLogTrackerAdapterFixture(clockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var resultUtc = clockNow.AddSeconds(-1);
            var startUtc = resultUtc.AddSeconds(-7205);

            adapter.TriggerScriptCompletedFromTest(Platform("846cea22",
                startUtc, Line(startUtc, 120, CmTraceOffsetOrigin.None, file: ExecutorLog),
                resultUtc, Line(resultUtc, 0, CmTraceOffsetOrigin.EraAnchored, anchor: "bootstrap-policy-result")));

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            Assert.False(info.Payload!.ContainsKey("derivedTimestamp"));
            Assert.False(info.Payload.ContainsKey("durationSeconds"));
            Assert.Equal("offset-grid", info.Payload["durationSuppressedReason"]);
            Assert.Equal("unverified", info.Payload["durationProvenance"]);
        }

        [Fact]
        public void Mixed_pair_off_the_grid_keeps_the_span_as_unverified()
        {
            var clockNow = ImeLogTrackerAdapterFixture.DefaultClockStart;
            using var f = new ImeLogTrackerAdapterFixture(clockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var resultUtc = clockNow.AddSeconds(-1);
            var startUtc = resultUtc.AddSeconds(-26);

            adapter.TriggerScriptCompletedFromTest(Platform("f6f67f79",
                startUtc, Line(startUtc, 120, CmTraceOffsetOrigin.LineAnchored),
                resultUtc, Line(resultUtc, 120, CmTraceOffsetOrigin.None)));

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            Assert.Equal("26.00", info.Payload!["durationSeconds"]);
            Assert.Equal("script_runtime", info.Payload["durationBasis"]);
            Assert.Equal("unverified", info.Payload["durationProvenance"]);
            Assert.False(info.Payload.ContainsKey("durationSuppressedReason"));
        }

        [Fact]
        public void Two_line_anchored_ends_on_the_grid_are_a_verified_fifteen_minute_run()
        {
            var clockNow = ImeLogTrackerAdapterFixture.DefaultClockStart;
            using var f = new ImeLogTrackerAdapterFixture(clockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var resultUtc = clockNow.AddSeconds(-1);
            var startUtc = resultUtc.AddSeconds(-900);

            adapter.TriggerScriptCompletedFromTest(Platform("11111111",
                startUtc, Line(startUtc, 120, CmTraceOffsetOrigin.LineAnchored),
                resultUtc, Line(resultUtc, 120, CmTraceOffsetOrigin.LineAnchored)));

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            Assert.Equal("900.00", info.Payload!["durationSeconds"]);
            Assert.Equal("verified", info.Payload["durationProvenance"]);
        }

        [Fact]
        public void Two_fallback_ends_from_the_same_file_share_their_error_and_stay_measurable()
        {
            var clockNow = ImeLogTrackerAdapterFixture.DefaultClockStart;
            using var f = new ImeLogTrackerAdapterFixture(clockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var resultUtc = clockNow.AddSeconds(-1);
            var startUtc = resultUtc.AddSeconds(-900);

            adapter.TriggerScriptCompletedFromTest(Platform("22222222",
                startUtc, Line(startUtc, 60, CmTraceOffsetOrigin.None),
                resultUtc, Line(resultUtc, 60, CmTraceOffsetOrigin.None)));

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            Assert.Equal("900.00", info.Payload!["durationSeconds"]);
            Assert.Equal("verified", info.Payload["durationProvenance"]);
        }

        [Fact]
        public void Two_fallback_ends_from_different_files_are_different_writers()
        {
            // Same reader zone applied to both, but the executor child process and the service
            // can hold different beliefs (cbaed57b) — a grid-sized span is not trusted.
            var clockNow = ImeLogTrackerAdapterFixture.DefaultClockStart;
            using var f = new ImeLogTrackerAdapterFixture(clockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var resultUtc = clockNow.AddSeconds(-1);
            var startUtc = resultUtc.AddSeconds(-900);

            adapter.TriggerScriptCompletedFromTest(Platform("33333333",
                startUtc, Line(startUtc, 60, CmTraceOffsetOrigin.None, file: ExecutorLog),
                resultUtc, Line(resultUtc, 60, CmTraceOffsetOrigin.None)));

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            Assert.False(info.Payload!.ContainsKey("durationSeconds"));
            Assert.Equal("offset-grid", info.Payload["durationSuppressedReason"]);
        }

        [Fact]
        public void Without_provenance_the_pair_is_taken_as_before()
        {
            // States from before the field (persisted by an older agent) and synthetic events:
            // no snapshot on either end, the 30-minute Failed run still reads as a timeout.
            var clockNow = ImeLogTrackerAdapterFixture.DefaultClockStart;
            using var f = new ImeLogTrackerAdapterFixture(clockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            f.Tracker.LastMatchedLogTimestamp = clockNow.AddSeconds(-1);
            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "44444444",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Failed",
                StartedAtUtc = clockNow.AddSeconds(-1801),
            });

            var failed = f.InfoEvent(SharedEventTypes.ScriptFailed);
            Assert.Equal("1800.00", failed.Payload!["durationSeconds"]);
            Assert.False(failed.Payload.ContainsKey("durationProvenance"));
            Assert.False(failed.Payload.ContainsKey("durationSuppressedReason"));
            Assert.Single(f.InfoEvents(SharedEventTypes.ScriptTimeoutSuspected));
        }

        [Fact]
        public void Negative_grid_sized_span_from_a_future_shifted_start_is_named_not_dropped_silently()
        {
            var clockNow = ImeLogTrackerAdapterFixture.DefaultClockStart;
            using var f = new ImeLogTrackerAdapterFixture(clockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var resultUtc = clockNow.AddSeconds(-1);
            var startUtc = resultUtc.AddSeconds(3595); // resolved one hour ahead of its real instant

            adapter.TriggerScriptCompletedFromTest(Platform("55555555",
                startUtc, Line(startUtc, 60, CmTraceOffsetOrigin.None),
                resultUtc, Line(resultUtc, 120, CmTraceOffsetOrigin.LineAnchored)));

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            Assert.False(info.Payload!.ContainsKey("durationSeconds"));
            Assert.Equal("offset-grid", info.Payload["durationSuppressedReason"]);
        }

        [Fact]
        public void Health_script_cycle_is_judged_the_same_way()
        {
            // HS-SCRIPT-START self-anchored, HS-NEW-RESULT re-read in the reader's zone and
            // rejected: the cycle duration is omitted, the compliance verdict stays.
            var clockNow = ImeLogTrackerAdapterFixture.DefaultClockStart;
            using var f = new ImeLogTrackerAdapterFixture(clockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var startUtc = clockNow.AddSeconds(-40);
            var resultRaw = clockNow.AddSeconds(-2).AddHours(1);

            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "66666666",
                ScriptType = "remediation",
                ScriptPart = "detection",
                ExitCode = 0,
                ComplianceResult = "True",
                StartedAtUtc = startUtc,
                StartedAtProvenance = Line(startUtc, 120, CmTraceOffsetOrigin.LineAnchored),
                ResultObservedAtUtc = resultRaw,
                ResultProvenance = Line(resultRaw, 60, CmTraceOffsetOrigin.None),
                DurationBasis = "cycle_including_reporting_latency",
            });

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            Assert.Equal("True", info.Payload!["complianceResult"]);
            Assert.False(info.Payload.ContainsKey("durationSeconds"));
            Assert.Equal("rejected-endpoint", info.Payload["durationSuppressedReason"]);
        }
    }
}
