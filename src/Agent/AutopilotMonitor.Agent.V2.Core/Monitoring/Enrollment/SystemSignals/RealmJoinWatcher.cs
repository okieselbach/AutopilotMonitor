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
        /// <summary>
        /// Bare version parsed from <c>C:\Program Files\RealmJoin\RealmJoin.exe</c>'s
        /// file-version resource (or the x86 fallback), e.g. <c>4.21.6</c>. <c>null</c> when
        /// the binary is missing, locked, or read fails — version is observability-only and
        /// never blocks detection.
        /// </summary>
        public string? ProductVersion { get; }

        /// <summary>
        /// RJ release channel parsed from the version string's SemVer prerelease tag
        /// ("beta", "canary"); <see cref="RealmJoinInfo.ReleaseChannelStable"/> when untagged;
        /// <c>null</c> when the version was unreadable.
        /// </summary>
        public string? ReleaseChannel { get; }

        public RealmJoinDetectedEventArgs(int deploymentPhase, string? productVersion = null, string? releaseChannel = null)
        {
            DeploymentPhase = deploymentPhase;
            ProductVersion = productVersion;
            ReleaseChannel = releaseChannel;
        }
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

        // Registry-access seams. Production binds these to RealmJoinInfo + the real KeyExists
        // probe via the public ctor below; tests inject fakes through the internal ctor to drive
        // enumerate / read / exists outcomes deterministically without touching the live hive.
        internal delegate IReadOnlyList<string> EnumeratePackageIdsDelegate(RegistryHive hive, string packagesPath);
        internal delegate bool TryReadPackageDelegate(RegistryHive hive, string packagesPath, string packageId, out RealmJoinPackageSnapshot snapshot);
        internal delegate bool KeyExistsDelegate(RegistryHive hive, string subPath);

        private readonly AgentLogger _logger;
        private readonly EnumeratePackageIdsDelegate _enumeratePackageIds;
        private readonly TryReadPackageDelegate _tryReadPackage;
        private readonly KeyExistsDelegate _keyExists;
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
        // Set once the first non-Blank DeploymentPhase (>= 100) is observed. Package watchers
        // (HKLM\SOFTWARE\RealmJoin + HKU\<sid>\SOFTWARE\RealmJoin) only arm at that point —
        // earlier observation would mix IME's in-flight package writes (which share the same
        // subtree) into RJ's lifecycle.
        private bool _packageWatchersArmed;
        private bool _disposed;

        public event EventHandler<RealmJoinDetectedEventArgs>? RealmJoinDetected;
        public event EventHandler<RealmJoinResolvedEventArgs>? RealmJoinResolved;
        public event EventHandler<RealmJoinPhaseChangedEventArgs>? RealmJoinPhaseChanged;
        public event EventHandler<RealmJoinPackageEventArgs>? RealmJoinPackageStarted;
        public event EventHandler<RealmJoinPackageEventArgs>? RealmJoinPackageCompleted;

        public RealmJoinWatcher(AgentLogger logger)
            : this(logger, RealmJoinInfo.EnumeratePackageIds, RealmJoinInfo.TryReadPackage, KeyExistsViaRegistry)
        {
        }

        internal RealmJoinWatcher(
            AgentLogger logger,
            EnumeratePackageIdsDelegate enumeratePackageIds,
            TryReadPackageDelegate tryReadPackage,
            KeyExistsDelegate keyExists)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _enumeratePackageIds = enumeratePackageIds ?? throw new ArgumentNullException(nameof(enumeratePackageIds));
            _tryReadPackage = tryReadPackage ?? throw new ArgumentNullException(nameof(tryReadPackage));
            _keyExists = keyExists ?? throw new ArgumentNullException(nameof(keyExists));
        }

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RealmJoinWatcher));

            // The Services\realmjoin gate is the SOLE entry point. HKLM\SOFTWARE\RealmJoin and
            // the HKU watcher do not run until this fires — RJ presence is the prerequisite for
            // touching those keys at all. On RJ-less devices the only standing observer is the
            // parent-watch on HKLM\SYSTEM\CurrentControlSet\Services.
            EnsureRealmJoinServiceStage();
        }

        /// <summary>
        /// Record the user SID for HKU watching. Called by the host once
        /// <see cref="DesktopArrivalDetector"/> resolves a real user owner and
        /// <see cref="UserSidResolver"/> produces the SID. Idempotent.
        /// <para>
        /// The actual HKU watcher only arms once the package-watcher arming threshold has
        /// fired (RJ observed at <see cref="RealmJoinInfo.PhaseRunningThresholdMin"/> or
        /// higher). Either it has already fired (immediate arm) or fires later
        /// (deferred arm from <see cref="NotifyRealmJoinPresence"/>). On devices without RJ
        /// or where RJ never leaves Blank, the SID is recorded but no HKU watcher is created.
        /// </para>
        /// </summary>
        public void ArmHku(string sid)
        {
            if (_disposed) return;
            if (string.IsNullOrEmpty(sid)) return;

            bool armNow;
            lock (_stateLock)
            {
                if (_hkuSid != null) return;
                _hkuSid = sid;
                armNow = _packageWatchersArmed;
            }
            _logger.Info($"RealmJoinWatcher: recorded SID {sid} for HKU watching (armNow={armNow})");

            if (armNow)
            {
                // EnsureUserRealmJoinStage -> AttachUserRealmJoinSubtreeWatcher seeds the dedup
                // sets BEFORE its internal CheckUserPackages sweep. A standalone CheckUserPackages
                // here would run pre-seed and surface pre-existing user-hive package sub-keys
                // (typical: RJ packages installed during ESP before phase >= 100) as spurious
                // started+completed events. The attach path is the single, ordered entry point.
                EnsureUserRealmJoinStage();
            }
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
            if (_keyExists(RegistryHive.LocalMachine, RealmJoinInfo.ServiceRealmJoinKeyPath))
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
            if (_keyExists(RegistryHive.LocalMachine, RealmJoinInfo.MachineRealmJoinPath))
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
            if (_keyExists(RegistryHive.Users, fullPath))
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
                    view: RegistryView.Registry64,
                    filter: RegistryNativeMethods.RegChangeNotifyFilter.Name,
                    trace: msg => _logger.Trace($"RealmJoinWatcher(appear:{role}): {msg}"));

                watcher.Changed += (s, e) =>
                {
                    if (_disposed) return;
                    if (!_keyExists(hive, targetFullPath)) return;
                    _logger.Info($"RealmJoinWatcher: {targetSubKeyName} appeared under {parentPath} — switching to subtree watcher");

                    // CRITICAL: this callback runs on the RegistryWatcher's own background
                    // thread. Calling Dispose() here would re-enter Stop() → Thread.Join()
                    // and self-deadlock (the thread would join itself). Use RequestStop()
                    // (non-blocking — see RegistryWatcher.RequestStop) and let a thread-pool
                    // worker dispose the watcher object after the loop has exited.
                    try { watcher.RequestStop(); } catch { /* nothing to do */ }
                    clearSlot();
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { watcher.Dispose(); } catch { /* dispose must not throw */ }
                    });

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
                        view: RegistryView.Registry64,
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
            // surface it even if Parameters\DeploymentPhase isn't populated yet. The package
            // watchers (HKLM + HKU SOFTWARE\RealmJoin) intentionally stay disarmed here:
            // NotifyRealmJoinPresence arms them once DeploymentPhase >= PhaseRunningThresholdMin
            // is observed. Before that the same subtree may still receive in-flight IME
            // package writes that are not part of RJ's lifecycle.
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
                    // Seed dedup sets with every package sub-key already present at arming time.
                    // Pre-existing rows are typically RJ packages installed during ESP — they are
                    // historical, not "new" from the watcher's perspective, and must not surface
                    // as started/completed events. Must run BEFORE Start() so no subtree wake-up
                    // can race past the seed and emit pre-existing IDs.
                    SeedExistingPackageIds(
                        RegistryHive.LocalMachine,
                        RealmJoinInfo.MachinePackagesRegistryPath,
                        _hklmPackagesStarted,
                        _hklmPackagesCompleted,
                        scopeLabel: "hklm");

                    _machineRealmJoinDebounce = new Timer(_ => CheckMachinePackages(), null, Timeout.Infinite, Timeout.Infinite);
                    _machineRealmJoinWatcher = new RegistryWatcher(
                        RegistryHive.LocalMachine,
                        RealmJoinInfo.MachineRealmJoinPath,
                        watchSubtree: true,
                        view: RegistryView.Registry64,
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
                    // Seed dedup sets — see AttachMachineRealmJoinSubtreeWatcher for rationale.
                    var userPackagesPath = fullPath + "\\Packages";
                    SeedExistingPackageIds(
                        RegistryHive.Users,
                        userPackagesPath,
                        _hkuPackagesStarted,
                        _hkuPackagesCompleted,
                        scopeLabel: "hku");

                    _userRealmJoinDebounce = new Timer(_ => CheckUserPackages(), null, Timeout.Infinite, Timeout.Infinite);
                    _userRealmJoinWatcher = new RegistryWatcher(
                        RegistryHive.Users,
                        fullPath,
                        watchSubtree: true,
                        view: RegistryView.Registry64,
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
            bool armPackageWatchers = false;
            bool armUserPackageWatcherToo = false;

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
                    // Arm the package watchers once RJ leaves Blank (phase >= 100). Before that,
                    // IME may still be writing to HKLM\SOFTWARE\RealmJoin during the pre-RJ
                    // install window and we'd misattribute those rows to RJ's lifecycle.
                    if (!_packageWatchersArmed && phase.Value >= RealmJoinInfo.PhaseRunningThresholdMin)
                    {
                        _packageWatchersArmed = true;
                        armPackageWatchers = true;
                        armUserPackageWatcherToo = _hkuSid != null;
                    }
                }
            }

            if (fireDetected)
            {
                var initial = phase ?? 0;
                // Read RJ version + release channel best-effort (fail-soft to null) ONLY when
                // the Detected event actually fires — single read per session lifetime keeps
                // any potential file-system cost to a single hit.
                var versionInfo = RealmJoinInfo.TryReadRealmJoinVersionInfo();
                _logger.Info($"RealmJoinWatcher: RJ detected (phase={initial}, productVersion={versionInfo.ProductVersion ?? "<unknown>"}, releaseChannel={versionInfo.ReleaseChannel ?? "<unknown>"})");
                try { RealmJoinDetected?.Invoke(this, new RealmJoinDetectedEventArgs(initial, versionInfo.ProductVersion, versionInfo.ReleaseChannel)); }
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

            if (armPackageWatchers)
            {
                _logger.Info($"RealmJoinWatcher: phase={phase} crossed RunningThreshold — arming package watchers (HKLM + HKU={armUserPackageWatcherToo})");
                EnsureMachineRealmJoinStage();
                if (armUserPackageWatcherToo)
                {
                    // EnsureUserRealmJoinStage -> AttachUserRealmJoinSubtreeWatcher seeds the
                    // dedup sets BEFORE its internal CheckUserPackages sweep. See the matching
                    // note in ArmHku — running a standalone CheckUserPackages here would re-fire
                    // every pre-existing user-hive package sub-key (ESP-installed RJ packages)
                    // as a started+completed pair before the seed had a chance to suppress them.
                    EnsureUserRealmJoinStage();
                }
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
                var ids = _enumeratePackageIds(hive, packagesPath);
                foreach (var id in ids)
                {
                    if (!_tryReadPackage(hive, packagesPath, id, out var snap)) continue;
                    MaybeFirePackageEvents(scope, snap, startedSet, completedSet);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"RealmJoinWatcher: CheckPackages({scope}) threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Pre-populate the started + completed dedup sets with every package sub-key already
        /// present at arming time. Caller MUST hold <see cref="_stateLock"/>. Pre-existing keys
        /// are historical (typically RJ packages installed during ESP before the phase gate
        /// fired) and are intentionally suppressed — only sub-keys that appear AFTER arming
        /// surface as started/completed events. Seeding both sets means a pre-existing key is
        /// fully ignored, including any late completion-marker write.
        /// </summary>
        private void SeedExistingPackageIds(
            RegistryHive hive,
            string packagesPath,
            HashSet<string> startedSet,
            HashSet<string> completedSet,
            string scopeLabel)
        {
            try
            {
                var ids = _enumeratePackageIds(hive, packagesPath);
                if (ids.Count == 0)
                {
                    _logger.Info($"RealmJoinWatcher: seed pass ({scopeLabel}) found no pre-existing packages under {packagesPath}");
                    return;
                }
                int seeded = 0;
                foreach (var id in ids)
                {
                    if (startedSet.Add(id)) seeded++;
                    completedSet.Add(id);
                }
                _logger.Info($"RealmJoinWatcher: seeded {seeded} pre-existing package id(s) under {packagesPath} ({scopeLabel}) — events suppressed for these");
            }
            catch (Exception ex)
            {
                _logger.Warning($"RealmJoinWatcher: seed pass ({scopeLabel}) under {packagesPath} threw: {ex.Message}");
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
                // Started fires the FIRST time we observe the <packageId> subkey, period.
                // DisplayName is just a passthrough metadata field — today's RJ doesn't always
                // populate it, so gating on snap.HasStartedMarker would silently drop the
                // started signal for most packages. A future RJ version that writes DisplayName
                // at install-start gets it surfaced for free via the adapter payload.
                if (!startedSet.Contains(snap.PackageId))
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

        // Default <see cref="_keyExists"/> binding for the public ctor — the real registry probe
        // used in production. Test ctors inject a fake to drive deterministic outcomes.
        private static bool KeyExistsViaRegistry(RegistryHive hive, string subPath)
        {
            try
            {
                // Registry64 is mandatory here — when the .NET host happens to run as 32-bit
                // (mirroring TenantIdResolver's defensive note) HKLM\SOFTWARE silently redirects
                // to WOW6432Node, so RealmJoin's 64-bit-only keys would appear missing.
                using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
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

        internal void TriggerMachinePackageObservationFromTest(RealmJoinPackageSnapshot snap) =>
            MaybeFirePackageEvents(RealmJoinPackageScope.Machine, snap, _hklmPackagesStarted, _hklmPackagesCompleted);

        internal void TriggerUserPackageObservationFromTest(RealmJoinPackageSnapshot snap) =>
            MaybeFirePackageEvents(RealmJoinPackageScope.User, snap, _hkuPackagesStarted, _hkuPackagesCompleted);

        internal void TriggerNotifyRealmJoinPresenceFromTest(int? phase) => NotifyRealmJoinPresence(phase);

        // Test seam — directly seed the dedup sets without touching the registry, so that a
        // subsequent MaybeFirePackageEvents observation can verify suppression behavior.
        internal void SeedMachinePackageIdsForTest(params string[] ids)
        {
            lock (_stateLock)
            {
                foreach (var id in ids)
                {
                    _hklmPackagesStarted.Add(id);
                    _hklmPackagesCompleted.Add(id);
                }
            }
        }

        internal void SeedUserPackageIdsForTest(params string[] ids)
        {
            lock (_stateLock)
            {
                foreach (var id in ids)
                {
                    _hkuPackagesStarted.Add(id);
                    _hkuPackagesCompleted.Add(id);
                }
            }
        }

        internal bool PackageWatchersArmedForTest
        {
            get { lock (_stateLock) { return _packageWatchersArmed; } }
        }
    }
}
