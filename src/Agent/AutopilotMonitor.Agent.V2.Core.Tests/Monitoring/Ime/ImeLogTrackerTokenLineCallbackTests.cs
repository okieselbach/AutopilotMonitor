using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Raw per-line token callbacks for the Entra user-affinity detector (2026-09-04).
    /// <c>OnUserTokenAcquired</c> fires on EVERY IME-TOKEN-SUCCESS line — independent of the
    /// phase-change dedup that swallows the second AccountSetup detection — and
    /// <c>OnTokenFailureLine</c> on every failure line; both honour the historic-replay gate.
    /// </summary>
    public sealed class ImeLogTrackerTokenLineCallbackTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 4, 14, 0, 0, DateTimeKind.Utc);

        private static List<ImeLogPattern> Patterns() => new List<ImeLogPattern>
        {
            new ImeLogPattern
            {
                PatternId = "IME-TOKEN-SUCCESS", Category = "always", Enabled = true,
                Pattern = "^Successfully get the token",
                Action = "espPhaseDetected",
                Parameters = new Dictionary<string, string> { ["phase"] = "AccountSetup" },
            },
            new ImeLogPattern
            {
                PatternId = "IME-TOKEN-FAILURE", Category = "always", Enabled = true,
                Pattern = @"^Failed to get AAD token\..*?errorCode = (?<errorCode>.*?)$",
                Action = "imeTokenFailure",
                Parameters = new Dictionary<string, string>(),
            },
        };

        private static ImeLogTracker Build(TempDirectory tmp)
        {
            var tracker = new ImeLogTracker(
                logFolder: tmp.Path,
                patterns: Patterns(),
                logger: new AgentLogger(tmp.Path, AgentLogLevel.Info));
            tracker.UtcNowProvider = () => Now;
            return tracker;
        }

        private const string Success = "Successfully get the token with client id fc0f3af4-6835-4174-b806-f7db311fd2f3 and resource id 26a4ae64-5862-427f-a9b0-044e62572a4f";
        private const string Failure = "Failed to get AAD token. len = 34 using client id fc0f3af4-6835-4174-b806-f7db311fd2f3 and resource id 26a4ae64-5862-427f-a9b0-044e62572a4f, errorCode = 3399548929";

        [Fact]
        public void Every_token_success_line_invokes_callback_even_when_phase_is_deduplicated()
        {
            using var tmp = new TempDirectory();
            using var tracker = Build(tmp);
            var tokens = new List<DateTime?>();
            tracker.OnUserTokenAcquired = ts => tokens.Add(ts);

            // The phase-change dedup lives in the adapter (AccountSetup is emitted once);
            // the raw token callback must fire for BOTH lines regardless.
            tracker.ProcessLogMessageForTest(Success, Now.AddMinutes(-6));
            tracker.ProcessLogMessageForTest(Success, Now.AddMinutes(-2));

            Assert.Equal(2, tokens.Count);
            Assert.Equal(Now.AddMinutes(-6), tokens[0]);
            Assert.Equal(Now.AddMinutes(-2), tokens[1]);
        }

        [Fact]
        public void Every_failure_line_invokes_callback_with_error_code()
        {
            using var tmp = new TempDirectory();
            using var tracker = Build(tmp);
            var failures = new List<(string code, DateTime? ts)>();
            tracker.OnTokenFailureLine = (code, ts) => failures.Add((code, ts));

            tracker.ProcessLogMessageForTest(Failure, Now.AddMinutes(-5));
            tracker.ProcessLogMessageForTest(Failure, Now.AddMinutes(-4));

            Assert.Equal(2, failures.Count);
            Assert.All(failures, f => Assert.Equal("3399548929", f.code));
            Assert.Equal(Now.AddMinutes(-5), failures[0].ts);
        }

        [Fact]
        public void Historic_replay_lines_do_not_invoke_either_callback()
        {
            using var tmp = new TempDirectory();
            using var tracker = Build(tmp);
            var tokens = 0;
            var failures = 0;
            tracker.OnUserTokenAcquired = _ => tokens++;
            tracker.OnTokenFailureLine = (_, __) => failures++;

            tracker.ProcessLogMessageForTest(Success, Now.AddDays(-3));
            tracker.ProcessLogMessageForTest(Failure, Now.AddDays(-3));

            Assert.Equal(0, tokens);
            Assert.Equal(0, failures);
        }

        [Fact]
        public void Callback_exception_does_not_break_the_drain()
        {
            using var tmp = new TempDirectory();
            using var tracker = Build(tmp);
            tracker.OnUserTokenAcquired = _ => throw new InvalidOperationException("boom");
            tracker.OnTokenFailureLine = (_, __) => throw new InvalidOperationException("boom");

            tracker.ProcessLogMessageForTest(Success, Now.AddMinutes(-1));
            tracker.ProcessLogMessageForTest(Failure, Now.AddMinutes(-1));
        }
    }
}
