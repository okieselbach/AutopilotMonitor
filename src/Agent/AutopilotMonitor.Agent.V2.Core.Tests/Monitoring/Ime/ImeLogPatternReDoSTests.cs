using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Backend-supplied IME log patterns are untrusted regex delivered on the config channel — a
    /// bad (or malicious) tenant/global pattern must never crash the tracker. Two defences are
    /// pinned here:
    /// <list type="bullet">
    /// <item>compile-time: an INVALID pattern is swallowed in <see cref="ImeLogTracker.CompilePatterns"/>
    /// (ImeLogTracker.cs:452-487) — the tracker constructs, valid siblings still compile, and the
    /// tracker stays usable.</item>
    /// <item>match-time: every pattern is compiled with a 1&#160;s <c>MatchTimeout</c>
    /// (ImeLogTracker.cs:458); a catastrophic-backtracking pattern + adversarial input raises
    /// <see cref="System.Text.RegularExpressions.RegexMatchTimeoutException"/>, which the per-line
    /// match loop catches (ImeLogTracker.LogProcessing.cs:177-180 / the ProcessLogMessageForTest
    /// seam:482-489) rather than propagating.</item>
    /// </list>
    /// </summary>
    public sealed class ImeLogPatternReDoSTests
    {
        private static ImeLogPattern Pattern(string id, string regex, string? action = null) => new ImeLogPattern
        {
            PatternId = id,
            Pattern = regex,
            Category = "always",
            // Action is optional on the wire (absent = null); the model declares it non-nullable.
            Action = action!,
            Enabled = true,
            Parameters = new Dictionary<string, string>(),
        };

        private static ImeLogTracker BuildTracker(TempDirectory tmp, List<ImeLogPattern> patterns) =>
            new ImeLogTracker(
                logFolder: tmp.Path,
                patterns: patterns,
                logger: new AgentLogger(tmp.Path, AgentLogLevel.Info));

        // ── Compile-time swallow ─────────────────────────────────────────────

        [Theory]
        [InlineData("(")]              // unbalanced group
        [InlineData("[a-")]            // unterminated character class
        [InlineData("(?<name>")]       // unterminated named group
        [InlineData("*invalid")]       // quantifier with nothing to quantify
        [InlineData("\\")]             // dangling escape
        public void Invalid_backend_pattern_is_swallowed_and_tracker_stays_usable(string badRegex)
        {
            using var tmp = new TempDirectory();

            // Construction compiles the patterns; the bad one must not surface as an exception.
            using var tracker = BuildTracker(tmp, new List<ImeLogPattern> { Pattern("BAD", badRegex) });

            // And the tracker is still functional — driving a line through the (empty) active-pattern
            // set is a no-op, not a throw.
            var ex = Record.Exception(() => tracker.ProcessLogMessageForTest("any line here"));
            Assert.Null(ex);
        }

        [Fact]
        public void Valid_patterns_still_compile_when_a_sibling_pattern_is_invalid()
        {
            using var tmp = new TempDirectory();

            var fired = false;
            using var tracker = BuildTracker(tmp, new List<ImeLogPattern>
            {
                Pattern("BAD", "("),                                        // dropped at compile
                Pattern("IME-START", "IME Agent Started", action: "imeStarted"), // survives
            });
            tracker.OnImeStarted = () => fired = true;

            tracker.ProcessLogMessageForTest("Info: IME Agent Started (v1.2.3)");

            // The invalid sibling did not poison the batch — the valid pattern compiled and fired.
            Assert.True(fired);
        }

        // ── Match-time timeout swallow (ReDoS) ───────────────────────────────

        [Fact]
        public void Catastrophic_backtracking_pattern_hits_match_timeout_and_is_swallowed()
        {
            using var tmp = new TempDirectory();

            // Classic exponential-backtracking pattern; the trailing non-matching char forces the
            // engine to explore the full 2^n search space, which blows past the 1 s MatchTimeout.
            using var tracker = BuildTracker(tmp, new List<ImeLogPattern>
            {
                Pattern("REDOS", "^(a+)+$"),
            });

            var adversarialInput = new string('a', 40) + "!";

            // The RegexMatchTimeoutException raised inside the match loop must be caught, not
            // propagated — the tracker survives a hostile backend pattern + hostile log line.
            var ex = Record.Exception(() => tracker.ProcessLogMessageForTest(adversarialInput));
            Assert.Null(ex);
        }
    }
}
