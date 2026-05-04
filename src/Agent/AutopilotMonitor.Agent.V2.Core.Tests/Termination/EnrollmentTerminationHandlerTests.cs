#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Termination;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Termination
{
    // Codex Finding 2 follow-up — the new DrainSpool_* tests use Stopwatch-bound
    // assertions on Task.Delay timings that flake under full-suite parallel load (the
    // Stopwatch sees thread-pool contention as drift beyond the assertion margins).
    // SerialThreading serialises this class against the other timing-sensitive ones so
    // CI runs deterministically. Cost is small — most tests in the class are sub-second
    // and just live alongside the timing-sensitive ones.
    [Collection("SerialThreading")]
    public sealed class EnrollmentTerminationHandlerTests
    {
        private static DateTime StartUtc => new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc);
        private static DateTime EndUtc => StartUtc.AddMinutes(10);

        /// <summary>
        /// CleanupService is virtual for testability. <c>ExecuteSelfDestruct</c> is a best-effort
        /// fire-and-forget in production — in tests we just count invocations.
        /// </summary>
        private sealed class RecordingCleanupService : CleanupService
        {
            public int Invocations;
            public RecordingCleanupService(AgentConfiguration config, AgentLogger logger) : base(config, logger) { }
            public override void ExecuteSelfDestruct() => Interlocked.Increment(ref Invocations);
        }

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public string StateDir { get; }
            public AgentLogger Logger { get; }
            public DecisionState State { get; set; } = DecisionState.CreateInitial("S1", "T1");
            public AppPackageStateList Packages { get; }
            public RecordingCleanupService CleanupService { get; }
            public int DiagnosticsUploads { get; private set; }
            public bool? LastDiagnosticsSucceededFlag { get; private set; }
            public string? LastDiagnosticsSuffix { get; private set; }
            public DiagnosticsUploadResult? DiagnosticsResult { get; set; }
            public int ShutdownSignalled;
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public InformationalEventPost Post { get; }
            public int RebootInvocations;
            public int RebootDelaySeconds;
            public SessionIdPersistence SessionPersistence { get; set; } = default!;

            public Rig()
            {
                StateDir = Path.Combine(Tmp.Path, "State");
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Packages = new AppPackageStateList(Logger);
                CleanupService = new RecordingCleanupService(BuildConfig(), Logger);
                SessionPersistence = new SessionIdPersistence(Tmp.Path);
                Post = new InformationalEventPost(Ingress, new VirtualClock(StartUtc));
            }

            /// <summary>
            /// Returns the event-types emitted through the single-rail InformationalEventPost, in
            /// the order their underlying signals were posted.
            /// </summary>
            public IReadOnlyList<string> EmittedEventTypes =>
                Ingress.Posted
                    .Where(p => p.Kind == DecisionSignalKind.InformationalEvent && p.Payload != null)
                    .Select(p => p.Payload!.TryGetValue(SignalPayloadKeys.EventType, out var v) ? v : string.Empty)
                    .ToList();

            /// <summary>
            /// Returns the string payload dictionary for the first emitted event of the given
            /// type, or null if no such event was emitted. Only the top-level reserved fields
            /// (eventType, source, severity, message, immediateUpload, phase) live here after
            /// the single-rail typed-sidecar refactor.
            /// </summary>
            public IReadOnlyDictionary<string, string>? PayloadOf(string eventType) =>
                Ingress.Posted
                    .Where(p => p.Kind == DecisionSignalKind.InformationalEvent && p.Payload != null)
                    .Where(p => p.Payload!.TryGetValue(SignalPayloadKeys.EventType, out var v) &&
                                string.Equals(v, eventType, StringComparison.Ordinal))
                    .Select(p => p.Payload)
                    .FirstOrDefault();

            /// <summary>
            /// Returns the structured <see cref="EnrollmentEvent.Data"/> dictionary for the first
            /// emitted event of the given type. After the single-rail typed-sidecar refactor
            /// (plan §1.3), Data fields flow through <see cref="DecisionSignal.TypedPayload"/>
            /// untouched — tests that previously read Data keys from the string payload must
            /// read them from here instead.
            /// </summary>
            public IReadOnlyDictionary<string, object>? DataOf(string eventType) =>
                Ingress.Posted
                    .Where(p => p.Kind == DecisionSignalKind.InformationalEvent && p.Payload != null)
                    .Where(p => p.Payload!.TryGetValue(SignalPayloadKeys.EventType, out var v) &&
                                string.Equals(v, eventType, StringComparison.Ordinal))
                    .Select(p => p.TypedPayload as IReadOnlyDictionary<string, object>)
                    .FirstOrDefault();

            public AgentConfiguration BuildConfig(
                bool selfDestruct = true,
                bool showDialog = false,
                bool diagEnabled = false,
                string diagMode = "Off")
            {
                return new AgentConfiguration
                {
                    SessionId = "S1",
                    TenantId = "T1",
                    ApiBaseUrl = "http://localhost",
                    SelfDestructOnComplete = selfDestruct,
                    ShowEnrollmentSummary = showDialog,
                    DiagnosticsUploadEnabled = diagEnabled,
                    DiagnosticsUploadMode = diagMode,
                    EnrollmentSummaryTimeoutSeconds = 60,
                };
            }

            public IReadOnlyDictionary<string, AppInstallTiming>? AppTimingsOverride { get; set; }

            // Option 1+2 (WG Part 1 graceful-exit hardening, 2026-04-30) test hooks.
            // Tests that exercise the new behaviour set these directly; tests that don't
            // care leave them null (handler falls back to the legacy blind-delay drain
            // and skips the early marker write).
            public Func<int>? PendingItemCountAccessor;
            public Func<long>? IngressPendingSignalCountAccessor;
            public Action? WriteCleanExitMarkerHook;
            public int CleanExitMarkerWrites;
            public List<string> TerminationActionLog { get; } = new List<string>();
            public TimeSpan SpoolDrainPeriodOverride { get; set; } = TimeSpan.Zero;

            public EnrollmentTerminationHandler Build(AgentConfiguration? config = null) =>
                BuildCore(config, agentVersion: null);

            public EnrollmentTerminationHandler BuildWithVersion(string agentVersion) =>
                BuildCore(config: null, agentVersion: agentVersion);

            private EnrollmentTerminationHandler BuildCore(AgentConfiguration? config, string? agentVersion)
            {
                config ??= BuildConfig();
                return new EnrollmentTerminationHandler(
                    configuration: config,
                    logger: Logger,
                    stateDirectory: StateDir,
                    agentStartTimeUtc: StartUtc,
                    currentStateAccessor: () => State,
                    packageStatesAccessor: () => Packages,
                    cleanupServiceFactory: () => CleanupService,
                    uploadDiagnosticsAsync: (succeeded, suffix) =>
                    {
                        DiagnosticsUploads++;
                        LastDiagnosticsSucceededFlag = succeeded;
                        LastDiagnosticsSuffix = suffix;
                        return Task.FromResult(DiagnosticsResult ?? new DiagnosticsUploadResult { BlobName = "blob" });
                    },
                    signalShutdown: () =>
                    {
                        Interlocked.Increment(ref ShutdownSignalled);
                        TerminationActionLog.Add("signalShutdown");
                    },
                    analyzerManager: null,
                    post: Post,
                    sessionPersistence: SessionPersistence,
                    triggerReboot: delay =>
                    {
                        Interlocked.Increment(ref RebootInvocations);
                        RebootDelaySeconds = delay;
                    },
                    // Zero out the timing ceremony for tests — production paths are covered by
                    // the dedicated V1-parity tests below which opt back in via their own Rig.
                    lateEventGracePeriod: TimeSpan.Zero,
                    spoolDrainPeriod: SpoolDrainPeriodOverride,
                    appTimingsAccessor: () => AppTimingsOverride ?? new Dictionary<string, AppInstallTiming>(),
                    agentVersion: agentVersion,
                    pendingItemCountAccessor: PendingItemCountAccessor,
                    writeCleanExitMarker: WriteCleanExitMarkerHook ?? new Action(() =>
                    {
                        Interlocked.Increment(ref CleanExitMarkerWrites);
                        TerminationActionLog.Add("writeCleanExitMarker");
                    }),
                    ingressPendingSignalCountAccessor: IngressPendingSignalCountAccessor);
            }

            public void Dispose() => Tmp.Dispose();
        }

        private static EnrollmentTerminatedEventArgs Args(
            EnrollmentTerminationReason reason,
            EnrollmentTerminationOutcome outcome,
            SessionStage stage,
            DateTime? at = null) =>
            new EnrollmentTerminatedEventArgs(
                reason, outcome, stage.ToString(), at ?? EndUtc);

        [Fact]
        public void Handle_writes_final_status_json_in_state_directory()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            var statusPath = Path.Combine(rig.StateDir, SummaryDialogLauncher.FinalStatusFileName);
            Assert.True(File.Exists(statusPath), "final-status.json should be written even when ShowEnrollmentSummary=false");
        }

        [Fact]
        public void Handle_writes_enrollment_complete_marker_on_non_whiteglove_terminal()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.True(File.Exists(Path.Combine(rig.StateDir, "enrollment-complete.marker")));
        }

        [Fact]
        public void Handle_skips_marker_and_cleanup_on_whiteglove_part1_exit()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));

            Assert.False(File.Exists(Path.Combine(rig.StateDir, "enrollment-complete.marker")));
            Assert.Equal(0, rig.CleanupService.Invocations);
        }

        [Fact]
        public void Handle_runs_cleanup_on_success_when_self_destruct_enabled()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.Equal(1, rig.CleanupService.Invocations);
        }

        [Fact]
        public void Handle_skips_cleanup_when_self_destruct_disabled()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();
            var cfg = rig.BuildConfig(selfDestruct: false);

            rig.Build(cfg).Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.Equal(0, rig.CleanupService.Invocations);
        }

        [Fact]
        public void Handle_signals_shutdown_exactly_once_even_on_error()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Failed }.Build();

            var sut = rig.Build();
            sut.Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            // Idempotent — second Handle is a no-op.
            sut.Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.Equal(1, rig.ShutdownSignalled);
        }

        [Theory]
        [InlineData("Off", EnrollmentTerminationOutcome.Succeeded, false, 0)]
        [InlineData("Always", EnrollmentTerminationOutcome.Succeeded, true, 1)]
        [InlineData("Always", EnrollmentTerminationOutcome.Failed, true, 1)]
        [InlineData("OnFailure", EnrollmentTerminationOutcome.Succeeded, true, 0)]
        [InlineData("OnFailure", EnrollmentTerminationOutcome.Failed, true, 1)]
        public void Handle_diagnostics_upload_respects_mode_and_outcome(
            string mode, EnrollmentTerminationOutcome outcome, bool enabled, int expected)
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();
            var cfg = rig.BuildConfig(diagEnabled: enabled, diagMode: mode, selfDestruct: false);

            rig.Build(cfg).Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, outcome,
                    outcome == EnrollmentTerminationOutcome.Succeeded ? SessionStage.Completed : SessionStage.Failed));

            Assert.Equal(expected, rig.DiagnosticsUploads);
        }

        [Fact]
        public void Handle_diagnostics_upload_suffix_reflects_success_vs_failure()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Failed }.Build();
            var cfg = rig.BuildConfig(diagEnabled: true, diagMode: "Always", selfDestruct: false);

            rig.Build(cfg).Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.Equal("failure", rig.LastDiagnosticsSuffix);
            Assert.Equal(false, rig.LastDiagnosticsSucceededFlag);
        }

        // ============================================================= PR #50 lifecycle events

        [Fact]
        public void Handle_emits_diagnostics_collecting_and_uploaded_events_on_success()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();
            rig.DiagnosticsResult = new DiagnosticsUploadResult { BlobName = "diag-blob.zip", SasUrlPrefix = "https://example.test" };
            var cfg = rig.BuildConfig(diagEnabled: true, diagMode: "Always", selfDestruct: false);

            rig.Build(cfg).Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.Contains("diagnostics_collecting", rig.EmittedEventTypes);
            var uploaded = rig.DataOf("diagnostics_uploaded");
            Assert.NotNull(uploaded);
            Assert.Equal("diag-blob.zip", (string)uploaded!["blobName"]);
            Assert.DoesNotContain("diagnostics_upload_failed", rig.EmittedEventTypes);
        }

        [Fact]
        public void Handle_emits_diagnostics_upload_failed_when_upload_returns_null()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Failed }.Build();
            rig.DiagnosticsResult = new DiagnosticsUploadResult { ErrorCode = "upload_5xx" };
            var cfg = rig.BuildConfig(diagEnabled: true, diagMode: "Always", selfDestruct: false);

            rig.Build(cfg).Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.Contains("diagnostics_collecting", rig.EmittedEventTypes);
            var failed = rig.DataOf("diagnostics_upload_failed");
            Assert.NotNull(failed);
            Assert.Equal("upload_5xx", (string)failed!["errorCode"]);
        }

        [Fact]
        public void Handle_whiteglove_part1_writes_marker_and_emits_whiteglove_part1_complete()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));

            // The whiteglove.complete marker must be present so the next boot classifies as Part 2 resume.
            Assert.True(rig.SessionPersistence.IsWhiteGloveResume());
            Assert.Contains("whiteglove_part1_complete", rig.EmittedEventTypes);
            // And — critically — self-destruct MUST have been skipped and the enrollment-complete
            // marker MUST NOT be written, or the next Part-2 boot would ghost-detect itself.
            Assert.Equal(0, rig.CleanupService.Invocations);
            Assert.False(File.Exists(Path.Combine(rig.StateDir, "enrollment-complete.marker")));
        }

        [Fact]
        public void Handle_standalone_reboot_fires_when_reboot_enabled_and_no_self_destruct()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();
            var cfg = rig.BuildConfig(selfDestruct: false);
            cfg.RebootOnComplete = true;
            cfg.RebootDelaySeconds = 15;

            rig.Build(cfg).Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.Equal(1, rig.RebootInvocations);
            Assert.Equal(15, rig.RebootDelaySeconds);
            Assert.Contains("reboot_triggered", rig.EmittedEventTypes);
        }

        [Fact]
        public void Handle_does_not_reboot_when_self_destruct_is_enabled()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();
            var cfg = rig.BuildConfig(selfDestruct: true);
            cfg.RebootOnComplete = true;

            rig.Build(cfg).Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            // Self-destruct owns the terminal transition; reboot is the no-self-destruct path only.
            Assert.Equal(0, rig.RebootInvocations);
            Assert.DoesNotContain("reboot_triggered", rig.EmittedEventTypes);
        }

        [Fact]
        public void Handle_emits_enrollment_summary_shown_when_dialog_enabled()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();
            var cfg = rig.BuildConfig(showDialog: true, selfDestruct: false);

            rig.Build(cfg).Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.Contains("enrollment_summary_shown", rig.EmittedEventTypes);
        }

        // ============================================================= PR-X1 (bdb3cf9d, 2026-05-04)
        // WhiteGlove Part 1 must NOT launch the summary dialog or emit
        // enrollment_summary_shown — the device is about to be reseal-rebooted, the user
        // session does not exist yet, and apps have not installed. The other terminal
        // stages (Completed / Failed) still launch the dialog. Part 2 reaches the same
        // Completed stage as a fresh Classic enrollment after Archive-and-Reset (PR-A).

        [Fact]
        public void Handle_skips_summary_dialog_on_whiteglove_part1_even_with_dialog_enabled()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();
            // Self-destruct stays off so we don't tear down between assertions, dialog is
            // explicitly enabled to prove the Part-1 stage gate beats the config flag.
            var cfg = rig.BuildConfig(showDialog: true, selfDestruct: false);

            rig.Build(cfg).Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));

            Assert.DoesNotContain("enrollment_summary_shown", rig.EmittedEventTypes);
            Assert.False(File.Exists(Path.Combine(rig.StateDir, SummaryDialogLauncher.FinalStatusFileName)),
                "final-status.json must not be written on WG Part 1 — the dialog launch is skipped.");
        }

        [Fact]
        public void Handle_emits_enrollment_summary_shown_on_completed_termination_with_part2_hint()
        {
            // PR-B: post WG-Part-2 cleanup, a Part-2 resume terminates with Stage=Completed
            // (Classic flow). The orchestrator's IsWhiteGlovePart2 hint distinguishes a
            // Part-2 run from a fresh first-boot Classic enrollment for the analyzer pipeline.
            // From the dialog's perspective both Part-2 and a regular Completed look identical
            // — final-status.json is written and enrollment_summary_shown is emitted.
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();
            var cfg = rig.BuildConfig(showDialog: true, selfDestruct: false);

            rig.Build(cfg).Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.Contains("enrollment_summary_shown", rig.EmittedEventTypes);
            Assert.True(File.Exists(Path.Combine(rig.StateDir, SummaryDialogLauncher.FinalStatusFileName)));
        }

        [Fact]
        public void Handle_emits_enrollment_summary_shown_on_failed_termination()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Failed }.Build();
            var cfg = rig.BuildConfig(showDialog: true, selfDestruct: false);

            rig.Build(cfg).Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.Contains("enrollment_summary_shown", rig.EmittedEventTypes);
            Assert.True(File.Exists(Path.Combine(rig.StateDir, SummaryDialogLauncher.FinalStatusFileName)));
        }

        // ============================================================= Plan §6.2 agent_shutting_down

        [Fact]
        public void Handle_emits_agent_shutting_down_before_cleanup_self_destruct()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.Equal(1, rig.CleanupService.Invocations);
            var shuttingDownIdx = rig.EmittedEventTypes.ToList().IndexOf(Constants.EventTypes.AgentShuttingDown);
            Assert.True(shuttingDownIdx >= 0, "agent_shutting_down event must be emitted when self-destruct runs.");

            var data = rig.DataOf(Constants.EventTypes.AgentShuttingDown);
            Assert.NotNull(data);
            Assert.Equal(EnrollmentTerminationOutcome.Succeeded.ToString(), (string)data!["outcome"]);
            Assert.Equal(EnrollmentTerminationReason.DecisionTerminalStage.ToString(), (string)data["reason"]);
        }

        [Fact]
        public void Handle_emits_agent_shutting_down_when_self_destruct_disabled()
        {
            // PR1-C / V1 parity: dev/test VMs run with SelfDestruct=false but the agent still
            // ends after termination — the timeline must show that.
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();
            var cfg = rig.BuildConfig(selfDestruct: false);

            rig.Build(cfg).Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.Equal(0, rig.CleanupService.Invocations);
            Assert.Contains(Constants.EventTypes.AgentShuttingDown, rig.EmittedEventTypes);

            var data = rig.DataOf(Constants.EventTypes.AgentShuttingDown);
            Assert.NotNull(data);
            Assert.Equal(EnrollmentTerminationReason.DecisionTerminalStage.ToString(), (string)data!["reason"]);
        }

        [Fact]
        public void Handle_emits_agent_shutting_down_on_whiteglove_part1_exit()
        {
            // PR1-C: the running agent process ends on Part 1 just like on a normal terminal —
            // emit the lifecycle marker so the timeline reflects the handoff. Stage payload
            // tells the backend this was the WG sealing branch.
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));

            // Cleanup still must NOT run on WG Part 1 (the session resumes on next boot).
            Assert.Equal(0, rig.CleanupService.Invocations);
            Assert.Contains(Constants.EventTypes.AgentShuttingDown, rig.EmittedEventTypes);

            var data = rig.DataOf(Constants.EventTypes.AgentShuttingDown);
            Assert.NotNull(data);
            Assert.Equal(SessionStage.WhiteGloveSealed.ToString(), (string)data!["stage"]);
        }

        [Fact]
        public void Handle_propagates_max_lifetime_reason_in_agent_shutting_down_data()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.AwaitingHello }.Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.MaxLifetimeExceeded, EnrollmentTerminationOutcome.Failed, SessionStage.AwaitingHello));

            var data = rig.DataOf(Constants.EventTypes.AgentShuttingDown);
            Assert.NotNull(data);
            Assert.Equal(EnrollmentTerminationReason.MaxLifetimeExceeded.ToString(), (string)data!["reason"]);
            Assert.Equal(EnrollmentTerminationOutcome.Failed.ToString(), (string)data["outcome"]);
        }

        [Fact]
        public void Handle_includes_uptime_and_agent_version_in_agent_shutting_down()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();

            rig.BuildWithVersion("9.9.9").Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            var data = rig.DataOf(Constants.EventTypes.AgentShuttingDown);
            Assert.NotNull(data);
            Assert.Equal("9.9.9", (string)data!["agentVersion"]);
            Assert.True(data.ContainsKey("uptimeMinutes"));
            Assert.IsType<double>(data["uptimeMinutes"]);
        }

        // ============================================================================
        // Plan §5 Fix 4b — app_tracking_summary event
        // ============================================================================

        /// <summary>Reflection helper: AppPackageState.Targeted has a private setter.</summary>
        private static void SetTargeted(AppPackageState pkg, AppTargeted t) =>
            typeof(AppPackageState).GetProperty(nameof(AppPackageState.Targeted))!.SetValue(pkg, t);

        [Fact]
        public void Handle_emits_app_tracking_summary_with_v1_schema_counts_and_split_errors()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();

            // AppPackageState.UpdateState has an inverse-detection guard: Installed without a
            // prior DownloadingOrInstallingSeen=true flip is rewritten to Skipped. Seed the
            // lifecycle via Installing first so the terminal state sticks.
            var installed = new AppPackageState("app-installed", 0);
            installed.UpdateState(AppInstallationState.Installing);
            installed.UpdateState(AppInstallationState.Installed);
            SetTargeted(installed, AppTargeted.Device);
            rig.Packages.Add(installed);

            var failed = new AppPackageState("app-failed", 1);
            failed.UpdateState(AppInstallationState.Error);
            SetTargeted(failed, AppTargeted.User);
            rig.Packages.Add(failed);

            var skipped = new AppPackageState("app-skipped", 2);
            skipped.UpdateState(AppInstallationState.Skipped);
            SetTargeted(skipped, AppTargeted.User);
            rig.Packages.Add(skipped);

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.Contains(Constants.EventTypes.AppTrackingSummary, rig.EmittedEventTypes);

            var data = rig.DataOf(Constants.EventTypes.AppTrackingSummary);
            Assert.NotNull(data);
            // Flat V1 schema — counts + split error attribution + bool helpers.
            Assert.Equal(3, Convert.ToInt32(data!["totalApps"]));
            Assert.Equal(3, Convert.ToInt32(data["completedApps"]));
            Assert.Equal(1, Convert.ToInt32(data["installed"]));
            Assert.Equal(1, Convert.ToInt32(data["skipped"]));
            Assert.Equal(1, Convert.ToInt32(data["failed"]));
            Assert.Equal(1, Convert.ToInt32(data["errorCount"]));
            Assert.Equal(0, Convert.ToInt32(data["deviceErrors"]));
            Assert.Equal(1, Convert.ToInt32(data["userErrors"]));
            Assert.True(Convert.ToBoolean(data["hasErrors"]));
            Assert.True(Convert.ToBoolean(data["isAllCompleted"]));
        }

        [Fact]
        public void Handle_does_not_emit_app_tracking_summary_on_whiteglove_part1_exit()
        {
            // Part 1 runs pre-provisioning — no apps have installed yet; the summary event
            // would be vacuous and misleading.
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));

            Assert.DoesNotContain(Constants.EventTypes.AppTrackingSummary, rig.EmittedEventTypes);
        }

        [Fact]
        public void Handle_app_tracking_summary_marked_immediate_upload()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            var payload = rig.PayloadOf(Constants.EventTypes.AppTrackingSummary);
            Assert.NotNull(payload);
            Assert.Equal("true", payload![SignalPayloadKeys.ImmediateUpload]);
        }

        // ============================================================= Option 1+2 — WG Part 1
        // graceful-exit hardening (2026-04-30). Active spool-empty drain + early clean-exit
        // marker write before _signalShutdown returns control to the main thread.

        // DrainSpool is invoked on the WG Part 1 + standalone-reboot paths. We exercise it
        // through the WG Part 1 path here (Stage=WhiteGloveSealed → DrainSpool runs in the
        // graceful-exit branch).

        [Fact]
        public void DrainSpool_returns_immediately_when_pending_count_is_zero()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();
            // Long bounded timeout — if the active drain is broken the test would hit this.
            rig.SpoolDrainPeriodOverride = TimeSpan.FromSeconds(5);
            rig.PendingItemCountAccessor = () => 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
                $"DrainSpool should exit immediately on pending=0 but took {sw.ElapsedMilliseconds}ms.");
        }

        [Fact]
        public void DrainSpool_polls_until_pending_drops_to_zero()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();
            rig.SpoolDrainPeriodOverride = TimeSpan.FromSeconds(5);

            // Simulate the spool draining ~250ms after the accessor is first observed.
            var startedAt = DateTime.UtcNow;
            rig.PendingItemCountAccessor = () =>
                (DateTime.UtcNow - startedAt) > TimeSpan.FromMilliseconds(250) ? 0 : 3;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));
            sw.Stop();

            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(200),
                $"Drain returned too early (elapsed={sw.ElapsedMilliseconds}ms).");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
                $"Drain did not exit early after spool drained (elapsed={sw.ElapsedMilliseconds}ms).");
        }

        [Fact]
        public void DrainSpool_falls_back_to_timeout_when_pending_never_drains()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();
            // Tight timeout to keep the test fast.
            rig.SpoolDrainPeriodOverride = TimeSpan.FromMilliseconds(400);
            rig.PendingItemCountAccessor = () => 5;  // Always pending — drain never satisfies.

            var sw = System.Diagnostics.Stopwatch.StartNew();
            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));
            sw.Stop();

            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(350),
                $"Drain returned before bounded timeout (elapsed={sw.ElapsedMilliseconds}ms).");
        }

        [Fact]
        public void DrainSpool_falls_back_to_blind_delay_when_no_accessor_is_wired()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();
            rig.SpoolDrainPeriodOverride = TimeSpan.FromMilliseconds(300);
            rig.PendingItemCountAccessor = null;  // Legacy / null path.

            var sw = System.Diagnostics.Stopwatch.StartNew();
            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));
            sw.Stop();

            // Without an accessor the drain just sleeps the full bounded period — verifies
            // the V1-parity fallback path stays intact.
            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(280),
                $"Blind delay returned too early (elapsed={sw.ElapsedMilliseconds}ms).");
        }

        [Fact]
        public void WhiteGlovePart1_writes_clean_exit_marker_before_signaling_shutdown()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));

            Assert.Equal(1, rig.CleanExitMarkerWrites);
            // Order matters — marker must be on disk BEFORE the main thread is released to
            // exit. Otherwise an admin reseal-reboot can race the AppDomain.ProcessExit
            // fallback handler and we'd be classified as reboot_kill on the next boot.
            var markerIdx = rig.TerminationActionLog.IndexOf("writeCleanExitMarker");
            var shutdownIdx = rig.TerminationActionLog.IndexOf("signalShutdown");
            Assert.True(markerIdx >= 0, "writeCleanExitMarker hook must be invoked on WG Part 1 exit.");
            Assert.True(shutdownIdx > markerIdx,
                $"writeCleanExitMarker must precede signalShutdown (got marker@{markerIdx}, shutdown@{shutdownIdx}).");
        }

        [Fact]
        public void Standard_terminal_path_writes_clean_exit_marker_too()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.Equal(1, rig.CleanExitMarkerWrites);
            var markerIdx = rig.TerminationActionLog.IndexOf("writeCleanExitMarker");
            var shutdownIdx = rig.TerminationActionLog.IndexOf("signalShutdown");
            Assert.True(markerIdx >= 0 && shutdownIdx > markerIdx);
        }

        [Fact]
        public void CleanExitMarker_writer_failure_does_not_break_termination()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();
            // Replace the default with a throwing hook to verify the handler swallows + logs
            // and still reaches signalShutdown.
            rig.WriteCleanExitMarkerHook = () => throw new System.IO.IOException("disk full");

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));

            Assert.Equal(1, rig.ShutdownSignalled);
        }

        // ============================================================== Codex Finding 2 —
        // off-worker dispatch + two-phase drain (ingress queue → spool). The drain MUST
        // wait for the ingress to settle BEFORE polling spool-empty, otherwise events the
        // handler itself just posted are still in the channel when DrainSpool returns.

        [Fact]
        public void DrainSpool_waits_for_ingress_to_drain_before_polling_spool()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();
            rig.SpoolDrainPeriodOverride = TimeSpan.FromSeconds(5);

            // Simulate the ingress holding the lifecycle events for ~250ms before the
            // worker processes them; spool stays empty (events haven't been spooled yet).
            // Once ingress drains, spool is also empty (no late uploads to wait for).
            int spoolPollsObserved = 0;
            var ingressIdleAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
            rig.IngressPendingSignalCountAccessor = () =>
                DateTime.UtcNow >= ingressIdleAt ? 0L : 4L;
            rig.PendingItemCountAccessor = () => { spoolPollsObserved++; return 0; };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));
            sw.Stop();

            // The drain MUST have waited until the ingress drained (>=200ms) — anything
            // shorter means we polled spool-empty before the handler-posted events hit it.
            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(200),
                $"Drain returned before ingress drained (elapsed={sw.ElapsedMilliseconds}ms).");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
                $"Drain did not exit early after ingress + spool both went idle (elapsed={sw.ElapsedMilliseconds}ms).");
            // Spool must have been polled at least once after ingress idle — proves Phase B ran.
            Assert.True(spoolPollsObserved >= 1, "Spool poll did not run after ingress drain.");
        }

        [Fact]
        public void DrainSpool_two_phase_budget_is_shared_across_ingress_and_spool()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();
            // Tight shared budget — the ingress phase eats most of it, the spool phase
            // gets whatever's left and times out cleanly.
            rig.SpoolDrainPeriodOverride = TimeSpan.FromMilliseconds(400);

            rig.IngressPendingSignalCountAccessor = () => 7L;  // never drains
            rig.PendingItemCountAccessor = () => 3;             // never drains either

            var sw = System.Diagnostics.Stopwatch.StartNew();
            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));
            sw.Stop();

            // Total drain MUST be bounded by the shared budget — the spool phase doesn't
            // get its OWN 400ms on top of the ingress phase.
            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(350),
                $"Drain returned too early (elapsed={sw.ElapsedMilliseconds}ms).");
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(900),
                $"Drain exceeded shared budget (elapsed={sw.ElapsedMilliseconds}ms) — phases must share, not stack.");
        }

        [Fact]
        public void DrainSpool_skips_ingress_phase_when_only_spool_accessor_wired()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();
            rig.SpoolDrainPeriodOverride = TimeSpan.FromSeconds(5);

            // No ingress accessor — handler skips Phase A and just polls spool.
            rig.IngressPendingSignalCountAccessor = null;
            rig.PendingItemCountAccessor = () => 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1),
                $"Spool-only drain should return promptly (elapsed={sw.ElapsedMilliseconds}ms).");
        }

        [Fact]
        public void DrainSpool_ingress_accessor_failure_does_not_break_termination()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();
            rig.SpoolDrainPeriodOverride = TimeSpan.FromMilliseconds(200);
            rig.IngressPendingSignalCountAccessor = () => throw new InvalidOperationException("orchestrator stopped");
            rig.PendingItemCountAccessor = () => 0;

            // Must not throw out of Handle, must still call signalShutdown.
            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));

            Assert.Equal(1, rig.ShutdownSignalled);
        }
    }
}
