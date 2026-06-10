using System;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    /// <summary>
    /// Session 9d052230 (2026-05-21) regression coverage: ESP-Apps failure with a Windows HRESULT
    /// embedded in the subcategory statusText must surface
    /// <c>errorCode</c>/<c>failedSubcategory</c>/<c>category</c> on the
    /// <see cref="DecisionSignalKind.EspTerminalFailure"/> signal payload so the DecisionEngine
    /// can propagate it to the terminal <c>enrollment_failed</c> event.
    /// </summary>
    public sealed class EspFailureDetectedHResultTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 5, 21, 12, 25, 41, DateTimeKind.Utc);

        // ------------------------------------------------------------------ Regex extraction

        [Theory]
        [InlineData("Apps (0x87d1041c)", "0x87d1041c")]
        [InlineData("Security policies (0x80070005)", "0x80070005")]
        [InlineData("Certificates (No setup needed)", null)]
        [InlineData("Apps (Identifying)", null)]
        [InlineData("Apps (0X87D1041C)", "0x87d1041c")]
        [InlineData("Network connections (1 of 1 added)", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void TryExtractErrorCode_extracts_parenthesised_hex_tail(string statusText, string expected)
        {
            var actual = ProvisioningStatusTracker.TryExtractErrorCode(statusText);
            Assert.Equal(expected, actual);
        }

        // ------------------------------------------------------------------ EspAndHelloTrackerAdapter

        [Fact]
        public void Coordinator_adapter_forwards_errorCode_failedSubcategory_category_to_signal_payload()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var clock = new VirtualClock(Fixed);
            var ingress = new FakeSignalIngressSink();
            var coordinator = new EspAndHelloTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: new InformationalEventPost(new FakeSignalIngressSink(), clock),
                logger: logger);

            try
            {
                using var adapter = new EspAndHelloTrackerAdapter(coordinator, ingress, clock);

                adapter.TriggerEspFailureFromTest(new EspFailureDetectedEventArgs(
                    failureType: "Provisioning_DeviceSetup_Apps_Failed",
                    errorCode: "0x87d1041c",
                    failedSubcategory: "Apps",
                    category: "DeviceSetup"));

                var posted = Assert.Single(ingress.Posted);
                Assert.Equal(DecisionSignalKind.EspTerminalFailure, posted.Kind);
                Assert.Equal("Provisioning_DeviceSetup_Apps_Failed", posted.Payload!["failureType"]);
                Assert.Equal("0x87d1041c", posted.Payload!["errorCode"]);
                Assert.Equal("Apps", posted.Payload!["failedSubcategory"]);
                Assert.Equal("DeviceSetup", posted.Payload!["category"]);
                Assert.Equal("0x87d1041c", posted.Evidence.DerivationInputs!["errorCode"]);
            }
            finally { coordinator.Dispose(); }
        }

        [Fact]
        public void Coordinator_adapter_omits_optional_keys_when_only_failureType_is_set()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var clock = new VirtualClock(Fixed);
            var ingress = new FakeSignalIngressSink();
            var coordinator = new EspAndHelloTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: new InformationalEventPost(new FakeSignalIngressSink(), clock),
                logger: logger);

            try
            {
                using var adapter = new EspAndHelloTrackerAdapter(coordinator, ingress, clock);

                // Backwards-compat: ShellCore-derived failures use the string overload —
                // no HRESULT, no subcategory, no category surface.
                adapter.TriggerEspFailureFromTest("ESPProgress_Timeout");

                var posted = Assert.Single(ingress.Posted);
                Assert.Equal("ESPProgress_Timeout", posted.Payload!["failureType"]);
                Assert.False(posted.Payload!.ContainsKey("errorCode"));
                Assert.False(posted.Payload!.ContainsKey("failedSubcategory"));
                Assert.False(posted.Payload!.ContainsKey("category"));
            }
            finally { coordinator.Dispose(); }
        }
    }
}
