#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry
{
    /// <summary>
    /// DoThrottlePolicyReader — pins the two-store semantics (GPO and Intune/MDM land in
    /// DIFFERENT registry paths and both must be read), the "0 = dynamic/no limit → not
    /// configured" cap rule vs. "DownloadMode 0 is a real value", and fail-soft conversion.
    /// </summary>
    public sealed class DoThrottlePolicyReaderTests
    {
        private static Func<string, string, object?> Fake(Dictionary<(string, string), object?> values)
            => (key, name) => values.TryGetValue((key, name), out var v) ? v : null;

        [Fact]
        public void Gpo_and_Mdm_are_read_from_their_own_stores()
        {
            var snapshot = DoThrottlePolicyReader.ReadCore(Fake(new Dictionary<(string, string), object?>
            {
                [(DoThrottlePolicyReader.GpoKeyPath, "DOMaxForegroundDownloadBandwidth")] = 2500,   // KB/s via GPO
                [(DoThrottlePolicyReader.MdmKeyPath, "DOPercentageMaxBackgroundBandwidth")] = 40,   // % via Intune
            }));

            Assert.Equal(2500, snapshot.GpoMaxForegroundKBps);
            Assert.Null(snapshot.MdmMaxForegroundKBps);   // MDM store has no absolute cap
            Assert.Equal(40, snapshot.MdmPctMaxBackground);
            Assert.Null(snapshot.GpoPctMaxBackground);    // GPO store has no percentage cap
            Assert.True(snapshot.ThrottleConfigured);
            Assert.Equal("gpo+mdm", snapshot.ThrottleSources);
        }

        [Fact]
        public void Zero_caps_mean_dynamic_and_are_not_configured()
        {
            var snapshot = DoThrottlePolicyReader.ReadCore(Fake(new Dictionary<(string, string), object?>
            {
                [(DoThrottlePolicyReader.GpoKeyPath, "DOMaxForegroundDownloadBandwidth")] = 0,
                [(DoThrottlePolicyReader.MdmKeyPath, "DOPercentageMaxForegroundBandwidth")] = 0,
            }));

            Assert.Null(snapshot.GpoMaxForegroundKBps);
            Assert.Null(snapshot.MdmPctMaxForeground);
            Assert.False(snapshot.ThrottleConfigured);
            Assert.Null(snapshot.ThrottleSources);
        }

        [Fact]
        public void DownloadMode_zero_is_a_real_value_but_not_a_throttle()
        {
            var snapshot = DoThrottlePolicyReader.ReadCore(Fake(new Dictionary<(string, string), object?>
            {
                [(DoThrottlePolicyReader.MdmKeyPath, "DODownloadMode")] = 0, // HTTP only — real setting
            }));

            Assert.Equal(0, snapshot.MdmDownloadMode);
            Assert.False(snapshot.ThrottleConfigured); // DownloadMode alone is no bandwidth cap
            Assert.Null(snapshot.ThrottleSources);
        }

        [Fact]
        public void String_stored_numbers_are_tolerated()
        {
            // PolicyManager occasionally materializes int policies as strings.
            var snapshot = DoThrottlePolicyReader.ReadCore(Fake(new Dictionary<(string, string), object?>
            {
                [(DoThrottlePolicyReader.MdmKeyPath, "DOMaxBackgroundDownloadBandwidth")] = "1200",
            }));

            Assert.Equal(1200, snapshot.MdmMaxBackgroundKBps);
            Assert.Equal("mdm", snapshot.ThrottleSources);
        }

        [Fact]
        public void Garbage_values_and_thrown_getters_are_fail_soft()
        {
            var garbage = DoThrottlePolicyReader.ReadCore(Fake(new Dictionary<(string, string), object?>
            {
                [(DoThrottlePolicyReader.GpoKeyPath, "DOMaxForegroundDownloadBandwidth")] = "not-a-number",
            }));
            Assert.Null(garbage.GpoMaxForegroundKBps);
            Assert.False(garbage.ThrottleConfigured);

            var throwing = DoThrottlePolicyReader.ReadCore((_, __) => throw new InvalidOperationException("registry broken"));
            Assert.False(throwing.ThrottleConfigured);
            Assert.Null(throwing.ThrottleSources);
        }

        [Fact]
        public void Empty_registry_yields_empty_snapshot()
        {
            var snapshot = DoThrottlePolicyReader.ReadCore((_, __) => null);

            Assert.False(snapshot.ThrottleConfigured);
            Assert.Null(snapshot.ThrottleSources);
            Assert.Null(snapshot.GpoDownloadMode);
            Assert.Null(snapshot.MdmDownloadMode);
        }
    }
}
