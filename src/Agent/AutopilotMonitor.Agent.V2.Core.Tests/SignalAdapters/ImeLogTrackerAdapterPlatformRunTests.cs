using System;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    /// <summary>
    /// D-316: every emitted platform-script completion carries a run id, and the tracker's run register holds what
    /// was emitted for it under the IME user id — what the registry reconciliation pairs IME's saved result with.
    /// </summary>
    public sealed class ImeLogTrackerAdapterPlatformRunTests
    {
        private static readonly DateTime ClockNow = new DateTime(2026, 10, 3, 21, 0, 0, DateTimeKind.Utc);
        private const string DeviceUser = "00000000-0000-0000-0000-000000000000";
        private const string PolicyId = "1d6f0a52-3c4e-4b7a-9e21-5f8c7d2b9a10";

        [Fact]
        public void Platform_completion_carries_its_run_id_and_the_register_holds_the_emitted_run()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddSeconds(-10);
            f.Tracker.HandlePlatformScriptStarted(PolicyId, "ime", DeviceUser);
            f.Tracker.RecordPlatformScriptExitCodeForTesting(PolicyId, 0, ClockNow.AddSeconds(-1));
            f.Tracker.LastMatchedLogTimestamp = ClockNow;
            f.Tracker.CompletePlatformScriptFromImeResultForTesting(PolicyId, "Success", ClockNow);

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            Assert.True(Guid.TryParse(info.Payload!["runId"], out _));

            f.Tracker.ScriptRuns.NotePassCompleted(ClockNow.AddMinutes(1));
            Assert.Equal(PlatformRunClaim.Claimed, f.Tracker.ScriptRuns.TryClaim(DeviceUser, PolicyId, ClockNow.AddSeconds(5), out var run));
            Assert.Equal(info.Payload["runId"], run!.RunId);
            Assert.Equal(0, run.ExitCode);
            Assert.Equal("Success", run.Result);
        }

        [Theory]
        [InlineData("[PowerShell] User Id = 00000000-0000-0000-0000-000000000000, Policy id = d94468af-6ebe-4e94-9901-2a62d33ea6c1, policy result = Success", "00000000-0000-0000-0000-000000000000")]
        [InlineData(@"Adding argument powershell with value C:\Program Files (x86)\Microsoft Intune Management Extension\Policies\Scripts\6C3F8A20-1D5E-4B97-8F4A-0E2D6B9C7A13_d94468af-6ebe-4e94-9901-2a62d33ea6c1.ps1", "6c3f8a20-1d5e-4b97-8f4a-0e2d6b9c7a13")]
        [InlineData(@"Script file C:\Program Files (x86)\Microsoft Intune Management Extension\Policies\Scripts\00000000-0000-0000-0000-000000000000_d94468af-6ebe-4e94-9901-2a62d33ea6c1.ps1 is generated", "00000000-0000-0000-0000-000000000000")]
        [InlineData(@"Adding argument powershellDetection with value C:\Program Files (x86)\Microsoft Intune Management Extension\Content\DetectionScripts\8b2e4c71-6a9d-4f03-b5e8-2c7a1d9f4e36_2.ps1", null)]
        [InlineData("[PowerShell] User Id = not-a-guid, Policy id = d94468af", null)]
        [InlineData("", null)]
        public void Ime_user_id_is_read_from_the_result_line_and_the_script_path(string text, string? expected)
        {
            Assert.Equal(expected, ImeLogTracker.ImeUserIdFrom(text));
        }
    }
}
