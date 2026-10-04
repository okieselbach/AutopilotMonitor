#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Registry second pillar for platform scripts (D-316). After IME reported a script batch to
    /// Intune it saves every run of the batch under
    /// <c>HKLM\SOFTWARE\Microsoft\IntuneManagementExtension\Policies\&lt;UserId&gt;\&lt;PolicyId&gt;</c>:
    /// <c>Result</c>, <c>ErrorCode</c> and <c>ResultDetails</c>, whose <c>ExecutionMsg</c> is what IME read
    /// from the result file of exactly that policy — the stdout when the run succeeded (IME's Success
    /// means an empty stderr file, whatever the exit code), the stderr when it failed with ErrorCode 99.
    /// The log tracker assigns a run's exit code and output by file position in AgentExecutor.log, where
    /// no policy id anchors them; this observer checks what was emitted against the keyed value and
    /// emits <c>script_output_reconciliation</c> when they disagree.
    /// <para>
    /// Snapshot-and-diff like <see cref="ImeRegistryAppStateObserver"/>, ticked by the same host: the
    /// first tick is the silent baseline, a later save is paired with its run through
    /// <see cref="PlatformScriptRunRegister"/>. No exit code lives in the registry: a run whose output
    /// matches proves the exit code of the same end block; a contradicted run loses its exit code.
    /// </para>
    /// </summary>
    internal sealed class ImeRegistryScriptResultObserver
    {
        public const string SourceLabel = "RegistryScriptResult";

        internal const string PoliciesKeyPath = ImeRegistryAppStateObserver.ImeRootKeyPath + @"\Policies";

        /// <summary>A save whose run never became claimable is dropped after this long (send failure of a later batch, agent restart).</summary>
        internal static readonly TimeSpan PendingExpiry = TimeSpan.FromMinutes(10);

        /// <summary>Safety cap on correction events per agent run; corrections beyond it are counted only.</summary>
        internal const int MaxCorrectionsPerSession = 100;

        private readonly PlatformScriptRunRegister _runs;
        private readonly Func<IReadOnlyDictionary<string, SavedScriptResult>> _snapshotSource;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger? _logger;
        private readonly IClock _clock;
        private readonly object _sync = new object();

        private IReadOnlyDictionary<string, SavedScriptResult>? _last;
        private readonly Dictionary<string, (SavedScriptResult Saved, DateTime SeenUtc)> _pending =
            new Dictionary<string, (SavedScriptResult, DateTime)>(StringComparer.OrdinalIgnoreCase);
        private int _correctionsEmitted;

        /// <param name="snapshotSource">Replaces the live registry walk in tests; per instance, so parallel test classes never share it.</param>
        public ImeRegistryScriptResultObserver(
            PlatformScriptRunRegister runs,
            InformationalEventPost post,
            AgentLogger? logger,
            IClock clock,
            Func<IReadOnlyDictionary<string, SavedScriptResult>>? snapshotSource = null)
        {
            _runs = runs ?? throw new ArgumentNullException(nameof(runs));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger;
            _snapshotSource = snapshotSource ?? ReadSnapshot;
        }

        /// <summary>One observation pass: snapshot → new saves → pair → judge → correct. Fail-soft: never throws.</summary>
        public void Tick(string reason)
        {
            try
            {
                lock (_sync)
                {
                    var snapshot = _snapshotSource();
                    var nowUtc = _clock.UtcNow;

                    if (_last == null)
                    {
                        // Saves that predate this agent run belong to runs it never emitted.
                        _last = snapshot;
                        _logger?.Info($"RegistryScriptResult: baseline captured ({snapshot.Count} saved platform-script results, trigger={reason})");
                        return;
                    }

                    foreach (var kv in snapshot)
                    {
                        if (_last.TryGetValue(kv.Key, out var previous) && previous.SameSaveAs(kv.Value)) continue;
                        _pending[kv.Key] = (kv.Value, nowUtc); // a newer save of the key replaces a waiting one
                    }
                    _last = snapshot;

                    ProcessPending(nowUtc);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"RegistryScriptResult: tick failed (fail-soft): {ex.Message}");
            }
        }

        private void ProcessPending(DateTime nowUtc)
        {
            foreach (var key in _pending.Keys.ToList())
            {
                var (saved, seenUtc) = _pending[key];
                var claim = _runs.TryClaim(saved.UserId, saved.PolicyId, seenUtc, out var run);
                if (claim == PlatformRunClaim.Wait)
                {
                    if (nowUtc - seenUtc >= PendingExpiry) _pending.Remove(key);
                    continue;
                }

                _pending.Remove(key);
                if (claim == PlatformRunClaim.Claimed && run != null) Reconcile(run, saved, nowUtc);
            }
        }

        private void Reconcile(PlatformScriptRun run, SavedScriptResult saved, DateTime nowUtc)
        {
            var judgement = PlatformOutputJudge.Judge(run, saved);
            switch (judgement.Verdict)
            {
                case PlatformOutputVerdict.Verified:
                    _runs.CountVerified();
                    _logger?.Debug($"RegistryScriptResult: platform script {run.PolicyId} output confirmed by IME's saved result");
                    return;
                case PlatformOutputVerdict.NotComparable:
                    _logger?.Debug($"RegistryScriptResult: platform script {run.PolicyId} not comparable ({judgement.Reason})");
                    return;
            }

            _runs.CountCorrected();
            if (_correctionsEmitted >= MaxCorrectionsPerSession)
            {
                _logger?.Warning($"RegistryScriptResult: correction for {run.PolicyId} not emitted — cap of {MaxCorrectionsPerSession} per agent run reached");
                return;
            }
            _correctionsEmitted++;

            var foreign = judgement.Verdict == PlatformOutputVerdict.Foreign;
            var culture = CultureInfo.InvariantCulture;
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["policyId"] = run.PolicyId,
                ["scriptType"] = "platform",
                ["outcome"] = foreign ? "foreign" : "repaired",
                ["reason"] = judgement.Reason,
            };
            if (!string.IsNullOrEmpty(run.RunId)) data["runId"] = run.RunId!;
            if (!string.IsNullOrEmpty(saved.Result)) data["result"] = saved.Result!;
            if (saved.ErrorCode.HasValue) data["registryErrorCode"] = saved.ErrorCode.Value.ToString(culture);
            // A present key replaces the emitted value, an empty one clears it; an absent key keeps it.
            if (judgement.Stdout != null) data["stdout"] = judgement.Stdout;
            if (judgement.Stderr != null) data["stderr"] = judgement.Stderr;
            if (foreign && run.ExitCode.HasValue) data["previousExitCode"] = run.ExitCode.Value.ToString(culture);

            var shortId = run.PolicyId.Length > 8 ? run.PolicyId.Substring(0, 8) : run.PolicyId;
            var message = foreign
                ? $"Platform script {shortId}: output corrected from IME's saved result; the log had attributed another script's output, exit code unverified"
                : $"Platform script {shortId}: output completed from IME's saved result";

            _post.Emit(
                eventType: SharedEventTypes.ScriptOutputReconciliation,
                source: SourceLabel,
                message: message,
                severity: foreign ? EventSeverity.Warning : EventSeverity.Info,
                immediateUpload: foreign,
                data: data,
                occurredAtUtc: nowUtc);

            if (foreign)
                _logger?.Warning($"RegistryScriptResult: platform script {run.PolicyId} run {run.RunId} carried another script's end block ({judgement.Reason}) — corrected from IME's saved result");
            else
                _logger?.Info($"RegistryScriptResult: platform script {run.PolicyId} run {run.RunId} output completed from IME's saved result ({judgement.Reason})");
        }

        // ── snapshot ────────────────────────────────────────────────────────────

        internal static string KeyOf(string userId, string policyId) => userId + "|" + policyId;

        internal static IReadOnlyDictionary<string, SavedScriptResult> ReadSnapshot()
        {
            var snapshot = new Dictionary<string, SavedScriptResult>(StringComparer.OrdinalIgnoreCase);
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var policies = baseKey.OpenSubKey(PoliciesKeyPath);
            if (policies == null) return snapshot;

            foreach (var userId in policies.GetSubKeyNames())
            {
                if (!Guid.TryParse(userId, out _)) continue;
                using var userKey = policies.OpenSubKey(userId);
                if (userKey == null) continue;

                foreach (var policyId in userKey.GetSubKeyNames())
                {
                    if (!Guid.TryParse(policyId, out _)) continue;
                    using var policyKey = userKey.OpenSubKey(policyId);
                    if (policyKey == null) continue;

                    snapshot[KeyOf(userId, policyId)] = new SavedScriptResult
                    {
                        UserId = userId,
                        PolicyId = policyId,
                        Result = policyKey.GetValue("Result") as string,
                        ErrorCode = TryReadInt(policyKey.GetValue("ErrorCode")),
                        ResultDetails = policyKey.GetValue("ResultDetails") as string,
                        LastUpdatedTimeUtc = policyKey.GetValue("LastUpdatedTimeUtc") as string,
                    };
                }
            }
            return snapshot;
        }

        private static int? TryReadInt(object? value)
        {
            switch (value)
            {
                case int i: return i;
                case long l when l >= int.MinValue && l <= int.MaxValue: return (int)l;
                case string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed): return parsed;
                default: return null;
            }
        }
    }

    /// <summary>One platform-script result as IME saved it under <c>Policies\&lt;UserId&gt;\&lt;PolicyId&gt;</c>.</summary>
    internal sealed class SavedScriptResult
    {
        public string UserId { get; set; } = string.Empty;
        public string PolicyId { get; set; } = string.Empty;
        public string? Result { get; set; }
        public int? ErrorCode { get; set; }
        public string? ResultDetails { get; set; }
        public string? LastUpdatedTimeUtc { get; set; }

        /// <summary>The same save: IME stamps every save with a new <c>LastUpdatedTimeUtc</c>.</summary>
        public bool SameSaveAs(SavedScriptResult other)
            => string.Equals(LastUpdatedTimeUtc, other.LastUpdatedTimeUtc, StringComparison.Ordinal)
            && string.Equals(Result, other.Result, StringComparison.Ordinal)
            && ErrorCode == other.ErrorCode
            && string.Equals(ResultDetails, other.ResultDetails, StringComparison.Ordinal);

        /// <summary>
        /// <c>ExecutionMsg</c> of the CryptoResult JSON in <c>ResultDetails</c> (IME 1.73 and later), or null
        /// when the value is not that JSON ("Hash mismatch", a launch failure).
        /// </summary>
        public string? ExecutionMessage()
        {
            if (string.IsNullOrWhiteSpace(ResultDetails)) return null;
            try
            {
                using var doc = JsonDocument.Parse(ResultDetails!);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                return doc.RootElement.TryGetProperty("ExecutionMsg", out var msg) && msg.ValueKind == JsonValueKind.String
                    ? msg.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    internal enum PlatformOutputVerdict
    {
        /// <summary>IME's saved result cannot confirm or contradict the run (no output saved, other failure kind).</summary>
        NotComparable,
        /// <summary>The emitted output is what IME saved: the end block, its exit code included, is the run's own.</summary>
        Verified,
        /// <summary>The log delivered the run's output incomplete; IME's saved text completes it, the exit code stands.</summary>
        Repaired,
        /// <summary>The emitted output contradicts IME's saved result: the end block was another executor's.</summary>
        Foreign,
    }

    internal sealed class PlatformOutputJudgement
    {
        public PlatformOutputVerdict Verdict { get; set; }
        public string Reason { get; set; } = string.Empty;
        /// <summary>Value to publish (Repaired/Foreign): null keeps the emitted stdout, empty clears it.</summary>
        public string? Stdout { get; set; }
        /// <summary>Value to publish (Repaired/Foreign): null keeps the emitted stderr, empty clears it.</summary>
        public string? Stderr { get; set; }
    }

    /// <summary>
    /// Compares a platform-script run as the tracker emitted it with IME's saved result. Comparable are
    /// a Success (ExecutionMsg = stdout; IME's Success means the stderr file was empty) and a Failed with
    /// ErrorCode 99 (ExecutionMsg = stderr). Both texts are compared after CR/LF normalisation and Trim;
    /// the tracker's own 8192-character cut is a prefix of the saved text.
    /// </summary>
    internal static class PlatformOutputJudge
    {
        internal const int ImeStderrErrorCode = 99;
        private const string OutputErrorSeparator = ", error =";

        public static PlatformOutputJudgement Judge(PlatformScriptRun run, SavedScriptResult saved)
        {
            var executionMessage = saved.ExecutionMessage();
            if (executionMessage == null) return NotComparable("no_execution_message");

            // The run's IME result line and the save come from the same IME run.
            if (string.Equals(run.ResultSource, "ime_policy_result", StringComparison.Ordinal)
                && !string.Equals(run.Result, saved.Result, StringComparison.OrdinalIgnoreCase))
                return NotComparable("result_differs");

            if (string.Equals(saved.Result, "Success", StringComparison.OrdinalIgnoreCase))
                return JudgeSuccess(run, Normalize(executionMessage)!);
            if (string.Equals(saved.Result, "Failed", StringComparison.OrdinalIgnoreCase) && saved.ErrorCode == ImeStderrErrorCode)
                return JudgeStderrFailure(run, Normalize(executionMessage)!);
            return NotComparable("no_saved_output");
        }

        private static PlatformOutputJudgement JudgeSuccess(PlatformScriptRun run, string savedStdout)
        {
            var stdout = NormalizeEmitted(run.Stdout, out var stdoutCut);
            var stderr = Normalize(run.Stderr);
            var stderrClean = string.IsNullOrEmpty(stderr);

            if (!string.IsNullOrEmpty(stdout) && Agrees(stdout!, stdoutCut, savedStdout))
                return stderrClean ? Verified() : Correct(PlatformOutputVerdict.Repaired, "stderr_cleared", savedStdout, string.Empty);

            if (stdout != null && stdout.Length == 0 && savedStdout.Length == 0)
                return stderrClean ? Verified() : Correct(PlatformOutputVerdict.Foreign, "stderr_on_success", string.Empty, string.Empty);

            if (stdout == null)
            {
                if (!stderrClean) return Correct(PlatformOutputVerdict.Foreign, "stderr_on_success", savedStdout, string.Empty);
                return savedStdout.Length > 0
                    ? Correct(PlatformOutputVerdict.Repaired, "stdout_missing", savedStdout, string.Empty)
                    : NotComparable("nothing_read");
            }

            // "write output done. output = X, error = Y" splits at the first ", error =" — a stdout that
            // contains the separator ends up partly in stderr. The saved text shows the cut right there.
            if (stdout.Length > 0 && !stdoutCut && !stderrClean
                && savedStdout.StartsWith(stdout, StringComparison.Ordinal)
                && savedStdout.Substring(stdout.Length).TrimStart().StartsWith(OutputErrorSeparator, StringComparison.Ordinal))
                return Correct(PlatformOutputVerdict.Repaired, "stdout_split", savedStdout, string.Empty);

            return Correct(PlatformOutputVerdict.Foreign, "stdout_differs", savedStdout, string.Empty);
        }

        private static PlatformOutputJudgement JudgeStderrFailure(PlatformScriptRun run, string savedStderr)
        {
            var stderr = NormalizeEmitted(run.Stderr, out var stderrCut);
            if (!string.IsNullOrEmpty(stderr) && Agrees(stderr!, stderrCut, savedStderr)) return Verified();

            // Nothing of the end block read: complete the stderr, the run's stdout stays as emitted.
            if (run.Stderr == null && run.Stdout == null)
                return Correct(PlatformOutputVerdict.Repaired, "stderr_missing", null, savedStderr);

            // The end block read said something else — an empty stderr included: IME read stderr from this
            // run's error file. The block's stdout is the other executor's as well.
            return Correct(PlatformOutputVerdict.Foreign, "stderr_differs", string.Empty, savedStderr);
        }

        private static bool Agrees(string emitted, bool emittedCut, string saved)
            => string.Equals(emitted, saved, StringComparison.Ordinal)
            || (emittedCut && saved.StartsWith(emitted, StringComparison.Ordinal));

        /// <summary>CR LF and lone CR to LF, then Trim — what the tracker's line reader and handlers make of a text.</summary>
        internal static string? Normalize(string? text)
            => text?.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

        private static string? NormalizeEmitted(string? text, out bool cut)
        {
            cut = false;
            if (text == null) return null;
            if (text.EndsWith(ImeLogTracker.OutputTruncationMarker, StringComparison.Ordinal))
            {
                cut = true;
                text = text.Substring(0, text.Length - ImeLogTracker.OutputTruncationMarker.Length);
            }
            return Normalize(text);
        }

        private static PlatformOutputJudgement Verified()
            => new PlatformOutputJudgement { Verdict = PlatformOutputVerdict.Verified, Reason = "match" };

        private static PlatformOutputJudgement NotComparable(string reason)
            => new PlatformOutputJudgement { Verdict = PlatformOutputVerdict.NotComparable, Reason = reason };

        private static PlatformOutputJudgement Correct(PlatformOutputVerdict verdict, string reason, string? stdout, string? stderr)
            => new PlatformOutputJudgement
            {
                Verdict = verdict,
                Reason = reason,
                // The tracker's own output cap: a corrected value is never longer than an emitted one.
                Stdout = stdout == null ? null : ImeLogTracker.TruncateOutput(stdout),
                Stderr = stderr == null ? null : ImeLogTracker.TruncateOutput(stderr),
            };
    }
}
