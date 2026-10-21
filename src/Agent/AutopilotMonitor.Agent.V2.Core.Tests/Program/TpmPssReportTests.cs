using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Runtime;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// The TPM-PSS report after a terminal SecureChannelFailure: it leaves before the run exits,
    /// carries the handshake code, the Schannel events and the TPM firmware, and a slow or failing
    /// context read never holds it back.
    /// </summary>
    public sealed class TpmPssReportTests
    {
        private static AgentLogger NewLogger(TempDirectory tmp)
            => new AgentLogger(Path.Combine(tmp.Path, "logs"), AgentLogLevel.Debug);

        private static TpmPssProbeResult Probe(bool pssWorks = false, string provider = "Microsoft Platform Crypto Provider") =>
            new TpmPssProbeResult(
                pkcs1Sha256Works: true, pssSha256Works: pssWorks, pssSha384Works: pssWorks,
                providerName: provider, keySizeBits: 2048,
                pssSha256Error: pssWorks ? null : "salt", pssSha384Error: pssWorks ? null : "not supported",
                pssSha256ErrorCode: pssWorks ? (int?)null : unchecked((int)0x80290423),
                pssSha384ErrorCode: pssWorks ? (int?)null : unchecked((int)0x80090029));

        private static Exception TlsFailure() =>
            new WebException("Could not create SSL/TLS secure channel.",
                new Win32Exception(unchecked((int)0x80090326)), WebExceptionStatus.SecureChannelFailure, null);

        private static Func<Task<(string TpmVersion, string SchannelEvents)>> Context(string tpm, string schannel) =>
            () => Task.FromResult((tpm, schannel));

        [Fact]
        public async Task Broken_tpm_report_completes_only_after_the_send()
        {
            using var tmp = new TempDirectory();
            var sendGate = new TaskCompletionSource<bool>();
            var reporter = new FakeDistressReporter { Send = () => sendGate.Task };

            var report = BackendSessionRegistration.ReportProbeResultAsync(
                Probe(), TlsFailure(), reporter, NewLogger(tmp), Context("IFX 7.63.3353.0", "36888:40/1205"));

            var call = Assert.Single(reporter.Calls);
            Assert.False(report.IsCompleted);
            Assert.Equal(DistressErrorType.TpmPssUnsupported, call.ErrorType);
            Assert.Equal("IFX 7.63.3353.0", call.TpmVersion);
            Assert.Equal(
                "pkcs1=ok pss256=fail(0x80290423) pss384=fail(0x80090029) tls=0x80090326 sch=36888:40/1205 prov=PCP key=2048",
                call.Message);

            sendGate.SetResult(true);
            await report;
        }

        [Fact]
        public async Task Failing_context_read_still_sends()
        {
            using var tmp = new TempDirectory();
            var reporter = new FakeDistressReporter();

            await BackendSessionRegistration.ReportProbeResultAsync(
                Probe(), TlsFailure(), reporter, NewLogger(tmp),
                () => throw new InvalidOperationException("WMI gone"));

            var call = Assert.Single(reporter.Calls);
            Assert.Null(call.TpmVersion);
            Assert.Contains("sch=unreadable", call.Message);
        }

        [Fact]
        public async Task Working_pss_sends_nothing()
        {
            using var tmp = new TempDirectory();
            var reporter = new FakeDistressReporter();

            await BackendSessionRegistration.ReportProbeResultAsync(
                Probe(pssWorks: true), TlsFailure(), reporter, NewLogger(tmp), Context("IFX 7.85", null!));

            Assert.Empty(reporter.Calls);
        }

        [Fact]
        public async Task Software_key_with_broken_pss_sends_nothing()
        {
            using var tmp = new TempDirectory();
            var reporter = new FakeDistressReporter();

            await BackendSessionRegistration.ReportProbeResultAsync(
                Probe(provider: "Microsoft Software Key Storage Provider"), TlsFailure(), reporter, NewLogger(tmp), Context(null!, null!));

            Assert.Empty(reporter.Calls);
        }

        [Fact]
        public async Task Context_reads_return_what_finished_within_the_bound()
        {
            using var tmp = new TempDirectory();
            using var slowRelease = new ManualResetEventSlim(false);
            using var fastReturned = new ManualResetEventSlim(false);
            try
            {
                // The bound ends only after the fast read returned (plus a grace for its task to
                // complete) — independent of how busy the thread pool is.
                var bound = Task.Run(async () =>
                {
                    fastReturned.Wait(TimeSpan.FromSeconds(30));
                    await Task.Delay(500);
                });

                var (tpm, schannel) = await BackendSessionRegistration.ReadDiagnosticContextAsync(
                    () => { fastReturned.Set(); return "IFX 7.63.3353.0"; },
                    () => { slowRelease.Wait(TimeSpan.FromSeconds(30)); return "late"; },
                    bound,
                    NewLogger(tmp));

                Assert.Equal("IFX 7.63.3353.0", tpm);
                Assert.Equal(BackendSessionRegistration.SchannelReadTimeout, schannel);
            }
            finally
            {
                slowRelease.Set();
            }
        }

        [Fact]
        public async Task Context_read_that_throws_counts_as_missing()
        {
            using var tmp = new TempDirectory();

            var (tpm, schannel) = await BackendSessionRegistration.ReadDiagnosticContextAsync(
                () => throw new InvalidOperationException("no WMI"),
                () => "36887:116",
                Task.Delay(TimeSpan.FromSeconds(30)),
                NewLogger(tmp));

            Assert.Null(tpm);
            Assert.Equal("36887:116", schannel);
        }

        [Fact]
        public async Task Schannel_read_that_throws_is_reported_as_unreadable_not_as_empty()
        {
            using var tmp = new TempDirectory();

            var (_, schannel) = await BackendSessionRegistration.ReadDiagnosticContextAsync(
                () => "IFX 7.63.3353.0",
                () => throw new UnauthorizedAccessException("System log"),
                Task.Delay(TimeSpan.FromSeconds(30)),
                NewLogger(tmp));

            Assert.Equal(SchannelEventReader.Unreadable, schannel);
        }

        [Fact]
        public async Task Report_carries_the_context_the_run_started_in()
        {
            using var tmp = new TempDirectory();
            var reporter = new FakeDistressReporter();
            var startContext = new DistressDeviceContext("in_progress", "device_setup",
                new DateTime(2026, 10, 21, 6, 40, 0, DateTimeKind.Utc));

            await BackendSessionRegistration.ReportProbeResultAsync(
                Probe(), TlsFailure(), reporter, NewLogger(tmp), Context("IFX 7.63.3353.0", "none"), startContext);

            var call = Assert.Single(reporter.Calls);
            Assert.Same(startContext, call.Context);
            Assert.Contains("sch=none", call.Message);
        }
    }
}
