using System;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// <see cref="TimelineEventStream.Publish"/> isolates subscribers: a throwing one must neither
    /// break the emit path nor starve the subscribers after it, and its fault must reach the log
    /// (Debug once, an identical repeat on Trace).
    /// </summary>
    public sealed class TimelineEventStreamTests
    {
        [Fact]
        public void A_throwing_subscriber_is_logged_and_the_next_one_still_runs()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Trace);
            var sut = new TimelineEventStream(logger);
            var delivered = 0;
            sut.EventEmitted += ThrowingSubscriber;
            sut.EventEmitted += (_, _, _) => delivered++;

            sut.Publish("app_install_started", EnrollmentPhase.Unknown, "ImeLogTracker");
            sut.Publish("app_install_completed", EnrollmentPhase.Unknown, "ImeLogTracker");

            Assert.Equal(2, delivered);
            var lines = File.ReadAllLines(Path.Combine(tmp.Path, "agent.log"))
                .Where(l => l.Contains("TimelineEventStream: subscriber"))
                .ToArray();
            Assert.Equal(2, lines.Length);
            Assert.Contains("[DEBUG]", lines[0]);
            Assert.Contains("ThrowingSubscriber threw: subscriber bug", lines[0]);
            Assert.Contains("[TRACE]", lines[1]);
        }

        private static void ThrowingSubscriber(string eventType, EnrollmentPhase phase, string source) =>
            throw new InvalidOperationException("subscriber bug");
    }
}
