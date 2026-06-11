using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// Parser tests for <see cref="RealmJoinInfo.ParseVersionAndChannel"/>. The RJ binary's
    /// FileVersion string resource carries a full SemVer where the prerelease tag IS the
    /// release channel: "4.21.6-canary+476277.d320cac0" (canary/beta) vs "4.21.6" (stable
    /// release — confirmed by the RJ developer that release builds carry no tag).
    /// </summary>
    public sealed class RealmJoinInfoParseTests
    {
        [Theory]
        [InlineData("4.21.6-canary+476277.d320cac0", "4.21.6", "canary")]
        [InlineData("4.21.6-beta+123456.abcdef00", "4.21.6", "beta")]
        [InlineData("4.21.6-canary", "4.21.6", "canary")]
        public void Tagged_version_yields_channel_from_prerelease_tag(string raw, string expectedVersion, string expectedChannel)
        {
            var info = RealmJoinInfo.ParseVersionAndChannel(raw);

            Assert.Equal(expectedVersion, info.ProductVersion);
            Assert.Equal(expectedChannel, info.ReleaseChannel);
        }

        [Theory]
        [InlineData("4.21.6")]
        [InlineData("4.21.6+476277.d320cac0")]
        [InlineData(" 4.21.6 ")]
        public void Untagged_version_yields_stable_release_channel(string raw)
        {
            var info = RealmJoinInfo.ParseVersionAndChannel(raw);

            Assert.Equal("4.21.6", info.ProductVersion);
            Assert.Equal(RealmJoinInfo.ReleaseChannelStable, info.ReleaseChannel);
        }

        [Fact]
        public void Build_metadata_is_dropped_from_both_fields()
        {
            var info = RealmJoinInfo.ParseVersionAndChannel("4.21.6-canary+476277.d320cac0");

            Assert.DoesNotContain("+", info.ProductVersion);
            Assert.DoesNotContain("476277", info.ReleaseChannel);
        }

        [Fact]
        public void Trailing_dash_without_tag_falls_back_to_stable_channel()
        {
            // Defensive: a malformed "4.21.6-" must not produce an empty channel string.
            var info = RealmJoinInfo.ParseVersionAndChannel("4.21.6-");

            Assert.Equal("4.21.6", info.ProductVersion);
            Assert.Equal(RealmJoinInfo.ReleaseChannelStable, info.ReleaseChannel);
        }

        [Fact]
        public void Empty_after_metadata_strip_yields_null_fields()
        {
            // Defensive: a string that is ONLY build metadata carries no version information.
            var info = RealmJoinInfo.ParseVersionAndChannel("+476277.d320cac0");

            Assert.Null(info.ProductVersion);
            Assert.Null(info.ReleaseChannel);
        }
    }
}
