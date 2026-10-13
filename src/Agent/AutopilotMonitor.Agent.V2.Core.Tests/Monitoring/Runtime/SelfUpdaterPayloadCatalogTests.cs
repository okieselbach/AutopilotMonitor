using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Security;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Runtime
{
    /// <summary>
    /// Step 5b of the self-update: the staged payload must match its signed catalog before a
    /// single file is swapped in. The verifier is replaced through the static hook; each test
    /// restores it, as in <see cref="SelfUpdaterGracefulShutdownTests"/>.
    /// </summary>
    public sealed class SelfUpdaterPayloadCatalogTests : IDisposable
    {
        private const string StagingDir = @"C:\ProgramData\AutopilotMonitor\Agent-Update";

        private readonly Func<string, PayloadCatalogVerdict> _savedVerifier;
        private readonly List<string> _log = new List<string>();

        public SelfUpdaterPayloadCatalogTests()
        {
            _savedVerifier = SelfUpdater.VerifyPayloadCatalog;
        }

        public void Dispose() => SelfUpdater.VerifyPayloadCatalog = _savedVerifier;

        [Fact]
        public void Production_default_is_the_real_verifier()
        {
            Assert.Equal(typeof(PayloadCatalogVerifier), _savedVerifier.Method.DeclaringType);
            Assert.Equal(nameof(PayloadCatalogVerifier.Verify), _savedVerifier.Method.Name);
        }

        [Fact]
        public void Accepted_payload_lets_the_update_continue()
        {
            string? checkedDir = null;
            SelfUpdater.VerifyPayloadCatalog = dir => { checkedDir = dir; return PayloadCatalogVerdict.Accept(); };

            var ok = SelfUpdater.VerifyStagedPayload(StagingDir, _log.Add, out var reason);

            Assert.True(ok);
            Assert.Null(reason);
            Assert.Equal(StagingDir, checkedDir);
            Assert.Contains(_log, line => line.Contains("payload catalog verified"));
        }

        [Fact]
        public void Rejected_payload_stops_the_update_and_hands_over_the_reason()
        {
            SelfUpdater.VerifyPayloadCatalog = _ =>
                PayloadCatalogVerdict.Reject("payload differs from catalog (missing: none; extra: evil.dll; changed: none)");

            var ok = SelfUpdater.VerifyStagedPayload(StagingDir, _log.Add, out var reason);

            Assert.False(ok);
            Assert.Contains("extra: evil.dll", reason);
            Assert.Contains(_log, line => line.Contains("REJECTED"));
        }

        [Fact]
        public void A_throwing_verifier_is_a_rejection_not_a_pass()
        {
            SelfUpdater.VerifyPayloadCatalog = _ => throw new InvalidOperationException("runspace broke");

            var ok = SelfUpdater.VerifyStagedPayload(StagingDir, _log.Add, out var reason);

            Assert.False(ok);
            Assert.Contains("runspace broke", reason);
        }

        [Fact]
        public void The_catalog_check_runs_before_any_file_is_swapped()
        {
            // Source-level pin: CheckAndApplyUpdateAsync has no seam for the download, so the
            // ordering that makes the check meaningful is pinned on the source itself.
            var source = File.ReadAllText(Path.Combine(FindRepoRoot(),
                "src", "Agent", "AutopilotMonitor.Agent.V2.Core", "Monitoring", "Runtime", "SelfUpdater.cs"));

            var check = source.IndexOf("if (!VerifyStagedPayload(stagingDir, log, out var catalogRejection))", StringComparison.Ordinal);
            var swap = source.IndexOf("var swapOk = SwapFiles(agentDir, stagingDir, log);", StringComparison.Ordinal);

            Assert.True(check > 0, "step 5b not found in SelfUpdater.cs");
            Assert.True(swap > check, "SwapFiles must come after the catalog check");
            Assert.Contains("WriteSkipMarker(\"catalog_rejected\"", source);
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }
}
