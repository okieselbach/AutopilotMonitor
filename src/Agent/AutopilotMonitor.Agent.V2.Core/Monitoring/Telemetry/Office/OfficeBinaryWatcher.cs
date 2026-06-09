#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office
{
    /// <summary>
    /// Event-driven completion proof for an Office C2R install: watches the C2R install tree for any of
    /// the core Office app binaries (<c>WINWORD.EXE</c> / <c>EXCEL.EXE</c> / <c>POWERPNT.EXE</c> /
    /// <c>OUTLOOK.EXE</c>) appearing on disk. C2R lays these down in the integrate phase — their
    /// presence is the one reliable "Office is installed" signal (the DO job aggregate is unreliable for
    /// completion: multi-job churn never reaches an aggregate 100% and Connected-Cache delivery makes the
    /// stream near-instant — field session 7da7dead).
    /// <para>
    /// <b>Any-of</b> by design: a deployment can exclude products (e.g. Word only, no Outlook), so the
    /// first of the four binaries to appear is enough. The watcher fires <see cref="BinaryAppeared"/> on
    /// EVERY matching filesystem event (NOT once) until it is disposed — the host re-probes on each raise
    /// and disposes the watcher when the lifecycle terminates. Raising repeatedly is deliberate: the
    /// first FS event for a binary can precede the on-disk completion proof under
    /// <c>{InstallationPath}\root\OfficeNN\</c> (the binary is streamed before the integrate <c>root</c>
    /// junction is laid down), so a single early raise must NOT be the only chance to complete — field
    /// session c2171821, where Office finished on disk yet no completed event fired.
    /// </para>
    /// <para>
    /// <b>Defensive re-probe</b> (<see cref="ScheduleRecheck"/>): C2R can create the <c>root</c> junction
    /// at integrate-end without raising a further per-file <c>*.exe</c> event (the files already exist at
    /// the junction target). The host arms a bounded re-probe timer — only after a raise that the probe
    /// rejected as not-yet — that re-raises <see cref="BinaryAppeared"/> on a cadence until the proof is
    /// found or the bound is exhausted. It is never armed on the happy path (first probe already finds
    /// the binaries), so there is no polling in the common case.
    /// </para>
    /// Fail-soft: any failure is logged and the watcher stays quiet (no false completion).
    /// </summary>
    public sealed class OfficeBinaryWatcher : IDisposable
    {
        // C2R lays the binaries down under {InstallationPath}\root\OfficeNN\ — the version folder is
        // enumerated (IncludeSubdirectories), never hardcoded.
        private const int ArmRetryDelaySeconds = 5;
        private const int MaxArmRetries = 24; // ~2 min — the InstallationPath dir is normally already present
        private const int DefaultRecheckIntervalMs = 10_000; // 10 s
        private const int DefaultMaxRechecks = 30; // ~5 min of bounded defensive re-probing

        private readonly string _installationPath;
        private readonly HashSet<string> _binaries; // upper-cased leaf names
        private readonly AgentLogger _logger;
        private readonly int _recheckIntervalMs;
        private readonly int _maxRechecks;
        private readonly object _lock = new object();

        private FileSystemWatcher? _fsw;
        private Timer? _armRetryTimer;
        private Timer? _recheckTimer;
        private int _armAttempts;
        private int _recheckAttempts;
        private int _raising;  // 0/1 — coalesces concurrent raises (re-entrancy guard, NOT a one-shot latch)
        private bool _disposed;

        /// <summary>Raised whenever a core Office binary may be present on disk — the host probes and
        /// either completes the lifecycle or keeps watching.</summary>
        public event EventHandler? BinaryAppeared;

        public OfficeBinaryWatcher(
            string installationPath,
            IEnumerable<string> binaries,
            AgentLogger logger,
            int recheckIntervalMs = DefaultRecheckIntervalMs,
            int maxRechecks = DefaultMaxRechecks)
        {
            _installationPath = installationPath ?? throw new ArgumentNullException(nameof(installationPath));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _recheckIntervalMs = recheckIntervalMs > 0 ? recheckIntervalMs : DefaultRecheckIntervalMs;
            _maxRechecks = maxRechecks > 0 ? maxRechecks : DefaultMaxRechecks;
            _binaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in binaries ?? Array.Empty<string>()) _binaries.Add(b);
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_disposed) return;
                TryArm();
            }
        }

        /// <summary>
        /// Arm the bounded defensive re-probe. Called by the host after a <see cref="BinaryAppeared"/>
        /// raise whose on-disk probe came back not-yet — covers the integrate-junction race where no
        /// further filesystem event arrives. Idempotent (a single timer); self-cancels after
        /// <see cref="_maxRechecks"/> attempts and is cancelled on <see cref="Dispose"/>. Never armed on
        /// the happy path, so the common install does no polling at all.
        /// </summary>
        public void ScheduleRecheck()
        {
            lock (_lock)
            {
                if (_disposed || _recheckTimer != null) return; // already scheduled (or done)
                _recheckAttempts = 0;
                _recheckTimer = new Timer(OnRecheck, null, TimeSpan.FromMilliseconds(_recheckIntervalMs), Timeout.InfiniteTimeSpan);
            }
        }

        private void OnRecheck(object? state)
        {
            lock (_lock)
            {
                if (_disposed) return;
                if (_recheckAttempts++ >= _maxRechecks)
                {
                    _logger.Debug($"[OfficeBinaryWatcher] completion re-probe exhausted after {_maxRechecks} attempts — no on-disk proof");
                    _recheckTimer?.Dispose();
                    _recheckTimer = null;
                    return;
                }
            }

            // Re-raise OUTSIDE the lock: the host's handler probes and may dispose this watcher on
            // completion (which cancels the timer). Holding _lock across the external callback would risk
            // a lock-ordering deadlock with the host.
            Raise();

            lock (_lock)
            {
                if (_disposed || _recheckTimer == null) return; // host completed/disposed during the raise
                _recheckTimer.Change(TimeSpan.FromMilliseconds(_recheckIntervalMs), Timeout.InfiniteTimeSpan);
            }
        }

        // Caller holds _lock.
        private void TryArm()
        {
            if (_disposed || _fsw != null) return;

            // Initial scan — the binaries may already be present (armed late / update over existing).
            if (CoreBinariesPresent())
            {
                Raise();
                return;
            }

            if (!Directory.Exists(_installationPath))
            {
                // The install root is not there yet (very early). Retry a bounded number of times; the
                // registry InstallationPath value normally precedes the directory only briefly.
                if (_armAttempts++ < MaxArmRetries)
                {
                    _armRetryTimer?.Dispose();
                    _armRetryTimer = new Timer(OnArmRetry, null, TimeSpan.FromSeconds(ArmRetryDelaySeconds), Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _logger.Debug($"[OfficeBinaryWatcher] install path '{_installationPath}' never appeared — giving up (no completion proof)");
                }
                return;
            }

            try
            {
                _fsw = new FileSystemWatcher(_installationPath)
                {
                    IncludeSubdirectories = true,
                    Filter = "*.exe",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                _fsw.Created += OnFileEvent;
                _fsw.Changed += OnFileEvent;
                _fsw.Renamed += OnFileRenamed;
                _fsw.EnableRaisingEvents = true;
                _logger.Info($"[OfficeBinaryWatcher] watching '{_installationPath}' for core Office binaries");

                // Race: a binary may have appeared between the scan above and arming — re-scan once.
                if (CoreBinariesPresent()) Raise();
            }
            catch (Exception ex)
            {
                _logger.Warning($"[OfficeBinaryWatcher] could not watch '{_installationPath}': {ex.Message}");
            }
        }

        private void OnArmRetry(object? state)
        {
            lock (_lock) { TryArm(); }
        }

        private void OnFileEvent(object sender, FileSystemEventArgs e) => CheckCandidate(e.Name);
        private void OnFileRenamed(object sender, RenamedEventArgs e) => CheckCandidate(e.Name);

        private void CheckCandidate(string? relativeName)
        {
            try
            {
                if (string.IsNullOrEmpty(relativeName)) return;
                var leaf = Path.GetFileName(relativeName);
                if (!string.IsNullOrEmpty(leaf) && _binaries.Contains(leaf))
                {
                    _logger.Info($"[OfficeBinaryWatcher] core Office binary appeared: {leaf}");
                    Raise();
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[OfficeBinaryWatcher] candidate check error: {ex.Message}");
            }
        }

        private bool CoreBinariesPresent()
            => OfficeInstallDetector.CoreBinariesPresentOnDisk(_installationPath, _logger);

        /// <summary>
        /// Raise <see cref="BinaryAppeared"/>. Repeatable (NOT a one-shot): the host re-probes on each
        /// raise and disposes the watcher when the lifecycle terminates. The <see cref="_raising"/> guard
        /// only coalesces concurrent raises (FS-thread + timer-thread) so they do not overlap; sequential
        /// raises are intended.
        /// </summary>
        private void Raise()
        {
            if (Volatile.Read(ref _disposed)) return;
            if (Interlocked.Exchange(ref _raising, 1) != 0) return; // a raise is already in flight — coalesce
            try
            {
                if (Volatile.Read(ref _disposed)) return;
                BinaryAppeared?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) { _logger.Warning($"[OfficeBinaryWatcher] BinaryAppeared handler threw: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _raising, 0); }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _armRetryTimer?.Dispose();
                _armRetryTimer = null;
                _recheckTimer?.Dispose();
                _recheckTimer = null;
                if (_fsw != null)
                {
                    try { _fsw.EnableRaisingEvents = false; } catch { }
                    try { _fsw.Created -= OnFileEvent; } catch { }
                    try { _fsw.Changed -= OnFileEvent; } catch { }
                    try { _fsw.Renamed -= OnFileRenamed; } catch { }
                    try { _fsw.Dispose(); } catch { }
                    _fsw = null;
                }
            }
        }
    }
}
