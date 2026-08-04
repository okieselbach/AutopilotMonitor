using System;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Hostile-input hardening for <see cref="CmTraceLogParser.TryParseLine"/>
    /// (CmTraceLogParser.cs:35-73). The parser runs on every IME log line — including partially
    /// flushed, BOM-prefixed, junk-prefixed and pathologically long lines written by Windows while
    /// the file is still being appended. It must never throw; it either returns a populated
    /// <see cref="CmTraceLogEntry"/> or reports a clean "does not match" (false). These pin CURRENT
    /// behaviour.
    /// </summary>
    public sealed class CmTraceLogParserTests
    {
        // Same well-formed CMTrace line shape as StallProbeActiveInstallFilterTests' helper.
        private static string CmTraceLine(string message, string time, string date) =>
            $"<![LOG[{message}]LOG]!><time=\"{time}\" date=\"{date}\" " +
            "component=\"AppEnforce\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";

        // ── Non-matching / malformed input → clean false, never a throw ───────

        [Theory]
        [InlineData(null)]                                     // null
        [InlineData("")]                                       // empty
        [InlineData("   ")]                                    // whitespace only
        [InlineData("<![LOG[truncated line with no closing")]  // truncated / missing fields
        [InlineData("garbage prefix <![LOG[msg]LOG]!><time=\"06:08:04.8834397\" date=\"2-8-2026\" component=\"C\" context=\"\" type=\"1\" thread=\"1\" file=\"\">")] // leading junk (non-ignorable) fails the StartsWith gate
        public void TryParseLine_returns_false_without_throwing_for_malformed_input(string? line)
        {
            var ok = CmTraceLogParser.TryParseLine(line, out var entry);

            Assert.False(ok);
            Assert.Null(entry);
        }

        // ── Leading BOM → still parses (BOM-tolerant) ────────────────────────

        [Fact]
        public void TryParseLine_parses_bom_prefixed_line()
        {
            // A U+FEFF BOM can prefix the first line of a freshly-opened log. It does NOT break
            // parsing: string.StartsWith (culture-sensitive) treats U+FEFF as an ignorable
            // character so the gate passes, and the unanchored regex matches the "<![LOG[..."
            // body after the BOM. This pins the BOM-tolerant behaviour.
            var line = "﻿" + CmTraceLine("msg", "06:08:04.8834397", "2-8-2026");

            var ok = CmTraceLogParser.TryParseLine(line, out var entry);

            Assert.True(ok);
            Assert.NotNull(entry);
            Assert.Equal("msg", entry.Message);
        }

        // ── Well-formed line → parses ────────────────────────────────────────

        [Fact]
        public void TryParseLine_parses_well_formed_line()
        {
            var line = CmTraceLine("EnforcementState: Installing app X", "06:08:04.8834397", "2-8-2026");

            var ok = CmTraceLogParser.TryParseLine(line, out var entry);

            Assert.True(ok);
            Assert.NotNull(entry);
            Assert.Equal("EnforcementState: Installing app X", entry.Message);
            Assert.Equal("AppEnforce", entry.Component);
            Assert.Equal(1, entry.Type);
            Assert.NotEqual(default, entry.Timestamp);
        }

        // ── Structurally valid but unparseable timestamp → UtcNow fallback ────

        [Fact]
        public void TryParseLine_falls_back_to_utcnow_for_unparseable_timestamp()
        {
            // time/date satisfy the regex character classes ([\d:.]+ / [\d-]+) so the line MATCHES,
            // but "13-45-2026" / "25:99:99" fail every DateTime format → the parser falls back to
            // DateTime.UtcNow (CmTraceLogParser.cs:57) instead of throwing.
            var before = DateTime.UtcNow;
            var line = CmTraceLine("some message", "25:99:99.0000000", "13-45-2026");

            var ok = CmTraceLogParser.TryParseLine(line, out var entry);
            var after = DateTime.UtcNow;

            Assert.True(ok);
            Assert.NotNull(entry);
            Assert.Equal("some message", entry.Message);
            // Don't assert an exact instant — only that the fallback stamped a fresh "now"
            // (a successfully-parsed date would land in 2026, far outside this window).
            Assert.InRange(entry.Timestamp, before.AddSeconds(-5), after.AddSeconds(5));
        }

        // ── UTC-bias suffix on the time field → honored, not dropped ──────────

        [Theory]
        [InlineData("+480", 8 * 60)]   // PST writer (UTC-8): UTC = local + 480min
        [InlineData("-060", -60)]      // UTC+1 writer: UTC = local - 60min
        public void TryParseLine_applies_utc_bias_suffix(string bias, int expectedOffsetMinutes)
        {
            // Session df1fcf47: a writer whose timezone differs from the agent's stamped its
            // lines with an explicit bias. Without bias handling the line either failed to
            // match at all (old regex) or would be misread in agent-local time.
            var line = CmTraceLine("msg", "11:46:19.0226610" + bias, "7-29-2026");

            var ok = CmTraceLogParser.TryParseLine(line, out var entry);

            Assert.True(ok);
            Assert.Equal(
                new DateTime(2026, 7, 29, 11, 46, 19, DateTimeKind.Utc).AddTicks(226610).AddMinutes(expectedOffsetMinutes),
                entry.Timestamp);
            Assert.Equal(DateTimeKind.Utc, entry.Timestamp.Kind);
        }

        // ── Oversized line → parses, no throw, no truncation of the message ───

        [Fact]
        public void TryParseLine_handles_oversized_line()
        {
            var hugeMessage = new string('x', 200_000);
            var line = CmTraceLine(hugeMessage, "06:08:04.8834397", "2-8-2026");

            var ok = CmTraceLogParser.TryParseLine(line, out var entry);

            Assert.True(ok);
            Assert.NotNull(entry);
            Assert.Equal(hugeMessage.Length, entry.Message.Length);
        }
    }
}
