using System;
using System.Globalization;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    /// <summary>
    /// Per-script run duration on every completion + the <c>script_timeout_suspected</c> heuristic.
    /// The fixture clock is fixed at <see cref="ImeLogTrackerAdapterFixture.DefaultClockStart"/>, so a
    /// completion with no source log timestamp resolves to that instant — setting
    /// <see cref="ScriptExecutionState.StartedAtUtc"/> relative to it gives a deterministic duration.
    /// </summary>
    public sealed class ImeLogTrackerAdapterScriptTimeoutTests
    {
        private static DateTime Now => ImeLogTrackerAdapterFixture.DefaultClockStart;

        private static double Duration(FakeSignalIngressSink.PostedSignal e) =>
            double.Parse(e.Payload!["durationSeconds"], CultureInfo.InvariantCulture);

        [Fact]
        public void Completed_script_carries_run_duration_from_start_to_completion()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "p-1",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Success",
                StartedAtUtc = Now.AddSeconds(-42),
            });

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            Assert.Equal(42d, Duration(info), 0);
        }

        [Fact]
        public void Completed_script_without_start_omits_duration()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "p-1",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Success",
                // StartedAtUtc deliberately null — completion seen without a matching start.
            });

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            Assert.False(info.Payload!.ContainsKey("durationSeconds"));
        }

        [Fact]
        public void Platform_script_failed_at_timeout_emits_script_timeout_suspected()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            // 30 min, Failed — mirrors the c3e0124c case (IME-marked Failed at its script timeout).
            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "c3e0124c",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Failed",
                StartedAtUtc = Now.AddMinutes(-30),
            });

            var info = f.InfoEvent(SharedEventTypes.ScriptTimeoutSuspected);
            Assert.Equal("c3e0124c", info.Payload!["policyId"]);
            Assert.Equal(EventSeverity.Warning.ToString(), info.Payload[SignalPayloadKeys.Severity]);
            Assert.Equal(1800d, Duration(info), 0);
        }

        [Fact]
        public void Short_failed_platform_script_does_not_emit_timeout()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "p-1",
                ScriptType = "platform",
                ExitCode = 1,
                Result = "Failed",
                StartedAtUtc = Now.AddMinutes(-10), // below the 25-min threshold
            });

            Assert.Empty(f.InfoEvents(SharedEventTypes.ScriptTimeoutSuspected));
        }

        [Fact]
        public void Long_but_successful_platform_script_does_not_emit_timeout()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "p-1",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Success",
                StartedAtUtc = Now.AddMinutes(-40), // long, but it completed on its own
            });

            Assert.Empty(f.InfoEvents(SharedEventTypes.ScriptTimeoutSuspected));
        }

        [Fact]
        public void Remediation_script_at_timeout_does_not_emit_timeout()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "p-1",
                ScriptType = "remediation",
                ScriptPart = "remediation",
                ExitCode = 1,
                Result = "Failed",
                StartedAtUtc = Now.AddMinutes(-30),
            });

            Assert.Empty(f.InfoEvents(SharedEventTypes.ScriptTimeoutSuspected));
        }

        [Fact]
        public void Timeout_event_is_deduped_per_policy()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            for (var i = 0; i < 3; i++)
            {
                adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
                {
                    PolicyId = "c3e0124c",
                    ScriptType = "platform",
                    ExitCode = 0,
                    Result = "Failed",
                    StartedAtUtc = Now.AddMinutes(-30),
                });
            }

            Assert.Single(f.InfoEvents(SharedEventTypes.ScriptTimeoutSuspected));
        }
    }
}
