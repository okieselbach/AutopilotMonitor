#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers
{
    /// <summary>
    /// Startup forensic complement to the live <see cref="Security.ConsoleBypassWatcher"/>. The live
    /// watcher only sees consoles spawned after the agent started; a <b>Shift+F10</b> pressed earlier
    /// in OOBE (before the agent installed) is its blind spot. Windows writes a Prefetch artifact
    /// (<c>%WINDIR%\Prefetch\CMD.EXE-*.pf</c>) when cmd.exe runs — empirically confirmed to fire for a
    /// Shift+F10 console in OOBE — so a cmd prefetch file whose last-run is <i>after this boot</i> is a
    /// coarse "a console executed on this device" signal that covers the pre-agent window.
    /// <para>
    /// <b>Honest limits</b> (encoded in the event payload):
    /// <list type="bullet">
    ///   <item>cmd.exe shares ONE prefetch file regardless of launcher. Once ESP runs, Intune Win32
    ///     apps legitimately spawn <c>cmd.exe /c ...</c>, so the last-run timestamp can no longer be
    ///     attributed to Shift+F10 vs. an install — hence <c>attribution: "coarse"</c>.</item>
    ///   <item>v1 reads only the file's NTFS last-write time (stat-only); it does NOT parse the
    ///     MAM-compressed run-history inside the .pf.</item>
    ///   <item>Prefetch may be disabled on some images → no artifact, no signal (fail-soft).</item>
    /// </list>
    /// Runs only at startup (the artifact is pre-enrollment state); shutdown is a no-op — a console
    /// opened during enrollment is the live watcher's job. Restart-deduped via the StartupEventGate.
    /// </para>
    /// </summary>
    public sealed class ConsolePrefetchScanner : IAgentAnalyzer
    {
        internal const string CmdPrefetchPattern = "CMD.EXE-*.pf";
        internal const string ConhostPrefetchPattern = "CONHOST.EXE-*.pf";

        /// <summary>
        /// M5: absorbs the clock-epoch mismatch between the .pf NTFS last-write (stamped under
        /// the clock at write time) and the boot time derived at scan time as now-minus-uptime
        /// (shifts with every NTP correction). OOBE clock corrections are typically seconds to a
        /// few minutes; image/sysprep artifacts predate boot by far more than this.
        /// </summary>
        internal static readonly TimeSpan BootClockSkewTolerance = TimeSpan.FromMinutes(10);

        private const string Attribution =
            "coarse: a console executed on this device; cannot distinguish a human Shift+F10 from a " +
            "legitimate install-launched cmd once ESP is running (cmd.exe shares one prefetch artifact)";
        private const string CoverageNote =
            "complements the live console watcher by covering the pre-agent OOBE window";

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly Core.Persistence.StartupEventGate? _startupGate;
        private readonly string _prefetchDirectory;
        private readonly Func<DateTime?> _bootTimeProvider;

        public string Name => "ConsolePrefetchScanner";

        public ConsolePrefetchScanner(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            Core.Persistence.StartupEventGate? startupGate = null,
            string? prefetchDirectory = null,
            Func<DateTime?>? bootTimeProvider = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _startupGate = startupGate;
            _prefetchDirectory = prefetchDirectory ?? DefaultPrefetchDirectory();
            _bootTimeProvider = bootTimeProvider ?? QueryBootTimeUtc;
        }

        public void AnalyzeAtStartup()
        {
            _logger.Info($"{Name}: scanning prefetch for a post-boot cmd.exe console artifact");
            try { RunScan(); }
            catch (Exception ex) { _logger.Error($"{Name}: scan failed unexpectedly", ex); }
        }

        public void AnalyzeAtShutdown()
        {
            // A console opened DURING enrollment is the live watcher's job; the prefetch artifact is
            // ambiguous once ESP-launched cmd contaminates it. Re-scanning at shutdown would add noise.
            _logger.Debug($"{Name}: shutdown no-op (pre-agent forensic already captured at startup)");
        }

        private void RunScan()
        {
            if (!Directory.Exists(_prefetchDirectory))
            {
                _logger.Debug($"{Name}: prefetch directory not present ({_prefetchDirectory}) — prefetch likely disabled, no signal");
                return;
            }

            var cmdArtifact = NewestArtifact(CmdPrefetchPattern);
            if (cmdArtifact == null)
            {
                _logger.Debug($"{Name}: no {CmdPrefetchPattern} artifact present — cmd.exe has not run");
                return;
            }

            var bootTimeUtc = _bootTimeProvider();
            if (!bootTimeUtc.HasValue)
            {
                _logger.Warning($"{Name}: could not determine boot time — cannot evaluate ranAfterBoot; skipping");
                return;
            }

            // M5 (delta review 2026-07-02): the two timestamps come from different clock epochs.
            // The .pf last-write was stamped under the clock at write time; boot time is derived
            // at SCAN time as now-minus-uptime, so an NTP correction in between (routine during
            // OOBE) shifts it by the correction amount. A forward correction made a real
            // Shift+F10 console look pre-boot ("stale image artifact"). The tolerance absorbs
            // typical OOBE clock corrections; image/sysprep artifacts are hours-to-months older
            // than boot and stay firmly below it.
            bool ranAfterBoot = cmdArtifact.Value.lastRunUtc > bootTimeUtc.Value - BootClockSkewTolerance;
            if (!ranAfterBoot)
            {
                // Stale artifact from the image build / sysprep — not this boot's console.
                _logger.Debug($"{Name}: cmd prefetch last-run {cmdArtifact.Value.lastRunUtc:o} predates boot " +
                    $"{bootTimeUtc.Value:o} (incl. {BootClockSkewTolerance.TotalMinutes:F0} min skew tolerance) — stale image artifact, not flagged");
                return;
            }

            var corroborating = ArtifactsRanAfter(ConhostPrefetchPattern, bootTimeUtc.Value);

            var data = new Dictionary<string, object>
            {
                { "decision", "console_prefetch_after_boot" },
                { "artifact", cmdArtifact.Value.name },
                { "prefetchLastRunUtc", cmdArtifact.Value.lastRunUtc.ToString("o") },
                { "bootTimeUtc", bootTimeUtc.Value.ToString("o") },
                { "ranAfterBoot", true },
                { "corroboratingArtifacts", corroborating },
                { "detectedVia", "PrefetchTimestampScan" },
                { "attribution", Attribution },
                { "coverageNote", CoverageNote },
                { "coverageComplete", false },
            };

            // Restart dedup — once per enrollment (M6, delta review 2026-07-02). The fingerprint
            // used to include prefetchLastRunUtc + corroboratingArtifacts, but every ESP `cmd /c`
            // installer wrapper refreshes CMD.EXE-*.pf between reboots, so multi-reboot sessions
            // re-warned per restart. The scanner cannot distinguish a new human console from
            // routine installer cmd churn via the artifact timestamp — one Warning per enrollment
            // is the honest signal; the live ConsoleBypassWatcher covers consoles opened later.
            if (_startupGate != null
                && !_startupGate.ShouldEmit(Constants.EventTypes.ConsolePrefetchDetected, "detected"))
            {
                _logger.Debug($"{Name}: console prefetch already reported this enrollment — suppressed (restart dedup)");
                return;
            }

            _logger.Warning($"{Name}: cmd.exe prefetch artifact ran after boot ({cmdArtifact.Value.name}, " +
                $"last-run {cmdArtifact.Value.lastRunUtc:o}) — possible Shift+F10 console (pre-agent window)");

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.ConsolePrefetchDetected,
                Severity = EventSeverity.Warning,
                Source = Name,
                Phase = EnrollmentPhase.Unknown,
                Message = "cmd.exe prefetch artifact ran after boot — possible Shift+F10 console (pre-agent window)",
                ImmediateUpload = true,
                Data = data,
            });

            // M4: commit the gate claim only after the Warning went out — committing first let a
            // crash in between suppress this security event for the rest of the enrollment.
            _startupGate?.MarkEmitted(Constants.EventTypes.ConsolePrefetchDetected);
        }

        /// <summary>Newest (max last-write) artifact matching <paramref name="pattern"/>, or null.</summary>
        private (string name, DateTime lastRunUtc)? NewestArtifact(string pattern)
        {
            (string name, DateTime lastRunUtc)? newest = null;
            foreach (var path in EnumerateArtifacts(pattern))
            {
                DateTime lastWriteUtc;
                try { lastWriteUtc = File.GetLastWriteTimeUtc(path); }
                catch (Exception ex) { _logger.Debug($"{Name}: stat failed for {path}: {ex.Message}"); continue; }

                if (newest == null || lastWriteUtc > newest.Value.lastRunUtc)
                    newest = (Path.GetFileName(path), lastWriteUtc);
            }
            return newest;
        }

        private List<string> ArtifactsRanAfter(string pattern, DateTime thresholdUtc)
        {
            var hits = new List<string>();
            foreach (var path in EnumerateArtifacts(pattern))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) > thresholdUtc)
                        hits.Add(Path.GetFileName(path));
                }
                catch (Exception ex) { _logger.Debug($"{Name}: stat failed for {path}: {ex.Message}"); }
            }
            // Stable order: Directory.EnumerateFiles gives no ordering guarantee, and this list feeds
            // the StartupEventGate fingerprint — an unsorted set would hash differently across restarts
            // and re-emit the same finding.
            hits.Sort(StringComparer.OrdinalIgnoreCase);
            return hits;
        }

        private IEnumerable<string> EnumerateArtifacts(string pattern)
        {
            try { return Directory.EnumerateFiles(_prefetchDirectory, pattern, SearchOption.TopDirectoryOnly); }
            catch (Exception ex)
            {
                _logger.Debug($"{Name}: enumerate '{pattern}' in {_prefetchDirectory} failed: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        private static string DefaultPrefetchDirectory()
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Prefetch");

        private DateTime? QueryBootTimeUtc()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
                foreach (var mo in searcher.Get())
                {
                    using (mo)
                    {
                        var raw = mo["LastBootUpTime"]?.ToString();
                        if (string.IsNullOrEmpty(raw)) continue;
                        var local = ManagementDateTimeConverter.ToDateTime(raw);
                        return DateTime.SpecifyKind(local.ToUniversalTime(), DateTimeKind.Utc);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"{Name}: boot-time query failed: {ex.Message}");
            }
            return null;
        }
    }
}
