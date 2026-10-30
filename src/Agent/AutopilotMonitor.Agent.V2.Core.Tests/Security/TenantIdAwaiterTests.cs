using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

// xUnit1031 (no blocking Task.Wait in tests) intentionally suppressed — the API
// under test is synchronous; we run it via Task.Run on a worker thread so the
// xUnit thread can manipulate scripted state and trigger the fake signal,
// then Wait verifies completion. Async would require turning a sync API into a
// Task<string>-returning one purely for test ergonomics, which is the wrong
// trade-off.
#pragma warning disable xUnit1031

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    [CollectionDefinition(nameof(TenantIdAwaiterTestCollection), DisableParallelization = true)]
    public sealed class TenantIdAwaiterTestCollection { }

    /// <summary>
    /// Drives <see cref="TenantIdAwaiter.WaitForTenantIdCore"/> through every interesting
    /// path with a fake <see cref="IRegistryChangeSignal"/> + scripted probe so the
    /// orchestration logic is verified without touching the registry. The public
    /// <c>WaitForTenantId</c> wrapper is covered through its internal overload with a scripted
    /// resolver (which probes log); the HKCU integration test in
    /// <c>RegistryWatcherIntegrationTests</c> proves the CompositeRegistryChangeSignal /
    /// RegNotifyChangeKeyValue plumbing separately.
    /// <para>
    /// Single-threaded collection: these tests use <c>Thread.Sleep</c> as a synchronisation
    /// primitive (waiting for the awaiter to enter its WaitAny loop before triggering the
    /// fake signal). Under the default parallel pool the sleep is unreliable on a busy
    /// box, so the class opts out of parallel execution.
    /// </para>
    /// </summary>
    [Collection(nameof(TenantIdAwaiterTestCollection))]
    public sealed class TenantIdAwaiterTests
    {
        private sealed class FakeSignal : IRegistryChangeSignal
        {
            public event EventHandler? Changed;
            public bool StartCalled { get; private set; }
            public bool DisposeCalled { get; private set; }

            public void Start() => StartCalled = true;
            public void Trigger() => Changed?.Invoke(this, EventArgs.Empty);
            public void Dispose() => DisposeCalled = true;
        }

        [Fact]
        public void Timeout_with_no_signals_returns_null_after_about_one_second()
        {
            using var signal = new FakeSignal();
            var sw = Stopwatch.StartNew();

            var result = TenantIdAwaiter.WaitForTenantIdCore(
                probe: () => null!,
                signal: signal,
                timeoutSeconds: 1,
                periodicReprobeMs: 5_000, // larger than timeout, so this never fires
                debounceMs: 0,
                logger: null!,
                ct: CancellationToken.None);

            sw.Stop();
            Assert.Null(result);
            Assert.True(sw.Elapsed.TotalSeconds >= 0.9 && sw.Elapsed.TotalSeconds < 2.5,
                $"expected ~1s, was {sw.Elapsed.TotalSeconds:F2}s");
            Assert.True(signal.StartCalled);
        }

        [Fact]
        public void Signal_fires_and_probe_hits_returns_value()
        {
            using var signal = new FakeSignal();
            string? scriptedReturn = null;

            var task = Task.Run(() => TenantIdAwaiter.WaitForTenantIdCore(
                probe: () => scriptedReturn!,
                signal: signal,
                timeoutSeconds: 10,
                periodicReprobeMs: 60_000, // never via periodic
                debounceMs: 5,
                logger: null!,
                ct: CancellationToken.None));

            // Let the awaiter enter its wait loop.
            Thread.Sleep(100);

            // Flip probe to "hit" then signal.
            scriptedReturn = "tenant-aaa-bbb";
            signal.Trigger();

            Assert.True(task.Wait(TimeSpan.FromSeconds(3)),
                "WaitForTenantIdCore did not return within 3s after signal");
            Assert.Equal("tenant-aaa-bbb", task.Result);
        }

        [Fact]
        public void Multiple_signals_with_late_probe_hit_returns_value()
        {
            using var signal = new FakeSignal();
            int probeCount = 0;
            string? scriptedReturn = null;

            var task = Task.Run(() => TenantIdAwaiter.WaitForTenantIdCore(
                probe: () =>
                {
                    Interlocked.Increment(ref probeCount);
                    return scriptedReturn!;
                },
                signal: signal,
                timeoutSeconds: 10,
                periodicReprobeMs: 60_000,
                debounceMs: 5,
                logger: null!,
                ct: CancellationToken.None));

            Thread.Sleep(80);
            // Two signal fires that yield no hit
            signal.Trigger(); Thread.Sleep(40);
            signal.Trigger(); Thread.Sleep(40);
            // Now flip the probe and fire again
            scriptedReturn = "tenant-late";
            signal.Trigger();

            Assert.True(task.Wait(TimeSpan.FromSeconds(3)));
            Assert.Equal("tenant-late", task.Result);
            Assert.True(probeCount >= 3, $"expected ≥3 probes (3 signals), saw {probeCount}");
        }

        [Fact]
        public void Cancellation_returns_null_promptly()
        {
            using var signal = new FakeSignal();
            using var cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();

            var task = Task.Run(() => TenantIdAwaiter.WaitForTenantIdCore(
                probe: () => null!,
                signal: signal,
                timeoutSeconds: 60, // would otherwise wait a minute
                periodicReprobeMs: 60_000,
                debounceMs: 0,
                logger: null!,
                ct: cts.Token));

            Thread.Sleep(100);
            cts.Cancel();

            Assert.True(task.Wait(TimeSpan.FromSeconds(3)),
                "expected prompt return on cancellation");
            Assert.Null(task.Result);
            sw.Stop();
            Assert.True(sw.Elapsed.TotalSeconds < 2.0,
                $"cancellation took {sw.Elapsed.TotalSeconds:F2}s — expected <2s");
        }

        [Fact]
        public void Periodic_reprobe_picks_up_change_when_no_signal_arrives()
        {
            using var signal = new FakeSignal();
            string? scriptedReturn = null;

            var task = Task.Run(() => TenantIdAwaiter.WaitForTenantIdCore(
                probe: () => scriptedReturn!,
                signal: signal,
                timeoutSeconds: 10,
                periodicReprobeMs: 100, // re-probe every 100 ms
                debounceMs: 0,
                logger: null!,
                ct: CancellationToken.None));

            Thread.Sleep(80);
            scriptedReturn = "tenant-via-periodic";
            // No signal — only the periodic timer should pick this up.

            Assert.True(task.Wait(TimeSpan.FromSeconds(3)),
                "periodic re-probe did not catch the registry change");
            Assert.Equal("tenant-via-periodic", task.Result);
        }

        [Fact]
        public void Empty_or_whitespace_probe_result_is_treated_as_miss()
        {
            using var signal = new FakeSignal();
            var probeReturns = new[] { "", "   ", null!, "tenant-ok" };
            int callIdx = 0;

            var task = Task.Run(() => TenantIdAwaiter.WaitForTenantIdCore(
                probe: () => probeReturns[Math.Min(callIdx++, probeReturns.Length - 1)],
                signal: signal,
                timeoutSeconds: 10,
                periodicReprobeMs: 50,
                debounceMs: 0,
                logger: null!,
                ct: CancellationToken.None));

            Assert.True(task.Wait(TimeSpan.FromSeconds(3)));
            Assert.Equal("tenant-ok", task.Result);
            // empty + whitespace + null + "tenant-ok"  =>  callIdx >= 4 by then
            Assert.True(callIdx >= 4, $"expected ≥4 probe calls, saw {callIdx}");
        }

        [Fact]
        public void Zero_or_negative_timeout_short_circuits_to_null()
        {
            using var signal = new FakeSignal();
            var sw = Stopwatch.StartNew();

            var resultZero = TenantIdAwaiter.WaitForTenantIdCore(
                probe: () => "tenant-would-have-hit",
                signal: signal,
                timeoutSeconds: 0,
                periodicReprobeMs: 100,
                debounceMs: 0,
                logger: null!,
                ct: CancellationToken.None);

            var resultNeg = TenantIdAwaiter.WaitForTenantIdCore(
                probe: () => "tenant-would-have-hit",
                signal: signal,
                timeoutSeconds: -5,
                periodicReprobeMs: 100,
                debounceMs: 0,
                logger: null!,
                ct: CancellationToken.None);

            sw.Stop();
            Assert.Null(resultZero);
            Assert.Null(resultNeg);
            // Should be near-instantaneous (no Start, no waits).
            Assert.True(sw.Elapsed.TotalMilliseconds < 200,
                $"short-circuit took {sw.Elapsed.TotalMilliseconds:F0}ms");
            Assert.False(signal.StartCalled, "signal must not be started for zero-timeout");
        }

        [Fact]
        public void Null_arguments_throw_ArgumentNullException()
        {
            using var signal = new FakeSignal();
            Assert.Throws<ArgumentNullException>(() =>
                TenantIdAwaiter.WaitForTenantIdCore(
                    probe: null!,
                    signal: signal,
                    timeoutSeconds: 1,
                    periodicReprobeMs: 100,
                    debounceMs: 0,
                    logger: null!,
                    ct: CancellationToken.None));

            Assert.Throws<ArgumentNullException>(() =>
                TenantIdAwaiter.WaitForTenantIdCore(
                    probe: () => null!,
                    signal: null!,
                    timeoutSeconds: 1,
                    periodicReprobeMs: 100,
                    debounceMs: 0,
                    logger: null!,
                    ct: CancellationToken.None));
        }

        [Fact]
        public void Initial_miss_never_probes_with_the_logger()
        {
            // AgentBootstrap probes with its logger before it calls the awaiter; the resolver's
            // miss block must not land a second time from the awaiter's own first check.
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            int loggedProbes = 0, silentProbes = 0;

            var result = TenantIdAwaiter.WaitForTenantId(
                timeoutSeconds: 1,
                logger: logger,
                ct: CancellationToken.None,
                resolve: l => { if (l != null) loggedProbes++; else silentProbes++; return null!; },
                signalFactory: () => new FakeSignal());

            Assert.Null(result);
            Assert.Equal(0, loggedProbes);
            Assert.True(silentProbes >= 1, $"expected a silent first check, saw {silentProbes}");
        }

        [Fact]
        public void Initial_hit_probes_once_more_with_the_logger_for_the_source_line()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var loggedProbes = 0;

            var result = TenantIdAwaiter.WaitForTenantId(
                timeoutSeconds: 600,
                logger: logger,
                ct: CancellationToken.None,
                resolve: l => { if (l != null) loggedProbes++; return "tenant-1"; },
                signalFactory: () => throw new InvalidOperationException("no wait expected on a hit"));

            Assert.Equal("tenant-1", result);
            Assert.Equal(1, loggedProbes);
        }
    }
}
