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
    /// Session 8bc1180f (2026-06-12) — harmless-EventId burst rollup. Windows dumped 689
    /// EventID-100 "Autopilot policy not found" records in one minute; every one was
    /// forwarded as an individual Debug event, saturating the signal-ingress queue
    /// (256/256 back-pressure). The tracker now forwards the first
    /// <see cref="ModernDeploymentTracker.HarmlessRollupIndividualLimit"/> occurrences per
    /// EventId individually and afterwards only every
    /// <see cref="ModernDeploymentTracker.HarmlessRollupEmitEvery"/>th, carrying the
    /// cumulative count.
    /// </summary>
    public sealed class ModernDeploymentTrackerRollupTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 6, 12, 9, 37, 0, DateTimeKind.Utc);

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink;
        private readonly ModernDeploymentTracker _tracker;

        public ModernDeploymentTrackerRollupTests()
        {
            _sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(_sink, new VirtualClock(At));
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            _tracker = new ModernDeploymentTracker(
                sessionId: "sess-rollup",
                tenantId: "tenant-rollup",
                post: post,
                logger: logger,
                backfillEnabled: false);
        }

        public void Dispose() => _tmp.Dispose();

        private void ProcessHarmless(int eventId, int times, int level = 2)
        {
            for (var i = 0; i < times; i++)
            {
                _tracker.ProcessEvent(
                    eventId: eventId,
                    level: level,
                    levelDisplayName: "Error",
                    providerName: "ModernDeployment-Diagnostics-Provider",
                    timeCreatedUtc: At.AddSeconds(i),
                    formattedDescription: $"Autopilot policy [SomePolicy] not found.",
                    shortName: "Autopilot",
                    channelName: ModernDeploymentTracker.AutopilotChannel,
                    isBackfill: false);
            }
        }

        private IReadOnlyList<FakeSignalIngressSink.PostedSignal> PostedLogs() =>
            _sink.Posted.Where(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == "modern_deployment_log").ToList();

        [Fact]
        public void FirstThreeOccurrences_AreForwardedIndividually()
        {
            ProcessHarmless(eventId: 100, times: ModernDeploymentTracker.HarmlessRollupIndividualLimit);

            Assert.Equal(ModernDeploymentTracker.HarmlessRollupIndividualLimit, PostedLogs().Count);
        }

        [Fact]
        public void BurstOf689_IsRolledUpToIndividualLimitPlusEveryHundredth()
        {
            // The real session-8bc1180f burst shape: 689 EventID-100 records in one minute.
            ProcessHarmless(eventId: 100, times: 689);

            // 3 individual + rollups at occurrence 100, 200, ..., 600 = 9 total (was 689).
            var posted = PostedLogs();
            Assert.Equal(
                ModernDeploymentTracker.HarmlessRollupIndividualLimit + 689 / ModernDeploymentTracker.HarmlessRollupEmitEvery,
                posted.Count);

            // The last rollup emission carries the cumulative count of 600 in the message.
            Assert.Contains("600 occurrences so far", posted[posted.Count - 1].Payload![SignalPayloadKeys.Message]);
        }

        [Fact]
        public void RollupCounters_AreIndependentPerEventId()
        {
            ProcessHarmless(eventId: 100, times: 10);
            ProcessHarmless(eventId: 1005, times: 2);

            // EventId 100: 3 individual (4..10 suppressed). EventId 1005: both individual —
            // its counter is untouched by the 100-burst.
            Assert.Equal(ModernDeploymentTracker.HarmlessRollupIndividualLimit + 2, PostedLogs().Count);
        }

        [Fact]
        public void NonHarmlessEventIds_AreNeverSuppressed()
        {
            // EventId 999 is not in the default harmless set {100, 1005, 1010} — every
            // occurrence keeps flowing (as modern_deployment_error for level 2).
            for (var i = 0; i < 10; i++)
            {
                _tracker.ProcessEvent(
                    eventId: 999,
                    level: 2,
                    levelDisplayName: "Error",
                    providerName: "ModernDeployment-Diagnostics-Provider",
                    timeCreatedUtc: At.AddSeconds(i),
                    formattedDescription: "Genuine deployment error.",
                    shortName: "Autopilot",
                    channelName: ModernDeploymentTracker.AutopilotChannel,
                    isBackfill: false);
            }

            var errors = _sink.Posted.Where(p =>
                p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == "modern_deployment_error").ToList();
            Assert.Equal(10, errors.Count);
        }

        [Fact]
        public void InfoLevelEvents_AreNotRollupGated()
        {
            // Level 4 (Informational) events are not part of the harmless Level-2/3 downgrade
            // and therefore not rollup-gated. (The watcher XPath only subscribes to Level<=3
            // in production; this guards the ProcessEvent contract itself.)
            for (var i = 0; i < 10; i++)
            {
                _tracker.ProcessEvent(
                    eventId: 100,
                    level: 4,
                    levelDisplayName: "Information",
                    providerName: "ModernDeployment-Diagnostics-Provider",
                    timeCreatedUtc: At.AddSeconds(i),
                    formattedDescription: "Informational record.",
                    shortName: "Autopilot",
                    channelName: ModernDeploymentTracker.AutopilotChannel,
                    isBackfill: false);
            }

            Assert.Equal(10, PostedLogs().Count);
        }
    }
}
