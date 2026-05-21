#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    public sealed class RealmJoinDetectedEventArgs : EventArgs
    {
        public int DeploymentPhase { get; }
        public RealmJoinDetectedEventArgs(int deploymentPhase) { DeploymentPhase = deploymentPhase; }
    }

    public sealed class RealmJoinResolvedEventArgs : EventArgs
    {
        public int DeploymentPhase { get; }
        public RealmJoinResolvedEventArgs(int deploymentPhase) { DeploymentPhase = deploymentPhase; }
    }

    public sealed class RealmJoinPhaseChangedEventArgs : EventArgs
    {
        public int PreviousPhase { get; }
        public int CurrentPhase { get; }
        public RealmJoinPhaseChangedEventArgs(int previousPhase, int currentPhase)
        {
            PreviousPhase = previousPhase;
            CurrentPhase = currentPhase;
        }
    }

    public sealed class RealmJoinPackageEventArgs : EventArgs
    {
        public string Scope { get; }
        public string PackageId { get; }
        public string? DisplayName { get; }
        public string? Version { get; }
        public bool? Success { get; }
        public int? LastExitCode { get; }

        public RealmJoinPackageEventArgs(string scope, RealmJoinPackageSnapshot snapshot)
        {
            Scope = scope;
            PackageId = snapshot.PackageId;
            DisplayName = snapshot.DisplayName;
            Version = snapshot.Version;
            Success = snapshot.Success;
            LastExitCode = snapshot.LastExitCode;
        }
    }

    /// <summary>
    /// Owns the registry watchers that observe RealmJoin (RJ) deployment state. Uses the
    /// canonical Win32 <c>RegNotifyChangeKeyValue</c> pattern end-to-end: when the target keys
    /// do not exist yet (typical on 80%+ of devices where RJ is never installed) the watcher
    /// armed a Name-filter notification on the always-existing parent (<c>Services</c>,
    /// <c>HKLM\SOFTWARE</c>, <c>HKU\&lt;sid&gt;\SOFTWARE</c>) and stays idle until that fires.
    /// Once the target sub-key appears the parent-watch is disposed and the real subtree
    /// watcher attaches. No background polling.
    /// </summary>
    /// <remarks>
    /// <para>Three stages per scope (machine + user):</para>
    /// <list type="number">
    ///   <item>Boot sync probe: if the target key already exists, jump straight to step 3.</item>
    ///   <item>Parent-watch armed (Name filter). Each wake-up re-probes the target.</item>
    ///   <item>Subtree watcher attached on the appeared key; emits packages/phase events.</item>
    /// </list>
    /// <para>
    /// Dedup is strict per stage: <see cref="RealmJoinDetected"/> / <see cref="RealmJoinResolved"/>
    /// fire once; <see cref="RealmJoinPackageStarted"/> / <see cref="RealmJoinPackageCompleted"/>
    /// fire once per <c>(scope, packageId)</c>.
    /// </para>
    /// </remarks>
    internal sealed class RealmJoinWatcher : IDisposable
    {
        internal const int DebounceMilliseconds = 500;

        private readonly AgentLogger _logger;
        private readonly object _stateLock = new object();

        // Stage-1 parent-watchers (disposed on appearance).
        private RegistryWatcher? _servicesAppearanceWatcher;
        private RegistryWatcher? _machineSoftwareAppearanceWatcher;
        private RegistryWatcher? _userSoftwareAppearanceWatcher;

        // Stage-2 active subtree watchers (attached after appearance).
        private RegistryWatcher? _serviceRealmjoinWatcher;
        private RegistryWatcher? _machineRealmJoinWatcher;
        private RegistryWatcher? _userRealmJoinWatcher;

        // Debounce timers for each active watcher.
        private Timer? _serviceRealmjoinDebounce;
        private Timer? _machineRealmJoinDebounce;
        private Timer? _userRealmJoinDebounce;

        // Dedup state.
        private bool _detectedFired;
        private bool _resolvedFired;
        private int? _lastPhase;

        private readonly HashSet<string> _hklmPackagesStarted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _hklmPackagesCompleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _hkuPackagesStarted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _hkuPackagesCompleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private string? _hkuSid;
        private bool _disposed;

        public event EventHandler<RealmJoinDetectedEventArgs>? RealmJoinDetected;
        public event EventHandler<RealmJoinResolvedEventArgs>? RealmJoinResolved;
        public event EventHandler<RealmJoinPhaseChangedEventArgs>? RealmJoinPhaseChanged;
        public event EventHandler<RealmJoinPackageEventArgs>? RealmJoinPackageStarted;
        public event EventHandler<RealmJoinPackageEventArgs>? RealmJoinPackageCompleted;

        public RealmJoinWatcher(AgentLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RealmJoinWatcher));

            // Initial sync reads (cheap; both bail out if the target keys don't exist).
            CheckParameters();
            CheckMachinePackages();

            // Stage A: ensure the machine RJ service key.
            EnsureRealmJoinServiceStage();

            // Stage B: ensure HKLM\SOFTWARE\RealmJoin (independent of A — either can appear first).
            EnsureMachineRealmJoinStage();
        }

        /// <summary>
        /// Attach the user-scope HKU watcher. Called by the host once
        /// <see cref="DesktopArrivalDetector"/> resolves a real user owner and
        /// <see cref="UserSidResolver"/> produces the SID. Idempotent.
        /// </summary>
        public void ArmHku(string sid)
        {
            if (_disposed) return;
            if (string.IsNullOrEmpty(sid)) return;

            lock (_stateLock)
            {
                if (_hkuSid != null) return;
                _hkuSid = sid;
            }
            _logger.Info($"RealmJoinWatcher: arming HKU watcher for SID {sid}");

            CheckUserPackages();
            EnsureUserRealmJoinStage();
        }

        public void Stop(string reason = "watcher_stopped")
        {
            try
            {
                lock (_stateLock)
                {
                    DisposeAndNull(ref _servicesAppearanceWatcher);
                    DisposeAndNull(ref _machineSoftwareAppearanceWatcher);
                    DisposeAndNull(ref _userSoftwareAppearanceWatcher);

                    _serviceRealmjoinDebounce?.Dispose(); _serviceRealmjoinDebounce = null;
                    _machineRealmJoinDebounce?.Dispose(); _machineRealmJoinDebounce = null;
                    _userRealmJoinDebounce?.Dispose(); _userRealmJoinDebounce = null;

                    DisposeAndNull(ref _serviceRealmjoinWatcher);
                    DisposeAndNull(ref _machineRealmJoinWatcher);
                    DisposeAndNull(ref _userRealmJoinWatcher);
                }
                _logger.Info($"RealmJoinWatcher: stopped ({reason})");
            }
            catch (Exception ex)
            {
                _logger.Error("RealmJoinWatcher: error during Stop", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop("disposed");
        }

        // ---- stage wiring -----------------------------------------------------------------

        private void EnsureRealmJoinServiceStage()
        {
            if (KeyExists(RegistryHive.LocalMachine, RealmJoinInfo.ServiceRealmJoinKeyPath))
            {
                AttachServiceRealmjoinSubtreeWatcher();
                return;
            }
            ArmAppearanceParentWatcher(
                role: "services",
                hive: RegistryHive.LocalMachine,
                parentPath: RealmJoinInfo.ServicesRootPath,
                targetSubKeyName: RealmJoinInfo.ServiceRealmJoinKeyName,
                targetFullPath: RealmJoinInfo.ServiceRealmJoinKeyPath,
                slot: w => { lock (_stateLock) { _servicesAppearanceWatcher = w; } },
                clearSlot: () => { lock (_stateLock) { _servicesAppearanceWatcher = null; } },
                onAppeared: AttachServiceRealmjoinSubtreeWatcher);
        }

        private void EnsureMachineRealmJoinStage()
        {
            if (KeyExists(RegistryHive.LocalMachine, RealmJoinInfo.MachineRealmJoinPath))
            {
                AttachMachineRealmJoinSubtreeWatcher();
                return;
            }
            ArmAppearanceParentWatcher(
                role: "hklm-software",
                hive: RegistryHive.LocalMachine,
                parentPath: RealmJoinInfo.MachineSoftwareRoot,
                targetSubKeyName: RealmJoinInfo.MachineRealmJoinKeyName,
                targetFullPath: RealmJoinInfo.MachineRealmJoinPath,
                slot: w => { lock (_stateLock) { _machineSoftwareAppearanceWatcher = w; } },
                clearSlot: () => { lock (_stateLock) { _machineSoftwareAppearanceWatcher = null; } },
                onAppeared: AttachMachineRealmJoinSubtreeWatcher);
        }

        private void EnsureUserRealmJoinStage()
        {
            string? sid;
            lock (_stateLock) { sid = _hkuSid; }
            if (string.IsNullOrEmpty(sid)) return;

            var fullPath = sid + "\\" + RealmJoinInfo.UserRealmJoinSubPath;
            if (KeyExists(RegistryHive.Users, fullPath))
            {
                AttachUserRealmJoinSubtreeWatcher(fullPath);
                return;
            }

            var parentPath = sid + "\\" + RealmJoinInfo.UserSoftwareSubRoot;
            ArmAppearanceParentWatcher(
                role: $"hku-{sid}-software",
                hive: RegistryHive.Users,
                parentPath: parentPath,
                targetSubKeyName: RealmJoinInfo.MachineRealmJoinKeyName, // same casing as HKLM
                targetFullPath: fullPath,
                slot: w => { lock (_stateLock) { _userSoftwareAppearanceWatcher = w; } },
                clearSlot: () => { lock (_stateLock) { _userSoftwareAppearanceWatcher = null; } },
                onAppeared: () => AttachUserRealmJoinSubtreeWatcher(fullPath));
        }

        /// <summary>
        /// Arm a <c>Name</c>-filter <see cref="RegistryWatcher"/> on <paramref name="parentPath"/>;
        /// the parent is always-existing so RegOpenKeyEx succeeds. On every change notification
        /// re-probe <paramref name="targetFullPath"/>; once it exists, dispose the parent-watcher
        /// and invoke <paramref name="onAppeared"/>. Pure event-driven — no polling.
        /// </summary>
        private void ArmAppearanceParentWatcher(
            string role,
            RegistryHive hive,
            string parentPath,
            string targetSubKeyName,
            string targetFullPath,
            Action<RegistryWatcher> slot,
            Action clearSlot,
            Action onAppeared)
        {
            try
            {
                var watcher = new RegistryWatcher(
                    hive,
                    parentPath,
                    watchSubtree: false,
                    filter: RegistryNativeMethods.RegChangeNotifyFilter.Name,
                    trace: msg => _logger.Trace($"RealmJoinWatcher(appear:{role}): {msg}"));

                watcher.Changed += (s, e) =>
                {
                    if (_disposed) return;
                    if (!KeyExists(hive, targetFullPath)) return;
                    _logger.Info($"RealmJoinWatcher: {targetSubKeyName} appeared under {parentPath} — switching to subtree watcher");
                    try { watcher.Dispose(); } catch { /* dispose may race with notification */ }
                    clearSlot();
                    try { onAppeared(); }
                    catch (Exception ex) { _logger.Error($"RealmJoinWatcher: onAppeared({role}) threw", ex); }
                };
                watcher.Error += (s, ex) => _logger.Warning($"RealmJoinWatcher(appear:{role}): {ex.Message}");

                watcher.Start();
                slot(watcher);
                _logger.Info($"RealmJoinWatcher: armed parent-watch ({role}) on {parentPath} for {targetSubKeyName} appearance");
            }
            catch (Exception ex)
            {
                _logger.Warning($"RealmJoinWatcher: failed to arm parent-watch ({role}) on {parentPath}: {ex.Message}");
            }
        }

        // ---- subtree-watcher attach (post-appearance) -------------------------------------

        private void AttachServiceRealmjoinSubtreeWatcher()
        {
            lock (_stateLock)
            {
                if (_serviceRealmjoinWatcher != null) return;
                try
                {
                    _serviceRealmjoinDebounce = new Timer(_ => CheckParameters(), null, Timeout.Infinite, Timeout.Infinite);
                    _serviceRealmjoinWatcher = new RegistryWatcher(
                        RegistryHive.LocalMachine,
                        RealmJoinInfo.ServiceRealmJoinKeyPath,
                        watchSubtree: true,
                        filter: RegistryNativeMethods.RegChangeNotifyFilter.LastSet
                              | RegistryNativeMethods.RegChangeNotifyFilter.Name,
                        trace: msg => _logger.Trace($"RealmJoinWatcher(svc): {msg}"));
                    _serviceRealmjoinWatcher.Changed += (s, e) => _serviceRealmjoinDebounce?.Change(DebounceMilliseconds, Timeout.Infinite);
                    _serviceRealmjoinWatcher.Error += (s, ex) => _logger.Warning($"RealmJoinWatcher(svc): {ex.Message}");
                    _serviceRealmjoinWatcher.Start();
                    _logger.Info("RealmJoinWatcher: attached subtree watcher on Services\\realmjoin");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"RealmJoinWatcher: failed to attach Services\\realmjoin watcher: {ex.Message}");
                }
            }

            // The appearance of Services\realmjoin is itself the RJ-detected signal —
            // surface it even if Parameters\DeploymentPhase isn't populated yet.
            NotifyRealmJoinPresence(phase: null);
            CheckParameters();
        }

        private void AttachMachineRealmJoinSubtreeWatcher()
        {
            lock (_stateLock)
            {
                if (_machineRealmJoinWatcher != null) return;
                try
                {
                    _machineRealmJoinDebounce = new Timer(_ => CheckMachinePackages(), null, Timeout.Infinite, Timeout.Infinite);
                    _machineRealmJoinWatcher = new RegistryWatcher(
                        RegistryHive.LocalMachine,
                        RealmJoinInfo.MachineRealmJoinPath,
                        watchSubtree: true,
                        filter: RegistryNativeMethods.RegChangeNotifyFilter.LastSet
                              | RegistryNativeMethods.RegChangeNotifyFilter.Name,
                        trace: msg => _logger.Trace($"RealmJoinWatcher(hklm-rj): {msg}"));
                    _machineRealmJoinWatcher.Changed += (s, e) => _machineRealmJoinDebounce?.Change(DebounceMilliseconds, Timeout.Infinite);
                    _machineRealmJoinWatcher.Error += (s, ex) => _logger.Warning($"RealmJoinWatcher(hklm-rj): {ex.Message}");
                    _machineRealmJoinWatcher.Start();
                    _logger.Info("RealmJoinWatcher: attached subtree watcher on HKLM\\SOFTWARE\\RealmJoin");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"RealmJoinWatcher: failed to attach HKLM\\SOFTWARE\\RealmJoin watcher: {ex.Message}");
                }
            }

            NotifyRealmJoinPresence(phase: null);
            CheckMachinePackages();
        }

        private void AttachUserRealmJoinSubtreeWatcher(string fullPath)
        {
            lock (_stateLock)
            {
                if (_userRealmJoinWatcher != null) return;
                try
                {
                    _userRealmJoinDebounce = new Timer(_ => CheckUserPackages(), null, Timeout.Infinite, Timeout.Infinite);
                    _userRealmJoinWatcher = new RegistryWatcher(
                        RegistryHive.Users,
                        fullPath,
                        watchSubtree: true,
                        filter: RegistryNativeMethods.RegChangeNotifyFilter.LastSet
                              | RegistryNativeMethods.RegChangeNotifyFilter.Name,
                        trace: msg => _logger.Trace($"RealmJoinWatcher(hku-rj): {msg}"));
                    _userRealmJoinWatcher.Changed += (s, e) => _userRealmJoinDebounce?.Change(DebounceMilliseconds, Timeout.Infinite);
                    _userRealmJoinWatcher.Error += (s, ex) => _logger.Warning($"RealmJoinWatcher(hku-rj): {ex.Message}");
                    _userRealmJoinWatcher.Start();
                    _logger.Info($"RealmJoinWatcher: attached subtree watcher on HKU\\{fullPath}");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"RealmJoinWatcher: failed to attach HKU\\{fullPath} watcher: {ex.Message}");
                }
            }

            CheckUserPackages();
        }

        // ---- read / fire ------------------------------------------------------------------

        private void CheckParameters()
        {
            try
            {
                var phase = RealmJoinInfo.TryReadDeploymentPhase(RegistryHive.LocalMachine);
                if (phase == null) return;
                NotifyRealmJoinPresence(phase);
            }
            catch (Exception ex)
            {
                _logger.Warning($"RealmJoinWatcher: CheckParameters threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Single chokepoint for the Detected / PhaseChanged / Resolved fan-out. Honors the
        /// fire-once dedup flags for Detected + Resolved. <paramref name="phase"/> may be null
        /// when the only thing observed so far is the appearance of a registry key (the value
        /// itself isn't populated yet).
        /// </summary>
        private void NotifyRealmJoinPresence(int? phase)
        {
            bool fireDetected;
            bool firePhaseChange = false;
            bool fireResolved = false;
            int? prevPhase = null;

            lock (_stateLock)
            {
                fireDetected = !_detectedFired;
                if (fireDetected) _detectedFired = true;

                if (phase.HasValue)
                {
                    if (_lastPhase.HasValue && _lastPhase.Value != phase.Value)
                    {
                        firePhaseChange = true;
                        prevPhase = _lastPhase;
                    }
                    _lastPhase = phase;
                    if (!_resolvedFired && phase.Value == RealmJoinInfo.PhaseCompletedFirstDeployment)
                    {
                        fireResolved = true;
                        _resolvedFired = true;
                    }
                }
            }

            if (fireDetected)
            {
                var initial = phase ?? 0;
                _logger.Info($"RealmJoinWatcher: RJ detected (phase={initial})");
                try { RealmJoinDetected?.Invoke(this, new RealmJoinDetectedEventArgs(initial)); }
                catch (Exception ex) { _logger.Error("RealmJoinWatcher: RealmJoinDetected handler threw", ex); }
            }

            if (firePhaseChange && prevPhase.HasValue && phase.HasValue)
            {
                try { RealmJoinPhaseChanged?.Invoke(this, new RealmJoinPhaseChangedEventArgs(prevPhase.Value, phase.Value)); }
                catch (Exception ex) { _logger.Error("RealmJoinWatcher: RealmJoinPhaseChanged handler threw", ex); }
            }

            if (fireResolved && phase.HasValue)
            {
                _logger.Info($"RealmJoinWatcher: RJ resolved (phase={phase.Value})");
                try { RealmJoinResolved?.Invoke(this, new RealmJoinResolvedEventArgs(phase.Value)); }
                catch (Exception ex) { _logger.Error("RealmJoinWatcher: RealmJoinResolved handler threw", ex); }
            }
        }

        private void CheckMachinePackages()
        {
            CheckPackages(
                scope: RealmJoinPackageScope.Machine,
                hive: RegistryHive.LocalMachine,
                packagesPath: RealmJoinInfo.MachinePackagesRegistryPath,
                startedSet: _hklmPackagesStarted,
                completedSet: _hklmPackagesCompleted);
        }

        private void CheckUserPackages()
        {
            string? sid;
            lock (_stateLock) { sid = _hkuSid; }
            if (string.IsNullOrEmpty(sid)) return;
            var path = sid + "\\" + RealmJoinInfo.UserPackagesRegistrySubPath;
            CheckPackages(
                scope: RealmJoinPackageScope.User,
                hive: RegistryHive.Users,
                packagesPath: path,
                startedSet: _hkuPackagesStarted,
                completedSet: _hkuPackagesCompleted);
        }

        private static class RealmJoinPackageScope
        {
            public const string Machine = "machine";
            public const string User = "user";
        }

        private void CheckPackages(
            string scope,
            RegistryHive hive,
            string packagesPath,
            HashSet<string> startedSet,
            HashSet<string> completedSet)
        {
            try
            {
                var ids = RealmJoinInfo.EnumeratePackageIds(hive, packagesPath);
                foreach (var id in ids)
                {
                    if (!RealmJoinInfo.TryReadPackage(hive, packagesPath, id, out var snap)) continue;
                    MaybeFirePackageEvents(scope, snap, startedSet, completedSet);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"RealmJoinWatcher: CheckPackages({scope}) threw: {ex.Message}");
            }
        }

        private void MaybeFirePackageEvents(
            string scope,
            RealmJoinPackageSnapshot snap,
            HashSet<string> startedSet,
            HashSet<string> completedSet)
        {
            bool fireStarted = false;
            bool fireCompleted = false;
            lock (_stateLock)
            {
                if (snap.HasStartedMarker && !startedSet.Contains(snap.PackageId))
                {
                    startedSet.Add(snap.PackageId);
                    fireStarted = true;
                }
                if (snap.HasCompletionMarker && !completedSet.Contains(snap.PackageId))
                {
                    completedSet.Add(snap.PackageId);
                    fireCompleted = true;
                }
            }

            if (fireStarted)
            {
                _logger.Info($"RealmJoinWatcher: package started (scope={scope}, id={snap.PackageId})");
                try { RealmJoinPackageStarted?.Invoke(this, new RealmJoinPackageEventArgs(scope, snap)); }
                catch (Exception ex) { _logger.Error("RealmJoinWatcher: RealmJoinPackageStarted handler threw", ex); }
            }

            if (fireCompleted)
            {
                _logger.Info($"RealmJoinWatcher: package completed (scope={scope}, id={snap.PackageId}, success={snap.Success}, exitCode={snap.LastExitCode})");
                try { RealmJoinPackageCompleted?.Invoke(this, new RealmJoinPackageEventArgs(scope, snap)); }
                catch (Exception ex) { _logger.Error("RealmJoinWatcher: RealmJoinPackageCompleted handler threw", ex); }
            }
        }

        // ---- utils ------------------------------------------------------------------------

        private static bool KeyExists(RegistryHive hive, string subPath)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                using (var key = baseKey.OpenSubKey(subPath, writable: false))
                {
                    return key != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void DisposeAndNull(ref RegistryWatcher? w)
        {
            if (w == null) return;
            try { w.Dispose(); } catch { }
            w = null;
        }

        // Test seams — drive the read paths deterministically without touching the real registry.
        internal void TriggerCheckParametersFromTest() => CheckParameters();
        internal void TriggerCheckMachinePackagesFromTest() => CheckMachinePackages();
        internal void TriggerCheckUserPackagesFromTest() => CheckUserPackages();
    }
}
