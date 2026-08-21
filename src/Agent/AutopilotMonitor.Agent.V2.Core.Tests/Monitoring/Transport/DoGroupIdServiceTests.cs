using System;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Transport
{
    public class DoGroupIdServiceTests
    {
        // -------------------------------------------------------------------
        // ComputeGroupId — RealmJoin byte-layout compatibility
        // -------------------------------------------------------------------

        [Fact]
        public void ComputeGroupId_MatchesHandComputedVector()
        {
            // Layout: {101,48,6} + {0,0,0} + {ip[1],0,ip[2],ip[3]} + mac — through new Guid(byte[])
            // the first three groups print little-endian, the last eight bytes verbatim.
            var guid = DoGroupIdService.ComputeGroupId(
                new byte[] { 192, 168, 178, 1 },
                new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 });

            Assert.Equal("00063065-0000-00a8-b201-001122334455", guid.ToString());
        }

        [Theory]
        [InlineData(new byte[] { 10, 0, 0, 1 }, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF })]
        [InlineData(new byte[] { 192, 168, 1, 254 }, new byte[] { 0x00, 0x50, 0x56, 0x01, 0x02, 0x03 })]
        [InlineData(new byte[] { 172, 16, 254, 3 }, new byte[] { 0xF0, 0x9F, 0xC2, 0x00, 0x00, 0x01 })]
        public void ComputeGroupId_MatchesRealmJoinReferenceImplementation(byte[] ip, byte[] mac)
        {
            Assert.Equal(RealmJoinReferenceGuid(ip, mac), DoGroupIdService.ComputeGroupId(ip, mac));
        }

        /// <summary>Verbatim port of RealmJoin's ConvertToGuid — the compatibility contract.</summary>
        private static Guid RealmJoinReferenceGuid(byte[] ipBytes, byte[] address)
        {
            byte[] prefix = { 101, 48, 6 };
            var stuff = new byte[4];
            stuff[0] = ipBytes[1];
            stuff[2] = ipBytes[2];
            stuff[3] = ipBytes[3];

            return new Guid(prefix
                .Concat(new byte[16 - prefix.Length - stuff.Length - address.Length])
                .Concat(stuff)
                .Concat(address)
                .ToArray());
        }

        [Fact]
        public void ComputeGroupId_RejectsNonIpv4AndBadMacLengths()
        {
            var ip4 = new byte[] { 10, 0, 0, 1 };
            var mac = new byte[] { 1, 2, 3, 4, 5, 6 };

            Assert.Throws<ArgumentException>(() => DoGroupIdService.ComputeGroupId(new byte[16], mac));
            Assert.Throws<ArgumentException>(() => DoGroupIdService.ComputeGroupId(null!, mac));
            Assert.Throws<ArgumentException>(() => DoGroupIdService.ComputeGroupId(ip4, new byte[8]));
            Assert.Throws<ArgumentException>(() => DoGroupIdService.ComputeGroupId(ip4, null!));
        }

        [Fact]
        public void ComputeGroupId_RegistryFormat_IsBracelessLowercase()
        {
            // RealmJoin writes SetValue(name, Guid) which stringifies via Guid.ToString() ("d"
            // format) — our written value must be the identical string form.
            var text = DoGroupIdService.ComputeGroupId(
                new byte[] { 10, 1, 2, 3 }, new byte[] { 0xAB, 0xCD, 0xEF, 0x01, 0x02, 0x03 }).ToString();

            Assert.DoesNotContain("{", text);
            Assert.Equal(text.ToLowerInvariant(), text);
            Assert.Equal(36, text.Length);
        }

        // -------------------------------------------------------------------
        // DecideAction — ownership matrix
        // -------------------------------------------------------------------

        private const string Computed = "00063065-0000-00a8-b201-001122334455";
        private const string Other = "11111111-2222-3333-4444-555555555555";

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void DecideAction_NoExistingValue_Writes(string? existing)
        {
            Assert.Equal(DoGroupIdAction.Write, DoGroupIdService.DecideAction(existing, Computed, null));
        }

        [Fact]
        public void DecideAction_ExistingEqualsComputed_IsNoOp()
        {
            Assert.Equal(DoGroupIdAction.NoOp, DoGroupIdService.DecideAction(Computed, Computed, null));
        }

        [Fact]
        public void DecideAction_ExistingEqualsComputed_ToleratesBracesAndCase()
        {
            // A value written by another tool in "{...}"/uppercase form is still semantically ours.
            var braced = "{" + Computed.ToUpperInvariant() + "}";
            Assert.Equal(DoGroupIdAction.NoOp, DoGroupIdService.DecideAction(braced, Computed, null));
        }

        [Fact]
        public void DecideAction_ExistingIsOurStaleValue_WritesUpdate()
        {
            // Gateway changed: the registry still holds the GUID we wrote for the old network.
            Assert.Equal(DoGroupIdAction.Write, DoGroupIdService.DecideAction(Other, Computed, Other));
        }

        [Fact]
        public void DecideAction_ForeignValue_Skips()
        {
            Assert.Equal(DoGroupIdAction.SkipForeign, DoGroupIdService.DecideAction(Other, Computed, null));
            Assert.Equal(DoGroupIdAction.SkipForeign, DoGroupIdService.DecideAction(Other, Computed, Computed));
        }

        [Fact]
        public void DecideAction_NonGuidGarbageExisting_SkipsAsForeign()
        {
            // Never overwrite a value we can't even parse — conservative by design.
            Assert.Equal(DoGroupIdAction.SkipForeign, DoGroupIdService.DecideAction("not-a-guid", Computed, null));
        }
    }
}
