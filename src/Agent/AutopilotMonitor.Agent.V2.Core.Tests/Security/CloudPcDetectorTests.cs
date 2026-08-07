using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Security;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    /// <summary>
    /// Pins the marker-AND contract of <see cref="CloudPcDetector.ResolveIsCloudPc"/>: BOTH
    /// the Windows365 registry key and the CloudManagedDesktopExtension service key must be
    /// present — each alone has plausible look-alikes (W365-Boot physical clients carry
    /// Windows365 policy state; the MCMD agent family served other managed-desktop
    /// offerings). Mirrors the bootstrap script's field-verified <c>Test-IsCloudPc</c>.
    /// </summary>
    public sealed class CloudPcDetectorTests
    {
        private static Func<string, bool> Probe(params string[] existingKeys)
        {
            var set = new HashSet<string>(existingKeys, StringComparer.OrdinalIgnoreCase);
            return path => set.Contains(path);
        }

        [Fact]
        public void Both_markers_present_resolves_true()
        {
            Assert.True(CloudPcDetector.ResolveIsCloudPc(Probe(
                CloudPcDetector.Windows365MarkerKey,
                CloudPcDetector.CmdeServiceKey)));
        }

        [Fact]
        public void Windows365_key_alone_resolves_false()
        {
            // W365-Boot physical client shape: policy state without the on-device MCMD agent.
            Assert.False(CloudPcDetector.ResolveIsCloudPc(Probe(CloudPcDetector.Windows365MarkerKey)));
        }

        [Fact]
        public void Cmde_service_alone_resolves_false()
        {
            // MCMD agent family also served other managed-desktop offerings.
            Assert.False(CloudPcDetector.ResolveIsCloudPc(Probe(CloudPcDetector.CmdeServiceKey)));
        }

        [Fact]
        public void No_markers_resolves_false()
        {
            Assert.False(CloudPcDetector.ResolveIsCloudPc(Probe()));
        }

        [Fact]
        public void Null_probe_resolves_false()
        {
            Assert.False(CloudPcDetector.ResolveIsCloudPc(null));
        }

        [Fact]
        public void Throwing_probe_degrades_to_false()
        {
            // SKIP-safe contract: a broken registry read must never classify, let alone throw.
            Assert.False(CloudPcDetector.ResolveIsCloudPc(_ => throw new UnauthorizedAccessException("registry denied")));
        }

        [Fact]
        public void DetectIsCloudPc_never_throws_on_build_host()
        {
            // Real-registry shell: exercises the exception-swallowing wrapper end to end.
            // A build host is not a Cloud PC, but the contract under test is "no throw",
            // not the detected value.
            var ex = Record.Exception(() => CloudPcDetector.DetectIsCloudPc());
            Assert.Null(ex);
        }
    }
}
