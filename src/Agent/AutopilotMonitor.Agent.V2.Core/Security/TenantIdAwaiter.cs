using System;
using System.Diagnostics;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Event-driven TenantId resolution wait. When the initial
    /// <see cref="TenantIdResolver"/> probe finds nothing, the awaiter subscribes to
    /// a registry-change signal (production: <see cref="CompositeRegistryChangeSignal"/>
    /// over Enrollments + CloudDomainJoin), re-probes silently on every change, and
    /// returns the TenantId as soon as the registry catches up — or <c>null</c> on
    /// timeout. A 30 s belt-and-suspenders periodic re-probe protects against a
    /// missed Win32 notification.
    /// <para>
    /// Used to bridge the OOBE / hybrid-AAD-join race window where the agent fires
    /// before <c>AADTenantID</c> / <c>CloudDomainJoin\TenantInfo</c> are written.
    /// Field evidence (GardnerMedia, 2026-04-30): the agent fired ~5 minutes before
    /// the AAD device certificate landed; an event-driven wait of 10 minutes would
    /// have recovered the run.
    /// </para>
    /// </summary>
    public static class TenantIdAwaiter
    {
        private const int DefaultPeriodicReprobeMs = 30_000;
        private const int DefaultDebounceMs = 100;

        /// <summary>
        /// Production entry point. Returns the resolved TenantId, or <c>null</c> when
        /// timed out / cancelled. <paramref name="timeoutSeconds"/> &lt;= 0 short-circuits
        /// to <c>null</c> immediately (legacy fast-fail behaviour — caller should also
        /// gate on the same condition). The caller probes with its logger first, so this
        /// method only re-checks silently.
        /// </summary>
        public static string WaitForTenantId(int timeoutSeconds, AgentLogger logger, CancellationToken ct) =>
            WaitForTenantId(
                timeoutSeconds, logger, ct,
                resolve: TenantIdResolver.Resolve,
                signalFactory: () => new CompositeRegistryChangeSignal(logger));

        /// <summary>
        /// Test-visible body of <see cref="WaitForTenantId(int, AgentLogger, CancellationToken)"/>.
        /// <paramref name="resolve"/> stands for <see cref="TenantIdResolver.Resolve"/>: a null
        /// logger probes silently.
        /// </summary>
        internal static string WaitForTenantId(
            int timeoutSeconds,
            AgentLogger logger,
            CancellationToken ct,
            Func<AgentLogger, string> resolve,
            Func<IRegistryChangeSignal> signalFactory)
        {
            if (timeoutSeconds <= 0)
                return null;

            // The caller probed with its logger a moment ago (AgentBootstrap: the source line on a
            // hit, the full diagnostic block on a miss), so this re-check stays silent and the
            // block lands once. A hit re-probes with the logger for the source line.
            var initial = resolve(null);
            if (!string.IsNullOrWhiteSpace(initial))
            {
                resolve(logger);
                return initial;
            }

            logger?.Info($"TenantIdAwaiter: TenantId not yet resolvable — waiting up to {timeoutSeconds}s for registry signal.");

            using (var signal = signalFactory())
            {
                var hit = WaitForTenantIdCore(
                    probe: () => resolve(null), // silent during the wait
                    signal: signal,
                    timeoutSeconds: timeoutSeconds,
                    periodicReprobeMs: DefaultPeriodicReprobeMs,
                    debounceMs: DefaultDebounceMs,
                    logger: logger,
                    ct: ct);

                if (!string.IsNullOrWhiteSpace(hit))
                {
                    // Re-probe once with the real logger so the source-tag Info line
                    // (e.g. "resolved TenantId=… from cloud_domain_join_tenant_info")
                    // lands in the log alongside the awaiter's "resolved after Xs".
                    resolve(logger);
                }
                return hit;
            }
        }

        /// <summary>
        /// Test-visible orchestration core. Takes a silent probe delegate and an
        /// abstract signal so xUnit can drive the loop deterministically without
        /// touching the registry. Production wires <paramref name="probe"/> to
        /// <c>TenantIdResolver.Resolve(null)</c> and <paramref name="signal"/> to
        /// <see cref="CompositeRegistryChangeSignal"/>.
        /// </summary>
        internal static string WaitForTenantIdCore(
            Func<string> probe,
            IRegistryChangeSignal signal,
            int timeoutSeconds,
            int periodicReprobeMs,
            int debounceMs,
            AgentLogger logger,
            CancellationToken ct)
        {
            if (probe == null) throw new ArgumentNullException(nameof(probe));
            if (signal == null) throw new ArgumentNullException(nameof(signal));
            if (timeoutSeconds <= 0) return null;

            using (var signalEvent = new ManualResetEvent(initialState: false))
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                EventHandler handler = (_, __) => signalEvent.Set();
                signal.Changed += handler;
                try
                {
                    signal.Start();
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                    var sw = Stopwatch.StartNew();
                    var handles = new WaitHandle[] { signalEvent, timeoutCts.Token.WaitHandle };

                    while (!timeoutCts.IsCancellationRequested)
                    {
                        var idx = WaitHandle.WaitAny(handles, periodicReprobeMs);
                        if (idx == 1) break; // cancelled / timed out

                        bool signalFired = idx == 0;
                        signalEvent.Reset();

                        if (signalFired && debounceMs > 0)
                        {
                            // Brief settling so multi-value transactions land before re-probe.
                            // Returns true if cancelled during the debounce → exit promptly.
                            if (timeoutCts.Token.WaitHandle.WaitOne(debounceMs))
                                break;
                            signalEvent.Reset();
                        }

                        var hit = probe();
                        if (!string.IsNullOrWhiteSpace(hit))
                        {
                            logger?.Info(
                                $"TenantIdAwaiter: resolved after {sw.Elapsed.TotalSeconds:F0}s " +
                                $"({(signalFired ? "registry signal" : "periodic re-probe")}).");
                            return hit;
                        }

                        if (signalFired)
                            logger?.Trace("TenantIdAwaiter: registry change observed but TenantId still unresolvable.");
                    }

                    logger?.Warning(
                        $"TenantIdAwaiter: timeout after {sw.Elapsed.TotalSeconds:F0}s — " +
                        "TenantId still not available, agent will exit cleanly and retry on next trigger.");
                    return null;
                }
                finally
                {
                    signal.Changed -= handler;
                }
            }
        }
    }
}
