using System;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests
{
    /// <summary>
    /// Pins the clock-frame contract of the agent-silence sweep.
    ///
    /// <para>
    /// <c>LastEventAt</c> is the maximum AGENT-supplied event timestamp and therefore lives in the
    /// device clock frame — it carries the device's clock error and, for CMTrace-derived events,
    /// the writing process's timezone belief. Field measurement over 11k sessions found skews from
    /// -17 h to +1 h. Comparing that against a server-derived silence cutoff marked live agents
    /// Stalled ("Agent silent for 1020min") and left genuinely silent ones undetected for the
    /// duration of the skew. <c>LastIngestAt</c> is stamped from the server clock on every ingest
    /// and is the only value such a comparison may use.
    /// </para>
    /// </summary>
    public class MaintenanceServerFrameLastContactTests
    {
        private static readonly DateTime Now = new(2026, 08, 20, 12, 00, 00, DateTimeKind.Utc);

        private static SessionSummary Session(DateTime? lastIngestAt, DateTime? lastEventAt, DateTime startedAt)
            => new() { LastIngestAt = lastIngestAt, LastEventAt = lastEventAt, StartedAt = startedAt };

        [Fact]
        public void PrefersServerFrameIngestStamp_OverDeviceFrameEventStamp()
        {
            var s = Session(lastIngestAt: Now.AddMinutes(-1), lastEventAt: Now.AddHours(-17), startedAt: Now.AddHours(-2));
            Assert.Equal(Now.AddMinutes(-1), MaintenanceService.ServerFrameLastContact(s));
        }

        [Fact]
        public void NegativeSkew_DoesNotLookSilent_WhenIngestIsFresh()
        {
            // The exact field shape: IME-derived events landed 17 h in the past, so LastEventAt is
            // far behind the 2 h silence cutoff while the agent is demonstrably still reporting.
            var silenceCutoff = Now.AddHours(-2);
            var s = Session(lastIngestAt: Now.AddSeconds(-30), lastEventAt: Now.AddHours(-17), startedAt: Now.AddHours(-1));

            Assert.True(MaintenanceService.ServerFrameLastContact(s) > silenceCutoff,
                "a session whose ingest stamp is 30 s old must never be classified as agent-silent");
        }

        [Fact]
        public void PositiveSkew_DoesNotMaskSilence_WhenIngestIsStale()
        {
            // Mirror case: a +1 h skew pushes LastEventAt into the future, which previously made the
            // sweep blind for an hour. The server frame still sees the silence.
            var silenceCutoff = Now.AddHours(-2);
            var s = Session(lastIngestAt: Now.AddHours(-3), lastEventAt: Now.AddHours(1), startedAt: Now.AddHours(-4));

            Assert.True(MaintenanceService.ServerFrameLastContact(s) < silenceCutoff,
                "a session with no ingest for 3 h must be detected as silent regardless of a future-dated event stamp");
        }

        [Fact]
        public void FallsBackToEventStamp_OnRowsPredatingTheIngestStamp()
        {
            // Rollout safety: rows written before LastIngestAt existed must keep the old behaviour
            // rather than dropping out of the sweep entirely.
            var s = Session(lastIngestAt: null, lastEventAt: Now.AddHours(-3), startedAt: Now.AddHours(-5));
            Assert.Equal(Now.AddHours(-3), MaintenanceService.ServerFrameLastContact(s));
        }

        [Fact]
        public void FallsBackToStartedAt_WhenNoEventStampExists()
        {
            var s = Session(lastIngestAt: null, lastEventAt: null, startedAt: Now.AddHours(-4));
            Assert.Equal(Now.AddHours(-4), MaintenanceService.ServerFrameLastContact(s));
        }
    }
}
