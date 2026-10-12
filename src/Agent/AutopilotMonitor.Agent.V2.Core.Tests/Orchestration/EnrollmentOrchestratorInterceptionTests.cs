#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// The orchestrator side of an intercepted upload: one <c>telemetry_upload_blocked</c> with the
    /// evidence (queued behind the intercepted batch, never uploaded while paused), and the one
    /// terminal attempt — requested by <c>DrainSpool</c> or made by <see cref="EnrollmentOrchestrator.Stop"/>.
    /// </summary>
    [Collection("SerialThreading")]
    public sealed class EnrollmentOrchestratorInterceptionTests
    {
        private const int WaitMs = 10_000;

        private static TelemetryItemDraft Draft() =>
            new TelemetryItemDraft(
                kind: TelemetryItemKind.Event,
                partitionKey: "T1_S1",
                rowKey: "0000000001",
                payloadJson: "{}",
                isSessionScoped: true,
                requiresImmediateFlush: false);

        // The signal and the decision transition of the same post mention the event type too.
        private static bool IsBlockedEvent(TelemetryItem item) =>
            item.Kind == TelemetryItemKind.Event
            && item.PayloadJson.Contains(AutopilotMonitor.Shared.Constants.EventTypes.TelemetryUploadBlocked);

        private static TelemetryItem? FindBlockedEvent(EnrollmentOrchestrator sut) =>
            sut.SpoolForTests!.Peek(1000).FirstOrDefault(IsBlockedEvent);

        // Dates stay strings: the wire format of firstBlockedAtUtc is what is under test.
        private static JObject Parse(string json)
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(reader);
        }

        /// <summary>Starts the orchestrator and drives the first upload into an interception.</summary>
        private static async Task<EnrollmentOrchestrator> StartInterceptedAsync(EnrollmentOrchestratorRig rig)
        {
            rig.Uploader.QueueIntercepted(5);
            var sut = rig.Build();
            sut.Start();
            sut.SpoolForTests!.Enqueue(Draft());
            await sut.DrainAsync();
            Assert.True(sut.Transport.IsInterceptionPaused);
            return sut;
        }

        [Fact]
        public async Task Interception_queues_one_telemetry_upload_blocked_with_the_evidence_and_wakeups_stay_offline()
        {
            using var rig = new EnrollmentOrchestratorRig();
            var sut = await StartInterceptedAsync(rig);
            try
            {
                TelemetryItem? blocked = null;
                Assert.True(SpinWait.SpinUntil(() => (blocked = FindBlockedEvent(sut)) != null, WaitMs),
                    "telemetry_upload_blocked never reached the spool.");
                var calls = rig.Uploader.CallCount;

                var data = (JObject)Parse(blocked!.PayloadJson)["Data"]!;
                Assert.Equal(
                    new[] { "firstBlockedAtUtc", "operation", "pageTitle", "reason", "serverCertIssuer", "statusCode" },
                    data.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
                Assert.Equal("client_certificate_required", (string?)data["reason"]);
                Assert.Equal("agent/telemetry", (string?)data["operation"]);
                Assert.Equal(JTokenType.Integer, data["statusCode"]!.Type);
                Assert.Equal(403, (int)data["statusCode"]!);
                Assert.Equal(InterceptionFixtures.PlatformPageTitle, (string?)data["pageTitle"]);
                Assert.Equal(InterceptionFixtures.InspectionIssuer, (string?)data["serverCertIssuer"]);
                Assert.Equal(
                    EnrollmentOrchestratorRig.DefaultClockStart.ToString("o", CultureInfo.InvariantCulture),
                    (string?)data["firstBlockedAtUtc"]);

                // The marker was an immediate-upload item; neither its wakeup nor a further drain
                // touches the network while the pause holds.
                var drain = await sut.DrainAsync();
                Assert.Contains("upload intercepted", drain.LastErrorReason);
                Assert.Equal(calls, rig.Uploader.CallCount);
                Assert.Single(sut.SpoolForTests!.Peek(1000), IsBlockedEvent);
            }
            finally
            {
                sut.Stop();
            }
        }

        [Fact]
        public async Task Terminal_attempt_requested_by_the_termination_runs_once_and_Stop_adds_none()
        {
            using var rig = new EnrollmentOrchestratorRig();
            var sut = await StartInterceptedAsync(rig);
            Assert.True(SpinWait.SpinUntil(() => FindBlockedEvent(sut) != null, WaitMs));
            var calls = rig.Uploader.CallCount;

            sut.RequestTerminalUploadAttempt();
            Assert.True(SpinWait.SpinUntil(() => rig.Uploader.CallCount == calls + 1, WaitMs),
                "the woken drain loop did not make the terminal attempt.");

            sut.Stop();
            Assert.Equal(calls + 1, rig.Uploader.CallCount);
        }

        [Fact]
        public async Task Stop_makes_the_one_terminal_attempt_when_the_termination_did_not()
        {
            using var rig = new EnrollmentOrchestratorRig();
            var sut = await StartInterceptedAsync(rig);
            var calls = rig.Uploader.CallCount;

            sut.Stop();

            Assert.Equal(calls + 1, rig.Uploader.CallCount);
        }
    }
}
