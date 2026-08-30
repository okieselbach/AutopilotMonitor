using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Guards the shipped IME log-pattern pack (<c>rules/ime-log-patterns/*.json</c>, the same
    /// files combine.js embeds into agent and backend) against super-linear backtracking.
    /// <para>
    /// The IME Logs folder is writable by standard users and the SYSTEM tracker matches every
    /// appended line against every active pattern. Unanchored patterns with unbounded lazy gaps
    /// (<c>.*?</c>, <c>[^}]*?</c>, lookaheads) let one crafted line burn the full 1 s timeout per
    /// pattern; anchoring at the message start ('^') makes a non-matching pattern O(prefix), and
    /// bounding the nested scan in IME-APP-VERSION removes the remaining quadratic case.
    /// </para>
    /// Compiled with exactly the options <c>ImeLogTracker.CompilePatterns</c> uses.
    /// </summary>
    public sealed class ImeLogPatternPackLinearityTests
    {
        private const string GuidPattern = @"(?<id>[a-z0-9]{8}-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{12})";
        private const RegexOptions AgentOptions = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline;
        private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(1);

        // Generous for a loaded CI box; a super-linear pattern on these inputs takes the full
        // 1 s timeout (or throws), never tens of milliseconds.
        private const int MaxMillisPerMatch = 150;
        private const int HostileBytes = 512 * 1024;
        private const string Guid1 = "5c95bf94-1cf4-4629-88d1-3f616e7a405c";

        private static string FindRulesPatternDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "rules", "ime-log-patterns");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException($"Could not locate rules/ime-log-patterns walking up from {AppContext.BaseDirectory}");
        }

        private static List<ImeLogPattern> LoadAll()
        {
            var list = new List<ImeLogPattern>();
            foreach (var file in Directory.GetFiles(FindRulesPatternDir(), "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var p = JsonConvert.DeserializeObject<ImeLogPattern>(File.ReadAllText(file));
                Assert.NotNull(p);
                Assert.False(string.IsNullOrEmpty(p!.PatternId), $"{file}: patternId missing");
                Assert.False(string.IsNullOrEmpty(p.Pattern), $"{file}: pattern missing");
                list.Add(p);
            }
            Assert.True(list.Count >= 80, $"expected the full pack, found {list.Count}");
            return list;
        }

        private static Regex Compile(ImeLogPattern p)
            => new Regex(p.Pattern.Replace("{GUID}", GuidPattern), AgentOptions, AgentTimeout);

        public static IEnumerable<object[]> AllPatternIds()
            => LoadAll().Select(p => new object[] { p.PatternId });

        private static ImeLogPattern Get(string id)
            => LoadAll().Single(p => string.Equals(p.PatternId, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The literal run at the start of a pattern (after '^'), with regex escapes resolved —
        /// what an attacker would repeat to force a re-scan at every occurrence.
        /// </summary>
        internal static string LiteralPrefix(string pattern)
        {
            var sb = new StringBuilder();
            var i = pattern.StartsWith("^", StringComparison.Ordinal) ? 1 : 0;
            while (i < pattern.Length)
            {
                var c = pattern[i];
                if (c == '\\' && i + 1 < pattern.Length)
                {
                    var n = pattern[i + 1];
                    if (char.IsLetterOrDigit(n)) break; // \d, \w, \s… — a class, not a literal
                    sb.Append(n);
                    i += 2;
                    continue;
                }
                if ("(.*+?[{|$".IndexOf(c) >= 0) break;
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- invariants

        [Fact]
        public void Every_shipped_pattern_is_anchored_at_the_message_start()
        {
            var unanchored = LoadAll().Where(p => !p.Pattern.StartsWith("^", StringComparison.Ordinal)).Select(p => p.PatternId).ToList();
            Assert.True(unanchored.Count == 0,
                "Shipped IME patterns must start with '^' (the matcher runs them over attacker-appendable lines; " +
                "an unanchored pattern re-scans at every offset). Unanchored: " + string.Join(", ", unanchored));
        }

        [Fact]
        public void Every_shipped_pattern_compiles_with_the_agent_options()
        {
            foreach (var p in LoadAll())
                Compile(p);
        }

        [Fact]
        public void No_shipped_pattern_uses_an_unbounded_negated_class_scan()
        {
            // '[^x]*?' after a lazy gap is the chained-quantifier shape that stays quadratic even
            // when anchored (IME-APP-VERSION before the fix). Bounded forms '[^x]{0,N}?' are fine.
            var offenders = LoadAll()
                .Where(p => Regex.IsMatch(p.Pattern, @"\[\^[^\]]+\]\*\??"))
                .Select(p => p.PatternId).ToList();
            Assert.True(offenders.Count == 0, "Unbounded negated-class scans: " + string.Join(", ", offenders));
        }

        // ---------------------------------------------------------------- hostile inputs

        private static IEnumerable<string> HostileInputsFor(ImeLogPattern p)
        {
            var prefix = LiteralPrefix(p.Pattern);
            if (prefix.Length == 0) prefix = "[Win32App] ";

            // (1) The prefix repeated: m start positions × O(n) tail scan for an unanchored pattern.
            var repeated = new StringBuilder(HostileBytes + prefix.Length);
            while (repeated.Length < HostileBytes) repeated.Append(prefix);
            yield return repeated.ToString();

            // (2) Prefix + GUID + repeated inner trigger with no closing brace / NewValue (APP-VERSION shape).
            var inner = new StringBuilder(HostileBytes);
            inner.Append(prefix).Append(Guid1);
            while (inner.Length < HostileBytes) inner.Append("\"DetectedIdentityVersion\":{x");
            yield return inner.ToString();

            // (3) Prefix once, then a long run of word chars (greedy \w+/[^"]+ backtracking shape).
            yield return prefix + Guid1 + new string('a', HostileBytes);

            // (4) Prefix repeated with newlines and DO-timeout words but the trailing literal missing.
            var mixed = new StringBuilder(HostileBytes);
            while (mixed.Length < HostileBytes) mixed.Append(prefix).Append(" not finished timeout\n");
            yield return mixed.ToString();
        }

        [Theory]
        [MemberData(nameof(AllPatternIds))]
        public void Pattern_completes_in_linear_time_on_hostile_lines(string patternId)
        {
            var p = Get(patternId);
            var regex = Compile(p);

            var n = 0;
            foreach (var input in HostileInputsFor(p))
            {
                n++;
                var best = TimeBestOfThree(regex, input, () => $"{patternId}: regex timeout on hostile input #{n} ({input.Length} chars)");
                Assert.True(best < MaxMillisPerMatch,
                    $"{patternId}: hostile input #{n} ({input.Length} chars) took {best} ms — super-linear backtracking");
            }
        }

        /// <summary>
        /// Best of three timed runs after a warm-up call: the first Match of a Compiled regex
        /// JIT-compiles its generated IL, and a loaded CI box adds scheduling noise — neither is
        /// backtracking. A quadratic pattern is not rescued by a minimum: it takes the full
        /// timeout every run.
        /// </summary>
        private static long TimeBestOfThree(Regex regex, string input, Func<string> timeoutMessage)
        {
            regex.Match("warm-up");
            long best = long.MaxValue;
            for (var run = 0; run < 3; run++)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    regex.Match(input);
                }
                catch (RegexMatchTimeoutException)
                {
                    Assert.Fail(timeoutMessage());
                }
                sw.Stop();
                best = Math.Min(best, sw.ElapsedMilliseconds);
            }
            return best;
        }

        [Fact]
        public void The_reported_quadratic_APP_VERSION_input_is_now_fast()
        {
            // The finding's concrete input: prefix + GUID + ~20,000 × '"DetectedIdentityVersion":{x'.
            var p = Get("IME-APP-VERSION");
            var regex = Compile(p);
            var sb = new StringBuilder();
            sb.Append("[Win32App][ReportingManager] Detection state for app with id: ").Append(Guid1);
            for (var i = 0; i < 20000; i++) sb.Append("\"DetectedIdentityVersion\":{x");

            var input = sb.ToString();
            Assert.False(regex.Match(input).Success);
            var best = TimeBestOfThree(regex, input, () => "regex timeout on the reported input");
            Assert.True(best < MaxMillisPerMatch, $"took {best} ms");
        }

        // ---------------------------------------------------------------- real-line fidelity

        /// <summary>
        /// Representative real IME messages (GUIDs/names neutralized) for the patterns whose
        /// regex changed shape: the anchored pack must still match them with the same captures.
        /// </summary>
        public static IEnumerable<object[]> RealLines()
        {
            yield return new object[]
            {
                "IME-APP-VERSION",
                "[Win32App][ReportingManager] Detection state for app with id: " + Guid1 + " has been updated. Report delta: {\"DetectionState\":{\"OldValue\":\"NotInstalled\",\"NewValue\":\"Installed\"},\"DetectedIdentityVersion\":{\"OldValue\":null,\"NewValue\":\"11.2.1952.0\"}}",
                new[] { "id=" + Guid1, "appVersion=11.2.1952.0" }
            };
            yield return new object[]
            {
                "IME-ESP-APP-REGISTERED",
                "[Win32App][EspManager] In EspPhase: DeviceSetup. App " + Guid1 + " has been registered. App name: Company Portal",
                new[] { "id=" + Guid1, "name=Company Portal" }
            };
            yield return new object[]
            {
                "IME-CACHE-MISS",
                "[Win32App] Content cache miss for app (id = " + Guid1 + ", name = Microsoft Teams - Autoupdate), start downloading...",
                new[] { "id=" + Guid1, "name=Microsoft Teams - Autoupdate" }
            };
            yield return new object[]
            {
                "IME-DOWNLOADING",
                "[StatusService] Downloading app (id = " + Guid1 + ", name Company Portal) via WinGet, bytes 0/100 for user 00000000-0000-0000-0000-000000000000",
                new[] { "id=" + Guid1, "tech=WinGet", "bytes=0", "ofbytes=100" }
            };
            yield return new object[]
            {
                "IME-APP-TYPE-WINGET",
                "[Win32App][WinGetApp][WinGetAppDetectionExecutor] Starting detection of app with id: " + Guid1 + " and context: SystemContext.",
                new[] { "id=" + Guid1 }
            };
            yield return new object[]
            {
                "IME-APP-TYPE-MSI",
                "[Win32App] Autopilot ESP phase: DeviceSetup. MSI App " + Guid1 + " reset retriable error codes MaxRetries to 15 times and RetryIntervalInMinutes to 3 min for this check-in",
                new[] { "id=" + Guid1 }
            };
            yield return new object[]
            {
                "IME-SET-CURRENT-4",
                @"[Win32App] SetCurrentDirectory: C:\Windows\IMECache\" + Guid1 + "_1",
                new[] { "id=" + Guid1 }
            };
            yield return new object[]
            {
                "IME-ERROR-REPORT",
                "[Win32App][ReportingManager] Execution state for app with id: " + Guid1 + " has been updated. Report delta: {\"EnforcementState\":{\"OldValue\":null,\"NewValue\":\"InProgress\"}}",
                new[] { "id=" + Guid1, "to=InProgress" }
            };
            yield return new object[]
            {
                "IME-TOKEN-FAILURE",
                "Failed to get AAD token. len = 34 using client id fc0f3af4-6835-4174-b806-f7db311fd2f3 and resource id 26a4ae64-5862-427f-a9b0-044e62572a4f, errorCode = 3399548929",
                new[] { "errorCode=3399548929" }
            };
            yield return new object[]
            {
                "IME-ESP-TRACK-STATUS",
                "[Win32App][EspManager] Updating ESP tracked install status from NotInstalled to InProgress for application " + Guid1 + " with name: Company Portal.",
                new[] { "from=NotInstalled", "to=InProgress", "id=" + Guid1 }
            };
            yield return new object[]
            {
                "PS-AGENT-OUTPUT",
                "write output done. output = OK. Hosts cleaned.\n\n, error = \n",
                new[] { "output=OK. Hosts cleaned.\n\n", "error=\n" }
            };
            yield return new object[]
            {
                "PS-SCRIPT-GENERATED",
                @"Script file C:\Program Files (x86)\Microsoft Intune Management Extension\Policies\Scripts\00000000-0000-0000-0000-000000000000_6269c24c-0a00-455b-b0b5-60e2e9026723.ps1 is generated.",
                new[] { "id=6269c24c-0a00-455b-b0b5-60e2e9026723" }
            };
            yield return new object[]
            {
                "IME-NOT-TARGETED-1",
                "[Win32App][ActionProcessor] App with id: " + Guid1 + ", targeted intent: RequiredInstall, and enforceability: Enforceable has projected enforcement classification: NotApplicableOrNotTargeted with desired state: None. Current state is:\nDetection = NotDetected",
                new[] { "id=" + Guid1 }
            };
            yield return new object[]
            {
                "IME-DETECTION-RESULT",
                "[Win32App][DetectionActionHandler] Detection for policy with id: " + Guid1 + " resulted in action status: Success and detection state: NotDetected.",
                new[] { "id=" + Guid1, "detection=NotDetected" }
            };
            yield return new object[]
            {
                "IME-ERROR-UNMAPPED-EXIT",
                "[Win32App] Admin did NOT set mapping for lpExitCode: 1603 of app: " + Guid1,
                new[] { "exitCode=1603", "id=" + Guid1 }
            };
            yield return new object[]
            {
                "IME-GRS-SKIP",
                "[Win32App][ActionProcessor] App with id: " + Guid1 + " to install is in GRS. The app will not be enforced.",
                new[] { "id=" + Guid1 }
            };
            yield return new object[]
            {
                "IME-ERROR-ENFORCEMENT",
                "[Win32App] Setting enforcementState as: Error with lpExitCode: 1603 without mapping",
                new string[0]
            };
            yield return new object[]
            {
                "PS-SCRIPT-CONTEXT",
                "Launch powershell executor in machine session",
                new[] { "context=machine" }
            };
            yield return new object[]
            {
                "IME-DO-TIMEOUT-2",
                "[Win32App DO] DO download is not finished after 600 seconds, timeout",
                new string[0]
            };
        }

        [Theory]
        [MemberData(nameof(RealLines))]
        public void Anchored_pattern_still_matches_the_real_line_with_the_same_captures(string patternId, string message, string[] expectedGroups)
        {
            var regex = Compile(Get(patternId));
            var m = regex.Match(message);
            Assert.True(m.Success, $"{patternId} no longer matches: {message}");
            Assert.Equal(0, m.Index);
            foreach (var expected in expectedGroups)
            {
                var eq = expected.IndexOf('=');
                var group = expected.Substring(0, eq);
                var value = expected.Substring(eq + 1);
                Assert.Equal(value, m.Groups[group].Value);
            }
        }

        [Fact]
        public void PS_SCRIPT_CONTEXT_no_longer_fires_on_Win32App_detection_script_lines()
        {
            // Before anchoring, the unanchored pattern matched these AppWorkload.log lines mid-message
            // (122 hits in 8 real diagnostics sets) and HandleScriptContext overwrote the pending
            // PLATFORM script's RunContext with a Win32 detection script's context.
            var regex = Compile(Get("PS-SCRIPT-CONTEXT"));
            Assert.DoesNotMatch(regex, "[Win32App] SideCarScriptDetectionManager Launch powershell executor in machine session");
            Assert.DoesNotMatch(regex, "[Win32App] SideCarScriptRequirementManager Launch powershell executor in machine session");
        }
    }
}
