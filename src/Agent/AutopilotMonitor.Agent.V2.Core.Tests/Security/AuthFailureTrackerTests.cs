using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    public sealed class AuthFailureTrackerTests
    {
        private static AgentLogger NewLogger()
        {
            var tmp = new TempDirectory();
            return new AgentLogger(System.IO.Path.Combine(tmp.Path, "logs"), AgentLogLevel.Debug);
        }

        // ============================================================= Count-based threshold

        [Fact]
        public void RecordFailure_fires_threshold_at_max_failures()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 3, timeoutMinutes: 0, clock, NewLogger());

            AuthFailureThresholdEventArgs captured = null!;
            tracker.ThresholdExceeded += (_, e) => captured = e;

            tracker.RecordFailure(401, "config");
            tracker.RecordFailure(401, "config");
            Assert.Null(captured);

            tracker.RecordFailure(401, "config");
            Assert.NotNull(captured);
            Assert.Equal(3, captured.ConsecutiveFailures);
            Assert.Equal(401, captured.LastStatusCode);
            Assert.Equal("config", captured.LastOperation);
        }

        [Fact]
        public void RecordSuccess_resets_counter()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 3, timeoutMinutes: 0, clock, NewLogger());

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => events++;

            tracker.RecordFailure(401, "config");
            tracker.RecordFailure(401, "config");
            tracker.RecordSuccess();
            tracker.RecordFailure(403, "upload");
            tracker.RecordFailure(403, "upload");

            Assert.Equal(0, events);
            Assert.Equal(2, tracker.ConsecutiveFailures);
        }

        // ============================================================= Time-window threshold

        [Fact]
        public void RecordFailure_fires_threshold_when_time_window_exceeded()
        {
            var start = new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc);
            var clock = new FakeClock(start);
            var tracker = new AuthFailureTracker(maxFailures: 0, timeoutMinutes: 10, clock, NewLogger());

            int events = 0;
            AuthFailureThresholdEventArgs captured = null!;
            tracker.ThresholdExceeded += (_, e) => { events++; captured = e; };

            tracker.RecordFailure(401, "config");
            Assert.Equal(0, events);

            clock.Now = start.AddMinutes(5);
            tracker.RecordFailure(401, "config");
            Assert.Equal(0, events);

            clock.Now = start.AddMinutes(11);
            tracker.RecordFailure(401, "config");

            Assert.Equal(1, events);
            Assert.Equal(start, captured.FirstFailureUtc);
            Assert.Contains("time window", captured.Reason);
        }

        [Fact]
        public void RecordSuccess_resets_time_window_anchor()
        {
            var start = new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc);
            var clock = new FakeClock(start);
            var tracker = new AuthFailureTracker(maxFailures: 0, timeoutMinutes: 10, clock, NewLogger());

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => events++;

            tracker.RecordFailure(401, "config");
            clock.Now = start.AddMinutes(9);
            tracker.RecordSuccess();

            // fresh window — 11 minutes later, first failure in this new window, no threshold.
            clock.Now = start.AddMinutes(20);
            tracker.RecordFailure(401, "config");

            Assert.Equal(0, events);
        }

        // ============================================================= Idempotence

        [Fact]
        public void ThresholdExceeded_fires_only_once_even_with_many_failures()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 2, timeoutMinutes: 0, clock, NewLogger());

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => events++;

            for (int i = 0; i < 10; i++) tracker.RecordFailure(401, "config");

            Assert.Equal(1, events);
        }

        [Fact]
        public void ThresholdExceeded_handler_exception_does_not_propagate()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 1, timeoutMinutes: 0, clock, NewLogger());

            tracker.ThresholdExceeded += (_, _) => throw new InvalidOperationException("listener exploded");

            // Must not propagate — tracker is a best-effort observer.
            tracker.RecordFailure(401, "config");
        }

        // ============================================================= Disabled limits

        [Fact]
        public void RecordFailure_never_fires_when_both_limits_disabled()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 0, timeoutMinutes: 0, clock, NewLogger());

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => events++;

            for (int i = 0; i < 100; i++) tracker.RecordFailure(401, "op");

            Assert.Equal(0, events);
            Assert.Equal(100, tracker.ConsecutiveFailures);
        }

        // ============================================================= UpdateLimits (remote config override)

        [Fact]
        public void UpdateLimits_tightens_threshold_retroactively()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 10, timeoutMinutes: 0, clock, NewLogger());

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => events++;

            tracker.RecordFailure(401, "config");
            tracker.RecordFailure(401, "config");

            // Tenant tightened the policy — the next failure should now fire.
            tracker.UpdateLimits(maxFailures: 3, timeoutMinutes: 0);
            tracker.RecordFailure(401, "config");

            Assert.Equal(1, events);
        }

        [Fact]
        public void UpdateLimits_accepts_negative_max_as_disabled()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: -1, timeoutMinutes: 0, clock, NewLogger());

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => events++;

            for (int i = 0; i < 20; i++) tracker.RecordFailure(401, "op");
            Assert.Equal(0, events);
        }

        // ============================================================= Thread-safety smoke

        [Fact]
        public void RecordFailure_is_safe_under_concurrent_callers()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 50, timeoutMinutes: 0, clock, NewLogger());

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => Interlocked.Increment(ref events);

            var threads = new Thread[8];
            for (int t = 0; t < threads.Length; t++)
            {
                threads[t] = new Thread(() =>
                {
                    for (int i = 0; i < 20; i++) tracker.RecordFailure(401, "concurrent");
                });
                threads[t].Start();
            }
            foreach (var th in threads) th.Join();

            // Many concurrent Increments cross the 50 ceiling — but ThresholdExceeded must fire exactly once.
            Assert.Equal(1, events);
        }

        // ============================================================= Guard

        [Fact]
        public void Ctor_throws_on_null_clock()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new AuthFailureTracker(1, 0, null!, NewLogger()));
        }

        [Fact]
        public void Ctor_throws_on_null_logger()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new AuthFailureTracker(1, 0, new FakeClock(DateTime.UtcNow), null!));
        }

        // ============================================================= Distress dispatch (V1 parity)

        [Theory]
        // V1 parity: 403 → DeviceNotRegistered, everything else (including 401 and any non-403
        // error the tenant happened to misconfigure) → AuthCertificateRejected. Distress dispatch
        // fires exactly once per tracker lifetime so we don't spam the channel with the same
        // auth problem repeating on every request.
        [InlineData(401, 1, DistressErrorType.AuthCertificateRejected)]
        [InlineData(401, 3, DistressErrorType.AuthCertificateRejected)] // dedup: 3 failures → 1 dispatch
        [InlineData(403, 1, DistressErrorType.DeviceNotRegistered)]
        [InlineData(403, 3, DistressErrorType.DeviceNotRegistered)]     // dedup for 403 too (NEW coverage)
        [InlineData(500, 1, DistressErrorType.AuthCertificateRejected)] // non-auth 5xx uses catch-all mapping (NEW)
        public void RecordFailure_dispatches_distress_once_with_correct_error_type_per_http_status(
            int statusCode, int nFailures, DistressErrorType expectedErrorType)
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var fakeDistress = new FakeDistressReporter();
            // maxFailures high enough that the threshold short-circuit (_thresholdFired=1 path)
            // does not interfere with the per-call distress semantics we're exercising.
            var tracker = new AuthFailureTracker(maxFailures: 50, timeoutMinutes: 0, clock, NewLogger(), fakeDistress);

            for (int i = 0; i < nFailures; i++)
                tracker.RecordFailure(statusCode, "agent/config");

            Assert.Single(fakeDistress.Calls);
            Assert.Equal(expectedErrorType, fakeDistress.Calls[0].ErrorType);
            Assert.Equal(statusCode, fakeDistress.Calls[0].HttpStatusCode);
            Assert.Contains("agent/config", fakeDistress.Calls[0].Message);
        }

        [Fact]
        public void RecordFailure_only_the_stopped_app_page_counts_without_distress()
        {
            // Rewritten deliberately for the client-certificate interception plan: the former
            // "endpoint unavailable" flag covered every HTML platform page, so the platform's
            // "Client Certificate Required" page (TLS inspection on the path) shut healthy agents
            // down. Now only the stopped-app page keeps that semantics — it counts toward the
            // shutdown (a dead endpoint must not retry forever) without a distress report that
            // could not reach it — while the certificate page never counts (tests below).
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var fakeDistress = new FakeDistressReporter();
            var tracker = new AuthFailureTracker(maxFailures: 2, timeoutMinutes: 0, clock, NewLogger(), fakeDistress);

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => events++;

            tracker.RecordFailure(403, "agent/config", InterceptionFixtures.CertificateRequired());
            Assert.Equal(0, tracker.ConsecutiveFailures);

            tracker.RecordFailure(403, "agent/config", InterceptionFixtures.StoppedApp());
            tracker.RecordFailure(403, "agent/register-session", InterceptionFixtures.StoppedApp());

            Assert.Equal(2, tracker.ConsecutiveFailures);
            Assert.Equal(1, events);
            Assert.DoesNotContain(fakeDistress.Calls, c => c.ErrorType == DistressErrorType.DeviceNotRegistered);
        }

        [Fact]
        public void RecordFailure_stopped_app_page_does_not_consume_streak_report_slot()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var fakeDistress = new FakeDistressReporter();
            var tracker = new AuthFailureTracker(maxFailures: 50, timeoutMinutes: 0, clock, NewLogger(), fakeDistress);

            // The streak opens with a platform-403 (no dispatch), then a live backend answers
            // with a genuine 403 — that one must still produce the streak's single report.
            tracker.RecordFailure(403, "agent/config", InterceptionFixtures.StoppedApp());
            tracker.RecordFailure(403, "agent/config");
            tracker.RecordFailure(403, "agent/config");

            Assert.Single(fakeDistress.Calls);
            Assert.Equal(DistressErrorType.DeviceNotRegistered, fakeDistress.Calls[0].ErrorType);
        }

        // ============================================================= Foreign answers (interception)

        [Fact]
        public void RecordFailure_foreign_never_reaches_the_threshold()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 5, timeoutMinutes: 10, clock, NewLogger(), new FakeDistressReporter());

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => events++;

            for (int i = 0; i < 10; i++)
            {
                tracker.RecordFailure(403, "agent/telemetry", InterceptionFixtures.CertificateRequired());
                clock.Now = clock.Now.AddMinutes(3); // also past the time window
            }

            Assert.Equal(0, events);
            Assert.Equal(0, tracker.ConsecutiveFailures);
        }

        [Fact]
        public void RecordFailure_foreign_dispatches_one_ClientCertificateNotReceived_per_process()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var fakeDistress = new FakeDistressReporter();
            var tracker = new AuthFailureTracker(maxFailures: 5, timeoutMinutes: 0, clock, NewLogger(), fakeDistress);

            tracker.RecordFailure(403, "agent/register-session", InterceptionFixtures.CertificateRequired());
            tracker.RecordFailure(403, "agent/telemetry", InterceptionFixtures.CertificateRequired());
            tracker.RecordSuccess();   // a success re-arms the streak report, never the per-process one
            tracker.RecordFailure(401, "agent/telemetry", InterceptionFixtures.CertificateRequired(401));

            var call = Assert.Single(fakeDistress.Calls);
            Assert.Equal(DistressErrorType.ClientCertificateNotReceived, call.ErrorType);
            Assert.Equal(403, call.HttpStatusCode);
            // Operation, status, sub-reason, page title, issuer — in that order, within the 256 budget.
            Assert.StartsWith("agent/register-session http 403 client_certificate_required;", call.Message);
            Assert.True(
                call.Message.IndexOf(InterceptionFixtures.PlatformPageTitle, StringComparison.Ordinal) <
                call.Message.IndexOf(InterceptionFixtures.InspectionIssuer, StringComparison.Ordinal));
            Assert.True(call.Message.Length <= 256, $"distress message is {call.Message.Length} characters");
        }

        [Fact]
        public void RecordFailure_foreign_within_a_backend_streak_neither_resets_nor_advances_it()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var fakeDistress = new FakeDistressReporter();
            var tracker = new AuthFailureTracker(maxFailures: 3, timeoutMinutes: 0, clock, NewLogger(), fakeDistress);

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => events++;

            tracker.RecordFailure(403, "agent/telemetry");
            tracker.RecordFailure(403, "agent/telemetry");
            tracker.RecordFailure(403, "agent/telemetry", InterceptionFixtures.CertificateRequired());
            tracker.RecordFailure(403, "agent/telemetry", InterceptionFixtures.CertificateRequired());

            Assert.Equal(2, tracker.ConsecutiveFailures);
            Assert.Equal(0, events);

            tracker.RecordFailure(403, "agent/telemetry");   // the third backend rejection trips it

            Assert.Equal(3, tracker.ConsecutiveFailures);
            Assert.Equal(1, events);
            Assert.Equal(1, fakeDistress.Calls.Count(c => c.ErrorType == DistressErrorType.DeviceNotRegistered));
        }

        // ============================================================= Pending foreign distress

        [Fact]
        public void WaitForPendingForeignDistress_returns_at_once_when_nothing_was_dispatched()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 5, timeoutMinutes: 0, clock, NewLogger(), new FakeDistressReporter());

            Assert.True(tracker.WaitForPendingForeignDistress(TimeSpan.Zero));
        }

        [Fact]
        public void WaitForPendingForeignDistress_waits_for_the_send_to_finish()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            Task? inFlight = null;
            var fakeDistress = new FakeDistressReporter { Send = () => inFlight = Task.Delay(200) };
            var tracker = new AuthFailureTracker(maxFailures: 5, timeoutMinutes: 0, clock, NewLogger(), fakeDistress);

            tracker.RecordFailure(403, "agent/register-session", InterceptionFixtures.CertificateRequired());

            Assert.True(tracker.WaitForPendingForeignDistress(TimeSpan.FromSeconds(10)));
            Assert.True(inFlight!.IsCompleted);
        }

        [Fact]
        public void WaitForPendingForeignDistress_stops_at_the_bound()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var neverSent = new TaskCompletionSource<bool>();
            var fakeDistress = new FakeDistressReporter { Send = () => neverSent.Task };
            var tracker = new AuthFailureTracker(maxFailures: 5, timeoutMinutes: 0, clock, NewLogger(), fakeDistress);
            tracker.RecordFailure(403, "agent/register-session", InterceptionFixtures.CertificateRequired());

            var watch = Stopwatch.StartNew();
            var finished = tracker.WaitForPendingForeignDistress(TimeSpan.FromMilliseconds(100));
            watch.Stop();

            Assert.False(finished);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"waited {watch.Elapsed}");
        }

        [Fact]
        public void WaitForPendingForeignDistress_never_waits_for_a_backend_streak_report()
        {
            // Only the ClientCertificateNotReceived send is kept: a failed start must not block
            // on anything else.
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var neverSent = new TaskCompletionSource<bool>();
            var fakeDistress = new FakeDistressReporter { Send = () => neverSent.Task };
            var tracker = new AuthFailureTracker(maxFailures: 5, timeoutMinutes: 0, clock, NewLogger(), fakeDistress);

            tracker.RecordFailure(403, "agent/register-session");

            Assert.Equal(DistressErrorType.DeviceNotRegistered, Assert.Single(fakeDistress.Calls).ErrorType);
            Assert.True(tracker.WaitForPendingForeignDistress(TimeSpan.Zero));
        }

        [Fact]
        public void RecordSuccess_rearms_distress_dispatch_for_the_next_streak()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var fakeDistress = new FakeDistressReporter();
            var tracker = new AuthFailureTracker(maxFailures: 50, timeoutMinutes: 0, clock, NewLogger(), fakeDistress);

            tracker.RecordFailure(401, "agent/config");
            tracker.RecordSuccess();
            tracker.RecordFailure(403, "agent/config");

            Assert.Equal(2, fakeDistress.Calls.Count);
            Assert.Equal(DistressErrorType.AuthCertificateRejected, fakeDistress.Calls[0].ErrorType);
            Assert.Equal(DistressErrorType.DeviceNotRegistered, fakeDistress.Calls[1].ErrorType);
        }

        [Fact]
        public void RecordFailure_without_distress_reporter_is_silent_but_still_tracks()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 2, timeoutMinutes: 0, clock, NewLogger());

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => events++;

            tracker.RecordFailure(401, "op");
            tracker.RecordFailure(401, "op");

            Assert.Equal(1, events);
        }

        private sealed class FakeClock : AutopilotMonitor.DecisionCore.Engine.IClock
        {
            public DateTime Now { get; set; }
            public FakeClock(DateTime now) { Now = now; }
            public DateTime UtcNow => Now;
            public System.Threading.Tasks.Task Delay(TimeSpan delay, CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
