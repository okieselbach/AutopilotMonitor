using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// Verifies the <see cref="EspFirstSyncSnapshot"/> decoded-flag accessors against the
    /// canonical <c>BlockInStatusPage</c> bitmask documented in Microsoft's
    /// <c>Get-AutopilotDiagnostics.ps1</c>: bit 1 = AllowReset, bit 2 = AllowTryAgain,
    /// bit 4 = AllowContinueAnyway.
    /// </summary>
    public sealed class EspFirstSyncSnapshotTests
    {
        [Theory]
        [InlineData(0, false, false, false)]
        [InlineData(1, true, false, false)]
        [InlineData(2, false, true, false)]
        [InlineData(3, true, true, false)]
        [InlineData(4, false, false, true)]
        [InlineData(5, true, false, true)]
        [InlineData(6, false, true, true)]
        [InlineData(7, true, true, true)]
        public void DecodedFlags_MatchBitmaskSemantics(int raw, bool reset, bool tryAgain, bool continueAnyway)
        {
            var snapshot = new EspFirstSyncSnapshot(
                skipUser: null,
                skipDevice: null,
                blockInStatusPage: raw,
                syncFailureTimeoutMinutes: null);

            Assert.Equal(reset, snapshot.AllowReset);
            Assert.Equal(tryAgain, snapshot.AllowTryAgain);
            Assert.Equal(continueAnyway, snapshot.AllowContinueAnyway);
        }

        [Fact]
        public void DecodedFlags_NullWhenBitmaskMissing()
        {
            var snapshot = new EspFirstSyncSnapshot(
                skipUser: true,
                skipDevice: false,
                blockInStatusPage: null,
                syncFailureTimeoutMinutes: 60);

            Assert.Null(snapshot.AllowReset);
            Assert.Null(snapshot.AllowTryAgain);
            Assert.Null(snapshot.AllowContinueAnyway);
        }

        [Fact]
        public void Empty_HasAllFieldsNull()
        {
            var snapshot = EspFirstSyncSnapshot.Empty;

            Assert.Null(snapshot.SkipUser);
            Assert.Null(snapshot.SkipDevice);
            Assert.Null(snapshot.BlockInStatusPage);
            Assert.Null(snapshot.SyncFailureTimeoutMinutes);
            Assert.Null(snapshot.AllowReset);
            Assert.Null(snapshot.AllowTryAgain);
            Assert.Null(snapshot.AllowContinueAnyway);
        }
    }
}
