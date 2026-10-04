#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using Xunit;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// D-316: IME saves every platform script's result under Policies\&lt;user&gt;\&lt;policy&gt; after reporting the
    /// batch; the observer checks the tracker's emitted run against it. The field case (session 756970cf): the run
    /// had been given a Win32 detection script's end block — exit 1 and "Detection Started" output — while IME saved
    /// the bootstrap output of the platform script itself.
    /// </summary>
    public sealed class ImeRegistryScriptResultObserverTests
    {
        private const string DeviceUser = "00000000-0000-0000-0000-000000000000";
        private const string PolicyA = "1d6f0a52-3c4e-4b7a-9e21-5f8c7d2b9a10";
        private const string PolicyB = "2b7d9e41-8c3a-4f60-a5d2-9e1b7c4f3a08";
        private const string OwnStdout = "=== bootstrap loader started ===\nInstaller finished (exit code 0).";
        private const string ForeignStdout = "VERBOSE: === Sample Suite Detection Started ===\nWARNING: === DETECTION FAILED ===";
        private static readonly DateTime T0 = new DateTime(2026, 10, 3, 20, 59, 0, DateTimeKind.Utc);

        // ── PlatformOutputJudge ─────────────────────────────────────────────────

        private static PlatformScriptRun Run(string? stdout, string? stderr = null, int? exitCode = 0, string result = "Success", string resultSource = "ime_policy_result")
            => new PlatformScriptRun { PolicyId = PolicyA, UserId = DeviceUser, RunId = "run-1", Emitted = true, Result = result, ResultSource = resultSource, ExitCode = exitCode, Stdout = stdout, Stderr = stderr };

        private static string Details(string executionMessage)
            => "{\"Version\":1,\"SigningCode\":15,\"EncryptionCode\":19,\"ExecutionMsg\":" + System.Text.Json.JsonSerializer.Serialize(executionMessage) + "}";

        private static SavedScriptResult Saved(string? executionMessage, string result = "Success", int errorCode = 0, string user = DeviceUser, string policy = PolicyA, string stamp = "10/3/2026 9:00:00 PM")
            => new SavedScriptResult
            {
                UserId = user,
                PolicyId = policy,
                Result = result,
                ErrorCode = errorCode,
                ResultDetails = executionMessage == null ? null : Details(executionMessage),
                LastUpdatedTimeUtc = stamp,
            };

        [Fact]
        public void Field_case_detection_output_on_the_platform_run_is_foreign_and_the_saved_stdout_replaces_it()
        {
            var judgement = PlatformOutputJudge.Judge(Run(ForeignStdout, string.Empty, exitCode: 1), Saved(OwnStdout.Replace("\n", "\r\n")));
            Assert.Equal(PlatformOutputVerdict.Foreign, judgement.Verdict);
            Assert.Equal("stdout_differs", judgement.Reason);
            Assert.Equal(OwnStdout, judgement.Stdout);
            Assert.Equal(string.Empty, judgement.Stderr);
        }

        [Fact]
        public void Matching_output_is_verified_across_line_ending_and_trim_differences()
        {
            var judgement = PlatformOutputJudge.Judge(Run(OwnStdout, string.Empty), Saved("  " + OwnStdout.Replace("\n", "\r\n") + "\r\n"));
            Assert.Equal(PlatformOutputVerdict.Verified, judgement.Verdict);
        }

        [Fact]
        public void Output_cut_by_the_tracker_is_verified_against_the_longer_saved_text()
        {
            var full = string.Concat(Enumerable.Repeat("line of output\n", 1000)).TrimEnd();
            var emitted = ImeLogTracker.TruncateOutput(full);
            Assert.EndsWith(ImeLogTracker.OutputTruncationMarker, emitted);
            Assert.Equal(PlatformOutputVerdict.Verified, PlatformOutputJudge.Judge(Run(emitted), Saved(full)).Verdict);
        }

        [Fact]
        public void Both_empty_is_verified_but_stderr_on_a_success_is_another_executors()
        {
            Assert.Equal(PlatformOutputVerdict.Verified, PlatformOutputJudge.Judge(Run(string.Empty), Saved(string.Empty)).Verdict);
            var judgement = PlatformOutputJudge.Judge(Run(string.Empty, "Get-Item : not found"), Saved(string.Empty));
            Assert.Equal(PlatformOutputVerdict.Foreign, judgement.Verdict);
            Assert.Equal("stderr_on_success", judgement.Reason);
        }

        [Fact]
        public void Explicitly_empty_emitted_output_against_saved_text_is_foreign()
        {
            Assert.Equal(PlatformOutputVerdict.Foreign, PlatformOutputJudge.Judge(Run(string.Empty), Saved(OwnStdout)).Verdict);
            Assert.Equal(PlatformOutputVerdict.Foreign, PlatformOutputJudge.Judge(Run(OwnStdout), Saved(string.Empty)).Verdict);
        }

        [Fact]
        public void Output_never_read_is_completed_from_the_saved_text_and_the_exit_code_stands()
        {
            var judgement = PlatformOutputJudge.Judge(Run(null), Saved(OwnStdout));
            Assert.Equal(PlatformOutputVerdict.Repaired, judgement.Verdict);
            Assert.Equal("stdout_missing", judgement.Reason);
            Assert.Equal(OwnStdout, judgement.Stdout);
            Assert.Equal(PlatformOutputVerdict.NotComparable, PlatformOutputJudge.Judge(Run(null), Saved(string.Empty)).Verdict);
        }

        [Fact]
        public void Stdout_containing_the_output_error_separator_is_repaired_not_foreign()
        {
            // "write output done. output = X, error = " with X = "Copying files, error = 0 errors\nDone": the
            // lazy split leaves "Copying files" as stdout and the rest as stderr.
            var judgement = PlatformOutputJudge.Judge(
                Run("Copying files", "0 errors\nDone, error ="),
                Saved("Copying files, error = 0 errors\r\nDone"));
            Assert.Equal(PlatformOutputVerdict.Repaired, judgement.Verdict);
            Assert.Equal("stdout_split", judgement.Reason);
            Assert.Equal("Copying files, error = 0 errors\nDone", judgement.Stdout);
            Assert.Equal(string.Empty, judgement.Stderr);
        }

        [Fact]
        public void A_prefix_without_the_split_explanation_is_foreign()
        {
            Assert.Equal(PlatformOutputVerdict.Foreign, PlatformOutputJudge.Judge(Run("Detected"), Saved("Detected and repaired")).Verdict);
        }

        [Fact]
        public void Failed_with_stderr_is_judged_on_stderr()
        {
            Assert.Equal(PlatformOutputVerdict.Verified,
                PlatformOutputJudge.Judge(Run("partial", "Access denied", 1, "Failed"), Saved("Access denied\r\n", "Failed", 99)).Verdict);

            var missing = PlatformOutputJudge.Judge(Run(null, null, 1, "Failed"), Saved("Access denied", "Failed", 99));
            Assert.Equal(PlatformOutputVerdict.Repaired, missing.Verdict);
            Assert.Null(missing.Stdout);
            Assert.Equal("Access denied", missing.Stderr);

            var foreign = PlatformOutputJudge.Judge(Run("Detected", string.Empty, 0, "Failed"), Saved("Access denied", "Failed", 99));
            Assert.Equal(PlatformOutputVerdict.Foreign, foreign.Verdict);
            Assert.Equal(string.Empty, foreign.Stdout);
            Assert.Equal("Access denied", foreign.Stderr);
        }

        [Fact]
        public void Results_without_saved_output_or_from_another_run_are_not_compared()
        {
            Assert.Equal("no_saved_output", PlatformOutputJudge.Judge(Run(OwnStdout, null, 1, "Failed"), Saved("1", "Failed", 1)).Reason);
            var hashMismatch = new SavedScriptResult { UserId = DeviceUser, PolicyId = PolicyA, Result = "Failed", ErrorCode = 2, ResultDetails = "Hash mismatch" };
            Assert.Equal("no_execution_message", PlatformOutputJudge.Judge(Run(OwnStdout, null, 1, "Failed"), hashMismatch).Reason);
            Assert.Equal("result_differs", PlatformOutputJudge.Judge(Run(OwnStdout), Saved("Access denied", "Failed", 99)).Reason);
            // A result derived from the exit code (fallback) is not IME's — the saved one is.
            Assert.Equal(PlatformOutputVerdict.Verified,
                PlatformOutputJudge.Judge(Run(OwnStdout, null, 1, "Failed", "agentexecutor_fallback"), Saved(OwnStdout)).Verdict);
        }

        // ── PlatformScriptRunRegister ───────────────────────────────────────────

        private static ScriptExecutionState Emitted(string policyId, string? stdout, int? exitCode = 0, string? userId = DeviceUser, string runId = "run-1")
            => new ScriptExecutionState { PolicyId = policyId, ScriptType = "platform", UserId = userId, RunId = runId, Result = "Success", ResultSource = "ime_policy_result", ExitCode = exitCode, Stdout = stdout };

        [Fact]
        public void A_save_is_paired_only_after_the_tracker_read_past_it_and_the_run_was_emitted()
        {
            var runs = new PlatformScriptRunRegister();
            runs.NoteStarted(DeviceUser, PolicyA, T0);
            var seen = T0.AddSeconds(30);

            Assert.Equal(PlatformRunClaim.Wait, runs.TryClaim(DeviceUser, PolicyA, seen, out _));
            runs.NotePassCompleted(seen.AddSeconds(1));
            Assert.Equal(PlatformRunClaim.Wait, runs.TryClaim(DeviceUser, PolicyA, seen, out _)); // pass started too early
            runs.NotePassCompleted(seen + PlatformScriptRunRegister.TrackerCatchUp);
            Assert.Equal(PlatformRunClaim.Wait, runs.TryClaim(DeviceUser, PolicyA, seen, out _)); // run not emitted yet

            runs.NoteEmitted(Emitted(PolicyA, OwnStdout), T0.AddSeconds(40));
            Assert.Equal(PlatformRunClaim.Claimed, runs.TryClaim(DeviceUser, PolicyA, seen, out var run));
            Assert.Equal("run-1", run!.RunId);
            Assert.Equal(OwnStdout, run.Stdout);
            Assert.Equal(PlatformRunClaim.None, runs.TryClaim(DeviceUser, PolicyA, seen, out _)); // never twice
        }

        [Fact]
        public void A_new_start_replaces_the_latest_run_of_its_key_and_other_users_stay_apart()
        {
            var runs = new PlatformScriptRunRegister();
            runs.NoteStarted(DeviceUser, PolicyA, T0);
            runs.NoteEmitted(Emitted(PolicyA, "first run"), T0.AddSeconds(5));
            runs.NoteStarted(DeviceUser, PolicyA, T0.AddMinutes(30));
            runs.NoteEmitted(Emitted(PolicyA, "second run", runId: "run-2"), T0.AddMinutes(30).AddSeconds(5));
            const string user = "6c3f8a20-1d5e-4b97-8f4a-0e2d6b9c7a13";
            runs.NoteStarted(user, PolicyA, T0.AddMinutes(31));
            runs.NotePassCompleted(T0.AddHours(1));

            Assert.Equal(PlatformRunClaim.Claimed, runs.TryClaim(DeviceUser, PolicyA, T0.AddMinutes(31), out var run));
            Assert.Equal("second run", run!.Stdout);
            Assert.Equal(PlatformRunClaim.Wait, runs.TryClaim(user, PolicyA, T0.AddMinutes(32), out _));
            Assert.Equal(PlatformRunClaim.None, runs.TryClaim(DeviceUser, PolicyB, T0.AddMinutes(32), out _));
        }

        [Fact]
        public void A_run_without_a_readable_user_id_is_claimed_by_its_policy()
        {
            var runs = new PlatformScriptRunRegister();
            runs.NoteStarted(null, PolicyA, T0);
            runs.NoteEmitted(Emitted(PolicyA, OwnStdout, userId: null), T0.AddSeconds(5));
            runs.NotePassCompleted(T0.AddMinutes(5));
            Assert.Equal(PlatformRunClaim.Claimed, runs.TryClaim(DeviceUser, PolicyA, T0.AddMinutes(1), out _));
        }

        [Fact]
        public void Register_keeps_the_newest_runs_and_its_counters_report_each_change_once()
        {
            var runs = new PlatformScriptRunRegister();
            for (var i = 0; i <= PlatformScriptRunRegister.MaxRuns; i++)
                runs.NoteStarted(DeviceUser, Guid.NewGuid().ToString("D"), T0.AddSeconds(i));
            runs.NoteStarted(DeviceUser, PolicyA, T0.AddHours(1));
            runs.NoteEmitted(Emitted(PolicyA, OwnStdout), T0.AddHours(1));
            runs.NotePassCompleted(T0.AddHours(2));
            Assert.Equal(PlatformRunClaim.Claimed, runs.TryClaim(DeviceUser, PolicyA, T0.AddHours(1), out _));

            Assert.False(runs.ConsumeCountersChanged());
            runs.CountVerified();
            runs.CountCorrected();
            Assert.True(runs.ConsumeCountersChanged());
            Assert.False(runs.ConsumeCountersChanged());
            Assert.Equal(1, runs.Verified);
            runs.RestoreCounters(7, 3);
            Assert.Equal(7, runs.Verified);
            Assert.Equal(3, runs.Corrected);
        }

        // ── observer flow ───────────────────────────────────────────────────────

        private sealed class Fixture
        {
            public readonly FakeSignalIngressSink Sink = new FakeSignalIngressSink();
            public readonly VirtualClock Clock = new VirtualClock(T0);
            public readonly PlatformScriptRunRegister Runs = new PlatformScriptRunRegister();
            public Dictionary<string, SavedScriptResult> Registry = new Dictionary<string, SavedScriptResult>(StringComparer.OrdinalIgnoreCase);
            public readonly ImeRegistryScriptResultObserver Observer;

            public Fixture()
            {
                Observer = new ImeRegistryScriptResultObserver(Runs, new InformationalEventPost(Sink, Clock), null, Clock,
                    () => new Dictionary<string, SavedScriptResult>(Registry, StringComparer.OrdinalIgnoreCase));
            }

            public void Save(SavedScriptResult saved) => Registry[ImeRegistryScriptResultObserver.KeyOf(saved.UserId, saved.PolicyId)] = saved;

            /// <summary>The tracker emitted a run, then kept polling past the save.</summary>
            public void EmitRun(string policyId, string? stdout, int? exitCode, string runId)
            {
                Runs.NoteStarted(DeviceUser, policyId, Clock.UtcNow);
                Runs.NoteEmitted(Emitted(policyId, stdout, exitCode, runId: runId), Clock.UtcNow);
            }

            public void TrackerCaughtUp() => Runs.NotePassCompleted(Clock.UtcNow);

            public IReadOnlyList<FakeSignalIngressSink.PostedSignal> Corrections()
                => Sink.Posted.Where(p => p.Payload != null
                    && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                    && et == SharedEventTypes.ScriptOutputReconciliation).ToList();
        }

        [Fact]
        public void Field_case_end_to_end_the_save_corrects_the_run_with_a_warning_naming_it()
        {
            var f = new Fixture();
            f.Save(Saved("an older run of another policy", policy: PolicyB, stamp: "10/2/2026 8:00:00 AM"));
            f.Observer.Tick("baseline");

            f.EmitRun(PolicyA, ForeignStdout, 1, "run-a");
            f.Save(Saved(OwnStdout));
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Observer.Tick("registry_change");
            Assert.Empty(f.Corrections()); // the tracker has not read past the save yet

            f.Clock.Advance(PlatformScriptRunRegister.TrackerCatchUp);
            f.TrackerCaughtUp();
            f.Observer.Tick("periodic");

            var correction = Assert.Single(f.Corrections());
            var data = correction.Payload!;
            Assert.Equal(PolicyA, data["policyId"]);
            Assert.Equal("platform", data["scriptType"]);
            Assert.Equal("run-a", data["runId"]);
            Assert.Equal("foreign", data["outcome"]);
            Assert.Equal("stdout_differs", data["reason"]);
            Assert.Equal("Success", data["result"]);
            Assert.Equal(OwnStdout, data["stdout"]);
            Assert.Equal(string.Empty, data["stderr"]);
            Assert.Equal("1", data["previousExitCode"]);
            Assert.Equal("0", data["registryErrorCode"]);
            Assert.Equal("Warning", data[SignalPayloadKeys.Severity]);
            Assert.Equal("true", data[SignalPayloadKeys.ImmediateUpload]);
            Assert.DoesNotContain("exitCode", data.Keys);
            Assert.Equal(1, f.Runs.Corrected);
            Assert.Equal(0, f.Runs.Verified);

            // The same save read again changes nothing.
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            f.TrackerCaughtUp();
            f.Observer.Tick("periodic");
            Assert.Single(f.Corrections());
        }

        [Fact]
        public void Confirmed_output_is_counted_and_emits_nothing()
        {
            var f = new Fixture();
            f.Observer.Tick("baseline");
            f.EmitRun(PolicyA, OwnStdout, 0, "run-a");
            f.Save(Saved(OwnStdout));
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Observer.Tick("registry_change");
            f.Clock.Advance(TimeSpan.FromSeconds(3));
            f.TrackerCaughtUp();
            f.Observer.Tick("periodic");

            Assert.Empty(f.Corrections());
            Assert.Equal(1, f.Runs.Verified);
        }

        [Fact]
        public void Completed_output_is_an_info_event_that_keeps_the_exit_code()
        {
            var f = new Fixture();
            f.Observer.Tick("baseline");
            f.EmitRun(PolicyA, null, 0, "run-a");
            f.Save(Saved(OwnStdout));
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Observer.Tick("registry_change");
            f.Clock.Advance(TimeSpan.FromSeconds(3));
            f.TrackerCaughtUp();
            f.Observer.Tick("periodic");

            var data = Assert.Single(f.Corrections()).Payload!;
            Assert.Equal("repaired", data["outcome"]);
            Assert.Equal("Info", data[SignalPayloadKeys.Severity]);
            Assert.DoesNotContain("previousExitCode", data.Keys);
        }

        [Fact]
        public void A_save_seen_before_its_run_was_emitted_waits_for_the_emit()
        {
            var f = new Fixture();
            f.Observer.Tick("baseline");
            f.Runs.NoteStarted(DeviceUser, PolicyA, f.Clock.UtcNow); // result held for its end block
            f.Save(Saved(OwnStdout));
            f.Observer.Tick("registry_change");
            f.Clock.Advance(TimeSpan.FromSeconds(3));
            f.TrackerCaughtUp();
            f.Observer.Tick("periodic");
            Assert.Equal(0, f.Runs.Verified + f.Runs.Corrected);

            f.Runs.NoteEmitted(Emitted(PolicyA, ForeignStdout, 1, runId: "run-a"), f.Clock.UtcNow);
            f.Clock.Advance(TimeSpan.FromSeconds(60));
            f.Observer.Tick("periodic");
            Assert.Equal("foreign", Assert.Single(f.Corrections()).Payload!["outcome"]);
        }

        [Fact]
        public void A_save_no_run_claims_within_the_expiry_is_dropped()
        {
            var f = new Fixture();
            f.Observer.Tick("baseline");
            f.Save(Saved(OwnStdout));
            f.Observer.Tick("registry_change");
            f.Clock.Advance(ImeRegistryScriptResultObserver.PendingExpiry);
            f.TrackerCaughtUp();
            f.Runs.NoteStarted(DeviceUser, PolicyA, f.Clock.UtcNow);
            f.Observer.Tick("periodic"); // waits for the emit — and expires

            f.Runs.NoteEmitted(Emitted(PolicyA, ForeignStdout, 1, runId: "run-a"), f.Clock.UtcNow);
            f.Clock.Advance(TimeSpan.FromSeconds(60));
            f.TrackerCaughtUp();
            f.Observer.Tick("periodic");
            Assert.Empty(f.Corrections());
        }

        [Fact]
        public void Saves_from_before_the_agent_started_are_the_silent_baseline()
        {
            var f = new Fixture();
            f.EmitRun(PolicyA, ForeignStdout, 1, "run-a");
            f.Save(Saved(OwnStdout));
            f.TrackerCaughtUp();
            f.Observer.Tick("baseline");
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            f.TrackerCaughtUp();
            f.Observer.Tick("periodic");

            Assert.Empty(f.Corrections());
            Assert.Equal(0, f.Runs.Verified + f.Runs.Corrected);
        }

        [Fact]
        public void Corrections_beyond_the_cap_are_counted_but_not_emitted()
        {
            var f = new Fixture();
            f.Observer.Tick("baseline");
            var policies = Enumerable.Range(0, ImeRegistryScriptResultObserver.MaxCorrectionsPerSession + 3).Select(_ => Guid.NewGuid().ToString("D")).ToList();
            foreach (var policy in policies)
            {
                f.EmitRun(policy, ForeignStdout, 1, "run-" + policy);
                f.Save(Saved(OwnStdout, policy: policy));
            }
            f.Observer.Tick("registry_change");
            f.Clock.Advance(TimeSpan.FromSeconds(3));
            f.TrackerCaughtUp();
            f.Observer.Tick("periodic");

            Assert.Equal(ImeRegistryScriptResultObserver.MaxCorrectionsPerSession, f.Corrections().Count);
            Assert.Equal(policies.Count, f.Runs.Corrected);
        }
    }
}
