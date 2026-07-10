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
            // c117946b debrief (2026-05-12) — captures every PromoteActiveInstallsToStuck
            // call so tests can assert the discriminator fired exactly when intended.
            // Session 080edee9 follow-up (2026-05-28) — added `errorCode` slot so tests can
            // verify the HRESULT-based classification (esp_apps_detection_failure vs
            // esp_apps_install_failure vs esp_apps_timeout).
            public List<(string failureType, string message, string errorCode)> PromotionCalls { get; } =
                new List<(string failureType, string message, string errorCode)>();
            public IReadOnlyList<string> PromotionReturnValue { get; set; } = Array.Empty<string>();
            // Session 080edee9 follow-up + Codex review (P2/P3, 2026-05-28) — feeds the
            // lastEspTerminalFailureAccessor in the production wiring. Defaults to null
            // (no ESP failure context observed) so existing tests fall through to the
            // `esp_apps_timeout` classification. Tests for HRESULT-driven classification
            // assign a fully-populated snapshot (errorCode + Apps subcategory); the
            // non-Apps gating test assigns a snapshot whose FailedSubcategory is NOT
            // "Apps".
            public EspTerminalFailureSnapshot? LastEspTerminalFailureOverride { get; set; } = null;

            // Liveness plan PR3 — starved-apps surfaces for the terminal sweep tests.
            public IReadOnlyList<AppPackageState>? StarvedAppsOverride { get; set; } = null;
            public IReadOnlyCollection<string> StarvedAlreadyReportedOverride { get; set; } = Array.Empty<string>();
            public List<string> TerminationActionLog { get; } = new List<string>();
            public TimeSpan SpoolDrainPeriodOverride { get; set; } = TimeSpan.Zero;
            // WG Part-1 straggler-ordering fix — the production wiring passes
            // orchestrator.StopCollectorHosts, whose collectors emit their one-shot
            // stop-time stragglers (network_bandwidth_estimate, …) through Post during
            // OnAfterStop. Tests that care about the ordering set this to a hook that
            // emits a sentinel event; unset leaves the handler with a null action (the
            // legacy no-op).
            public Action? StopPeripheralCollectorsHook;
            // Shutdown-gap closure (2026-05-15) — when set, the constructed handler shares
            // this gate with the cross-path idempotency check. Tests that don't set this
            // get null = legacy always-emit behaviour (preserved).
            public Func<bool>? TryClaimShutdownEventOverride;

            public EnrollmentTerminationHandler Build(AgentConfiguration? config = null) =>
                BuildCore(config, agentVersion: null);

            public EnrollmentTerminationHandler BuildWithVersion(string agentVersion) =>
                BuildCore(config: null, agentVersion: agentVersion);

            private EnrollmentTerminationHandler BuildCore(AgentConfiguration? config, string? agentVersion)
            {
                config ??= BuildConfig();
                return new EnrollmentTerminationHandler(
                    session: new TerminationSessionContext(
                        configuration: config,
                        stateDirectory: StateDir,
                        agentStartTimeUtc: StartUtc,
                        agentVersion: agentVersion,
                        sessionPersistence: SessionPersistence),
                    appTracking: new RigAppTracking(this),
                    drainStatus: new RigDrainStatus(this),
                    shutdownGate: new RigShutdownGate(this),
                    logger: Logger,
                    cleanupServiceFactory: () => CleanupService,
                    uploadDiagnosticsAsync: (succeeded, suffix) =>
                    {
                        DiagnosticsUploads++;
                        LastDiagnosticsSucceededFlag = succeeded;
                        LastDiagnosticsSuffix = suffix;
                        return Task.FromResult(DiagnosticsResult ?? new DiagnosticsUploadResult { BlobName = "blob" });
                    },
                    analyzerManager: null,
                    post: Post,
                    stopPeripheralCollectors: StopPeripheralCollectorsHook,
                    // Zero out the timing ceremony for tests — production paths are covered by
                    // the dedicated V1-parity tests below which opt back in via their own Rig.
                    lateEventGracePeriod: TimeSpan.Zero);
            }

            public void Dispose() => Tmp.Dispose();
        }

        /// <summary>ARCH-F2 — grouped read-model fake over the rig's mutable test state.</summary>
        private sealed class RigAppTracking : IAppTrackingReadModel
        {
            private readonly Rig _rig;
            public RigAppTracking(Rig rig) { _rig = rig; }

            public DecisionState CurrentState => _rig.State;
            public IReadOnlyList<AppPackageState>? PackageStates => _rig.Packages;
            public IReadOnlyDictionary<string, AppInstallTiming>? AppTimings =>
                _rig.AppTimingsOverride ?? new Dictionary<string, AppInstallTiming>();
            public int IgnoredCount => 0;

            public IReadOnlyList<string> PromoteActiveInstallsToStuck(string failureType, string message, string? errorCode)
            {
                _rig.PromotionCalls.Add((failureType, message, errorCode!));
                return _rig.PromotionReturnValue;
            }

            public EspTerminalFailureSnapshot? LastEspTerminalFailure => _rig.LastEspTerminalFailureOverride;

            public IReadOnlyList<AppPackageState>? GetStarvedUserEspApps() => _rig.StarvedAppsOverride;

            // L6 — claim-based dedup: pre-seeded ids model apps the live path already reported.
            private HashSet<string>? _starvedClaims;
            public bool TryClaimStarvedUserEspAppReport(string appId)
            {
                _starvedClaims ??= new HashSet<string>(_rig.StarvedAlreadyReportedOverride, StringComparer.OrdinalIgnoreCase);
                return _starvedClaims.Add(appId);
            }
        }

        /// <summary>
        /// ARCH-F2 — drain fake. The nullable rig accessors map onto the capability flags:
        /// an unset accessor means the surface is unobservable (legacy blind-delay path).
        /// </summary>
        private sealed class RigDrainStatus : IDrainStatus
        {
            private readonly Rig _rig;
            public RigDrainStatus(Rig rig) { _rig = rig; }

            public TimeSpan SpoolDrainPeriod => _rig.SpoolDrainPeriodOverride;
            public bool CanObserveIngress => _rig.IngressPendingSignalCountAccessor != null;
            public long IngressPendingSignalCount => _rig.IngressPendingSignalCountAccessor!();
            public bool CanObserveSpool => _rig.PendingItemCountAccessor != null;
            public int SpoolPendingItemCount => _rig.PendingItemCountAccessor!();
        }

        /// <summary>ARCH-F2 — recording shutdown gate (no real shutdown.exe in tests).</summary>
        private sealed class RigShutdownGate : IShutdownGate
        {
            private readonly Rig _rig;
            public RigShutdownGate(Rig rig) { _rig = rig; }

            public void SignalShutdown()
            {
                Interlocked.Increment(ref _rig.ShutdownSignalled);
                _rig.TerminationActionLog.Add("signalShutdown");
            }

            public void TriggerReboot(int delaySeconds)
            {
                Interlocked.Increment(ref _rig.RebootInvocations);
                _rig.RebootDelaySeconds = delaySeconds;
            }

            public void WriteCleanExitMarker()
            {
                if (_rig.WriteCleanExitMarkerHook != null)
                {
                    _rig.WriteCleanExitMarkerHook();
                    return;
                }
                Interlocked.Increment(ref _rig.CleanExitMarkerWrites);
                _rig.TerminationActionLog.Add("writeCleanExitMarker");
            }

            // Null override = legacy always-emit behaviour (claim always succeeds).
            public bool TryClaimShutdownEvent() => _rig.TryClaimShutdownEventOverride?.Invoke() ?? true;
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
        public void Handle_whiteglove_part1_stops_collectors_before_emitting_part1_complete()
        {
            // WG Part-1 straggler-ordering fix. Peripheral collectors emit their one-shot
            // stop-time stragglers (e.g. the DeliveryOptimizationCollector's
            // network_bandwidth_estimate via OnAfterStop) synchronously when stopped. The
            // handler MUST stop them before emitting whiteglove_part1_complete so those
            // stragglers receive a lower sequence than the marker — otherwise the web split
            // (computeWhiteGloveSplitSequence) mis-files them into the resumed Part-2 block.
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();
            // Mirror production: the stop action emits the straggler through Post, exactly
            // like a real collector's OnAfterStop would.
            rig.StopPeripheralCollectorsHook = () => rig.Post.Emit(new EnrollmentEvent
            {
                SessionId = "S1",
                TenantId = "T1",
                EventType = Constants.EventTypes.NetworkBandwidthEstimate,
                Severity = EventSeverity.Info,
                Source = "DeliveryOptimizationCollector",
                Phase = EnrollmentPhase.Unknown,
                Message = "Estimated internet bandwidth ~200 Mbit/s.",
                ImmediateUpload = true,
            });

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));

            var emitted = rig.EmittedEventTypes.ToList();
            var bandwidthIdx = emitted.IndexOf(Constants.EventTypes.NetworkBandwidthEstimate);
            var part1CompleteIdx = emitted.IndexOf(Constants.EventTypes.WhiteGlovePart1Complete);

            Assert.True(bandwidthIdx >= 0, "collector straggler must have been emitted (stop action fired).");
            Assert.True(part1CompleteIdx >= 0, "whiteglove_part1_complete must have been emitted.");
            Assert.True(bandwidthIdx < part1CompleteIdx,
                $"network_bandwidth_estimate (@{bandwidthIdx}) must precede whiteglove_part1_complete (@{part1CompleteIdx}) so the straggler stays in Part 1.");
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
            // Event-type unification (2026-05-15): the reason tag is now the unified
            // lowercased vocabulary shared with the AgentRuntimeHost gap-paths.
            Assert.Equal("decision_terminal", (string)data["reason"]);
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
            Assert.Equal("decision_terminal", (string)data!["reason"]);
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
        public void Handle_skips_agent_shutting_down_emit_when_gate_already_claimed()
        {
            // Shutdown-gap closure (2026-05-15): when AgentRuntimeHost's gap path (Ctrl+C,
            // ProcessExit, unhandled_exception) emitted first and claimed the cross-path
            // gate, the Terminated-driven handler must NOT emit a second agent_shutting_down
            // event. Idempotency is critical because Ctrl+C followed by a real Terminated
            // transition is a normal sequence in dev/console mode.
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();
            rig.TryClaimShutdownEventOverride = () => false; // slot already claimed by gap path

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            // Cleanup + other lifecycle still runs — only the agent_shutting_down emit is
            // suppressed.
            Assert.Equal(1, rig.CleanupService.Invocations);
            Assert.DoesNotContain(Constants.EventTypes.AgentShuttingDown, rig.EmittedEventTypes);
        }

        [Fact]
        public void Handle_emits_agent_shutting_down_when_gate_grants_slot()
        {
            // Symmetric counterpart — when the gate returns true (handler is first to claim),
            // the emit happens as usual.
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();
            rig.TryClaimShutdownEventOverride = () => true;

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.Contains(Constants.EventTypes.AgentShuttingDown, rig.EmittedEventTypes);
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
            Assert.Equal("max_lifetime", (string)data!["reason"]);
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

        // ----------------------------------------------------------------
        // c117946b debrief (2026-05-12): promote-active-installs-as-stuck
        // ----------------------------------------------------------------

        /// <summary>
        /// Helper — build a Failed-stage state with the full 4-check discriminator armed:
        /// outcome = EnrollmentFailed and lastFailureTrigger = "EspTerminalFailure".
        /// </summary>
        private static DecisionState BuildEspTerminalFailureState() =>
            new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1"))
                .WithStage(SessionStage.Failed)
                .WithOutcome(SessionOutcome.EnrollmentFailed)
                .WithLastFailureTrigger(nameof(DecisionSignalKind.EspTerminalFailure), sourceSignalOrdinal: 42)
                .Build();

        [Fact]
        public void PromoteActiveInstalls_fires_on_full_discriminator_match()
        {
            using var rig = new Rig();
            rig.State = BuildEspTerminalFailureState();
            rig.PromotionReturnValue = new[] { "app-stuck-1" };

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            // No HRESULT observed → fallback to the EspAppsTimeout classification with the
            // canonical "ESP gave up while still installing" message. errorCode is null.
            Assert.Single(rig.PromotionCalls);
            Assert.Equal(AutopilotMonitor.Shared.Constants.AppFailureTypes.EspAppsTimeout, rig.PromotionCalls[0].failureType);
            Assert.Contains("ESP gave up", rig.PromotionCalls[0].message);
            Assert.Null(rig.PromotionCalls[0].errorCode);
        }

        [Fact]
        public void PromoteActiveInstalls_skipped_on_max_lifetime_terminate()
        {
            // MaxLifetimeExceeded → watchdog notbremse, not an ESP-Apps verdict; the
            // agent stops monitoring but the installs may have continued. No promotion.
            using var rig = new Rig();
            rig.State = BuildEspTerminalFailureState(); // even with the fact set …

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.MaxLifetimeExceeded, EnrollmentTerminationOutcome.TimedOut, SessionStage.EspDeviceSetup));

            Assert.Empty(rig.PromotionCalls);
        }

        [Fact]
        public void PromoteActiveInstalls_skipped_on_succeeded_outcome()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1"))
                .WithStage(SessionStage.Completed)
                .WithOutcome(SessionOutcome.EnrollmentComplete)
                .Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.Empty(rig.PromotionCalls);
        }

        [Fact]
        public void PromoteActiveInstalls_skipped_on_aborted_outcome()
        {
            // Admin-kill → state.Outcome = Aborted (not EnrollmentFailed). Says nothing
            // about app status — operator pulled the plug intentionally.
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1"))
                .WithStage(SessionStage.Failed)
                .WithOutcome(SessionOutcome.Aborted)
                .WithLastFailureTrigger(nameof(DecisionSignalKind.SessionAborted), sourceSignalOrdinal: 7)
                .Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.Empty(rig.PromotionCalls);
        }

        // ----------------------------------------------------------------
        // Sessions 6b4993e5 / fc48c71a: esp_apps_failure_correlation
        // ----------------------------------------------------------------

        private static AppPackageState BuildInFlightDeviceApp(string id, string name, AppInstallationState state, long bytesTotal)
        {
            var pkg = new AppPackageState(id, listPos: 0);
            pkg.UpdateName(name);
            pkg.UpdateState(state, newProgressPercent: 0, upgradeOnly: false, bytesDownloaded: 0, bytesTotal: bytesTotal);
            SetTargeted(pkg, AppTargeted.Device);
            return pkg;
        }

        [Fact]
        public void EspAppsFailureCorrelation_names_inflight_device_app_without_mutating_state()
        {
            using var rig = new Rig();
            rig.State = BuildEspTerminalFailureState();
            rig.LastEspTerminalFailureOverride = new EspTerminalFailureSnapshot("0x80070002", "Apps", "DeviceSetup");
            var realmJoin = BuildInFlightDeviceApp("c1dcbb7d-60a3-4703-b5b0-04b3e9037db0", "RealmJoin Agent (Device)", AppInstallationState.Downloading, 2988784);
            rig.Packages.Add(realmJoin);

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.Contains("esp_apps_failure_correlation", rig.EmittedEventTypes);
            var data = rig.DataOf("esp_apps_failure_correlation");
            Assert.NotNull(data);
            Assert.Equal("Apps", data!["failedSubcategory"]);
            Assert.Equal(1, Convert.ToInt32(data["inFlightDeviceAppCount"]));
            var apps = Assert.IsAssignableFrom<IEnumerable<Dictionary<string, object>>>(data["likelyCauseApps"]);
            Assert.Contains(apps, a => (string)a["appName"] == "RealmJoin Agent (Device)" && (string)a["state"] == "Downloading");

            // Correlation must NOT mutate app state — no fabricated app_install_failed.
            Assert.Equal(AppInstallationState.Downloading, realmJoin.InstallationState);
            Assert.False(realmJoin.IsError);
            Assert.DoesNotContain("app_install_failed", rig.EmittedEventTypes);
        }

        [Fact]
        public void EspAppsFailureCorrelation_skipped_when_failure_is_not_apps_subcategory()
        {
            using var rig = new Rig();
            rig.State = BuildEspTerminalFailureState();
            // A non-Apps ESP failure must not blame in-flight apps.
            rig.LastEspTerminalFailureOverride = new EspTerminalFailureSnapshot(null, "SecurityPolicies", "DeviceSetup");
            rig.Packages.Add(BuildInFlightDeviceApp("dev-1", "Some Device App", AppInstallationState.Downloading, 1000));

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.DoesNotContain("esp_apps_failure_correlation", rig.EmittedEventTypes);
        }

        [Fact]
        public void EspAppsFailureCorrelation_skipped_when_no_inflight_device_apps()
        {
            using var rig = new Rig();
            rig.State = BuildEspTerminalFailureState();
            rig.LastEspTerminalFailureOverride = new EspTerminalFailureSnapshot("0x80070002", "Apps", "DeviceSetup");
            // A *user*-targeted downloading app is not a DeviceSetup/Apps culprit; nothing to name.
            var userApp = BuildInFlightDeviceApp("user-1", "User App", AppInstallationState.Downloading, 1000);
            SetTargeted(userApp, AppTargeted.User);
            rig.Packages.Add(userApp);

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.DoesNotContain("esp_apps_failure_correlation", rig.EmittedEventTypes);
        }

        [Fact]
        public void EspAppsFailureCorrelation_skips_queued_device_apps_that_never_started()
        {
            // Codex P2: a device app still in Unknown/NotInstalled (queued, never began
            // downloading/installing) is NOT a likely cause — only IsActive apps are named.
            using var rig = new Rig();
            rig.State = BuildEspTerminalFailureState();
            rig.LastEspTerminalFailureOverride = new EspTerminalFailureSnapshot("0x80070002", "Apps", "DeviceSetup");
            var queued = new AppPackageState("queued-1", listPos: 0); // stays Unknown — IsActive == false
            queued.UpdateName("Queued Device App");
            SetTargeted(queued, AppTargeted.Device);
            rig.Packages.Add(queued);

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.DoesNotContain("esp_apps_failure_correlation", rig.EmittedEventTypes);
        }

        // ----------------------------------------------------------------
        // Liveness plan PR3: app_install_starved terminal sweep
        // ----------------------------------------------------------------

        private static AppPackageState BuildStarvedApp(string id, string name)
        {
            var pkg = new AppPackageState(id, listPos: 0);
            pkg.UpdateIntent(AppIntent.Install);
            pkg.UpdateName(name);
            return pkg;
        }

        [Fact]
        public void StarvedApps_at_termination_emit_one_warning_per_app()
        {
            using var rig = new Rig();
            rig.StarvedAppsOverride = new[] { BuildStarvedApp("app-starving", "Contoso Backgrounds") };

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.MaxLifetimeExceeded, EnrollmentTerminationOutcome.TimedOut, SessionStage.EspAccountSetup));

            Assert.Contains("app_install_starved", rig.EmittedEventTypes);
            var post = rig.Ingress.Posted.Single(p =>
                p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == "app_install_starved");
            Assert.Equal("Warning", post.Payload![SignalPayloadKeys.Severity]);
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(post.TypedPayload);
            Assert.Equal("app-starving", data["appId"]);
            Assert.Equal("termination", data["trigger"]);
            Assert.Equal("TimedOut", data["terminationOutcome"]);
        }

        [Fact]
        public void StarvedApps_on_succeeded_outcome_soften_to_info()
        {
            // Session a4537c36: on a Succeeded termination nothing was starved — the ESP exited
            // normally and pending installs continue via IME. Info + neutral wording, same Data.
            using var rig = new Rig();
            rig.StarvedAppsOverride = new[] { BuildStarvedApp("app-pending", "Contoso Backgrounds") };

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.Contains("app_install_starved", rig.EmittedEventTypes);
            var post = rig.Ingress.Posted.Single(p =>
                p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == "app_install_starved");
            Assert.Equal("Info", post.Payload![SignalPayloadKeys.Severity]);
            Assert.Contains("installs continue via the Intune Management Extension", post.Payload[SignalPayloadKeys.Message]);
            Assert.DoesNotContain("starved", post.Payload[SignalPayloadKeys.Message]);
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(post.TypedPayload);
            Assert.Equal("app-pending", data["appId"]);
            Assert.Equal("termination", data["trigger"]);
            Assert.Equal("Succeeded", data["terminationOutcome"]);
        }

        [Fact]
        public void StarvedApps_already_reported_by_live_path_are_skipped()
        {
            using var rig = new Rig();
            rig.StarvedAppsOverride = new[] { BuildStarvedApp("app-starving", "Contoso Backgrounds") };
            rig.StarvedAlreadyReportedOverride = new[] { "app-starving" };

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.DoesNotContain("app_install_starved", rig.EmittedEventTypes);
        }

        [Fact]
        public void No_starved_apps_emits_nothing()
        {
            using var rig = new Rig();
            rig.StarvedAppsOverride = null; // no IME surface

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.DoesNotContain("app_install_starved", rig.EmittedEventTypes);
        }

        [Fact]
        public void StarvedApps_skipped_on_whiteglove_part1()
        {
            // Part 1 has no user session — user apps haven't run yet; Part 2 is where they land.
            using var rig = new Rig();
            rig.StarvedAppsOverride = new[] { BuildStarvedApp("app-starving", "Contoso Backgrounds") };

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));

            Assert.DoesNotContain("app_install_starved", rig.EmittedEventTypes);
        }

        [Fact]
        public void PromoteActiveInstalls_skipped_on_effect_infrastructure_failure()
        {
            // Monitor-notbremse path: EnrollmentFailed outcome but trigger is not ESP.
            // We have no signal that the apps themselves failed → no promotion.
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1"))
                .WithStage(SessionStage.Failed)
                .WithOutcome(SessionOutcome.EnrollmentFailed)
                .WithLastFailureTrigger(nameof(DecisionSignalKind.EffectInfrastructureFailure), sourceSignalOrdinal: 12)
                .Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.Empty(rig.PromotionCalls);
        }

        [Fact]
        public void PromoteActiveInstalls_skipped_when_LastFailureTrigger_is_null()
        {
            // Defensive guard for snapshots from older agents that don't carry the fact.
            // Without trigger info we can't tell which Failed path fired — abstain.
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1"))
                .WithStage(SessionStage.Failed)
                .WithOutcome(SessionOutcome.EnrollmentFailed)
                .Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.Empty(rig.PromotionCalls);
        }

        [Fact]
        public void PromoteActiveInstalls_message_includes_timeout_when_observation_present()
        {
            using var rig = new Rig();
            var observations = EnrollmentScenarioObservations.Empty
                .WithEspSyncFailureTimeoutMinutes(60, sourceSignalOrdinal: 5);
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1"))
                .WithStage(SessionStage.Failed)
                .WithOutcome(SessionOutcome.EnrollmentFailed)
                .WithLastFailureTrigger(nameof(DecisionSignalKind.EspTerminalFailure), sourceSignalOrdinal: 42)
                .WithScenarioObservations(observations)
                .Build();
            rig.PromotionReturnValue = new[] { "encompass-installer" };

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.Single(rig.PromotionCalls);
            // Session 080edee9 follow-up — message no longer claims a literal timeout
            // (the timeout was the *configured* ceiling, not necessarily the elapsed time).
            Assert.Equal(AutopilotMonitor.Shared.Constants.AppFailureTypes.EspAppsTimeout, rig.PromotionCalls[0].failureType);
            Assert.Contains("ESP gave up", rig.PromotionCalls[0].message);
            Assert.Contains("60 min", rig.PromotionCalls[0].message);
            Assert.Contains("configured", rig.PromotionCalls[0].message);
        }

        [Fact]
        public void PromoteActiveInstalls_message_falls_back_when_timeout_unknown()
        {
            // No EspSyncFailureTimeoutMinutes fact (agent did not observe the FirstSync value)
            // — handler must emit the generic message without an empty parenthesis.
            using var rig = new Rig();
            rig.State = BuildEspTerminalFailureState();
            rig.PromotionReturnValue = new[] { "app-stuck-1" };

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.Single(rig.PromotionCalls);
            Assert.DoesNotContain("min)", rig.PromotionCalls[0].message);
            Assert.DoesNotContain("min;", rig.PromotionCalls[0].message);
            Assert.Contains("ESP gave up while this app was still installing", rig.PromotionCalls[0].message);
        }

        [Fact]
        public void PromoteActiveInstalls_classifies_0x87d1041c_as_detection_failure()
        {
            // Session 080edee9: the agent observed HRESULT 0x87D1041C in the failed
            // DeviceSetup/Apps subcategory ("App not detected after install completed
            // successfully"). The classifier must NOT label this as a timeout — it is a
            // confirmed Intune detection-rule mismatch. The errorCode is propagated to the
            // promotion so the per-app `app_install_failed` event carries DataJson.errorCode
            // and downstream rules (ANALYZE-APP-013) can correlate.
            using var rig = new Rig();
            rig.State = BuildEspTerminalFailureState();
            rig.PromotionReturnValue = new[] { "office-365" };
            rig.LastEspTerminalFailureOverride = new EspTerminalFailureSnapshot(
                errorCode: "0x87d1041c",
                failedSubcategory: "Apps",
                category: "DeviceSetup");

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.Single(rig.PromotionCalls);
            Assert.Equal(
                AutopilotMonitor.Shared.Constants.AppFailureTypes.EspAppsDetectionFailure,
                rig.PromotionCalls[0].failureType);
            Assert.Contains("detection", rig.PromotionCalls[0].message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("0x87d1041c", rig.PromotionCalls[0].message);
            Assert.Equal("0x87d1041c", rig.PromotionCalls[0].errorCode);
        }

        [Fact]
        public void PromoteActiveInstalls_classifies_other_hresult_as_install_failure()
        {
            // Any HRESULT other than 0x87D1041C means the install itself reported an error
            // (vs. a detection-rule mismatch). Must NOT be labelled as a timeout.
            using var rig = new Rig();
            rig.State = BuildEspTerminalFailureState();
            rig.PromotionReturnValue = new[] { "vpn-client" };
            rig.LastEspTerminalFailureOverride = new EspTerminalFailureSnapshot(
                errorCode: "0x80070643",
                failedSubcategory: "Apps",
                category: "DeviceSetup");

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.Single(rig.PromotionCalls);
            Assert.Equal(
                AutopilotMonitor.Shared.Constants.AppFailureTypes.EspAppsInstallFailure,
                rig.PromotionCalls[0].failureType);
            Assert.Contains("0x80070643", rig.PromotionCalls[0].message);
            Assert.Equal("0x80070643", rig.PromotionCalls[0].errorCode);
        }

        [Fact]
        public void PromoteActiveInstalls_falls_back_to_timeout_when_failure_subcategory_is_not_Apps()
        {
            // Codex review (P3): a non-Apps ESP failure with HRESULT (here:
            // DeviceSetup/SecurityPolicies failing with a policy-related code) MUST NOT
            // mis-classify the still-installing apps as `esp_apps_install_failure` —
            // the HRESULT describes the SecurityPolicies subcategory, not the
            // in-flight installs. The classifier must drop the HRESULT and fall back
            // to the generic `esp_apps_timeout` classification.
            using var rig = new Rig();
            rig.State = BuildEspTerminalFailureState();
            rig.PromotionReturnValue = new[] { "in-flight-app" };
            rig.LastEspTerminalFailureOverride = new EspTerminalFailureSnapshot(
                errorCode: "0x8007064a",
                failedSubcategory: "SecurityPolicies",
                category: "DeviceSetup");

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.Single(rig.PromotionCalls);
            Assert.Equal(
                AutopilotMonitor.Shared.Constants.AppFailureTypes.EspAppsTimeout,
                rig.PromotionCalls[0].failureType);
            Assert.DoesNotContain("0x8007064a", rig.PromotionCalls[0].message);
            Assert.Null(rig.PromotionCalls[0].errorCode);
        }

        [Fact]
        public void PromoteActiveInstalls_falls_back_to_timeout_when_failure_snapshot_has_no_subcategory()
        {
            // Defense-in-depth: if the snapshot somehow carries a HRESULT but no
            // failedSubcategory (registry shape regression), we cannot prove the
            // failure came from Apps — must NOT classify as detection / install failure.
            using var rig = new Rig();
            rig.State = BuildEspTerminalFailureState();
            rig.PromotionReturnValue = new[] { "in-flight-app" };
            rig.LastEspTerminalFailureOverride = new EspTerminalFailureSnapshot(
                errorCode: "0x87d1041c",
                failedSubcategory: null,
                category: "DeviceSetup");

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.Single(rig.PromotionCalls);
            Assert.Equal(
                AutopilotMonitor.Shared.Constants.AppFailureTypes.EspAppsTimeout,
                rig.PromotionCalls[0].failureType);
            Assert.Null(rig.PromotionCalls[0].errorCode);
        }
    }
}
