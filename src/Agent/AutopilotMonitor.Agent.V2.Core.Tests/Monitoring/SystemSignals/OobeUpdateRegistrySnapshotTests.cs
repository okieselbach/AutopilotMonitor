#nullable enable
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// The OOBE update registry snapshot (D-310, S4/S5): one readable, bounded line per key, for
    /// agent.log and the <c>oobe_update_state</c> event. The registry read itself is a thin wrapper.
    /// </summary>
    public sealed class OobeUpdateRegistrySnapshotTests
    {
        private static KeyValuePair<string, object> Entry(string name, object value) => new KeyValuePair<string, object>(name, value);

        [Fact]
        public void Values_AreFormattedByType()
        {
            Assert.Equal("1", OobeUpdateRegistrySnapshot.FormatValue(1));
            Assert.Equal("5000000000", OobeUpdateRegistrySnapshot.FormatValue(5000000000L));
            Assert.Equal("Succeeded", OobeUpdateRegistrySnapshot.FormatValue("Succeeded"));
            Assert.Equal("a|b", OobeUpdateRegistrySnapshot.FormatValue(new[] { "a", "b" }));
            Assert.Equal("(binary 3 bytes)", OobeUpdateRegistrySnapshot.FormatValue(new byte[] { 1, 2, 3 }));
            Assert.Equal("(null)", OobeUpdateRegistrySnapshot.FormatValue(null!));
        }

        [Fact]
        public void LongValues_AreTruncated()
        {
            var formatted = OobeUpdateRegistrySnapshot.FormatValue(new string('x', 500));
            Assert.Equal(OobeUpdateRegistrySnapshot.MaxValueLength + 1, formatted.Length);
            Assert.EndsWith("…", formatted);
        }

        [Fact]
        public void Entries_AreOrderedByName()
        {
            var line = OobeUpdateRegistrySnapshot.FormatEntries(new[]
            {
                Entry("ExpeditedUpdateStatus", 2),
                Entry("EnableExpeditedUpdate", 1),
                Entry("", "default"),
            }, oobeOnly: false);

            Assert.Equal("(default)=default, EnableExpeditedUpdate=1, ExpeditedUpdateStatus=2", line);
        }

        [Fact]
        public void PolicyManagerSystemKey_KeepsOnlyOobeValues()
        {
            // That key holds every System policy of the device; only the OOBE ones matter here.
            var line = OobeUpdateRegistrySnapshot.FormatEntries(new[]
            {
                Entry("AllowTelemetry", 1),
                Entry("AllowOOBEUpdates", 1),
                Entry("AllowOOBEUpdates_ProviderSet", 1),
            }, oobeOnly: true);

            Assert.Equal("AllowOOBEUpdates=1, AllowOOBEUpdates_ProviderSet=1", line);
            Assert.Equal("(no OOBE values)", OobeUpdateRegistrySnapshot.FormatEntries(new[] { Entry("AllowTelemetry", 1) }, oobeOnly: true));
            Assert.Equal("(no values)", OobeUpdateRegistrySnapshot.FormatEntries(new KeyValuePair<string, object>[0], oobeOnly: false));
        }

        [Fact]
        public void ManyEntries_AreCapped_AndSaySo()
        {
            var entries = Enumerable.Range(0, OobeUpdateRegistrySnapshot.MaxEntries + 5)
                .Select(i => Entry($"Value{i:000}", i));

            var line = OobeUpdateRegistrySnapshot.FormatEntries(entries, oobeOnly: false);

            Assert.EndsWith("… (+5 more)", line);
        }

        [Fact]
        public void Describe_CountsOnlyOobeValues_OfThePolicyManagerSystemKey()
        {
            var policy = OobeUpdateRegistrySnapshot.Describe("policyManagerSystem", OobeUpdateRegistrySnapshot.PolicyManagerSystemKey,
                new[] { Entry("AllowTelemetry", 1) }, new string[0]);
            Assert.True(policy.Exists);
            Assert.False(policy.HasValues);

            var oobe = OobeUpdateRegistrySnapshot.Describe("policyManagerSystem", OobeUpdateRegistrySnapshot.PolicyManagerSystemKey,
                new[] { Entry("AllowTelemetry", 1), Entry("AllowOOBEUpdates", 1) }, new string[0]);
            Assert.True(oobe.HasValues);
            Assert.Equal("AllowOOBEUpdates=1", oobe.Line);
        }

        [Fact]
        public void Describe_SubkeysAlone_AreNoValues()
        {
            var state = OobeUpdateRegistrySnapshot.Describe("ndup", @"SOFTWARE\Microsoft\Windows\CurrentVersion\NDUP",
                new KeyValuePair<string, object>[0], new[] { "Updates" });

            Assert.False(state.HasValues);
            Assert.Equal("(no values) | subkeys: Updates", state.Line);
        }

        [Fact]
        public void Keys_HaveDistinctAliases()
        {
            // The aliases are the data keys of oobe_update_state.
            Assert.Equal(OobeUpdateRegistrySnapshot.Keys.Length,
                OobeUpdateRegistrySnapshot.Keys.Select(k => k.Alias).Distinct().Count());
        }

        [Fact]
        public void Read_ReturnsEveryKey_FromTheRealRegistry()
        {
            var states = OobeUpdateRegistrySnapshot.Read();

            Assert.Equal(OobeUpdateRegistrySnapshot.Keys.Select(k => k.Path), states.Select(s => s.Path));
            Assert.All(states.Where(s => !s.Exists), s => Assert.False(s.HasValues));
        }

        [Fact]
        public void Log_ToleratesMissingLoggerOrStates()
        {
            using var tmp = new TempDirectory();
            OobeUpdateRegistrySnapshot.Log(new AgentLogger(tmp.Path, AgentLogLevel.Info), "test", OobeUpdateRegistrySnapshot.Read());
            OobeUpdateRegistrySnapshot.Log(null!, "test", OobeUpdateRegistrySnapshot.Read());
            OobeUpdateRegistrySnapshot.Log(new AgentLogger(tmp.Path, AgentLogLevel.Info), "test", null!);
        }
    }
}
