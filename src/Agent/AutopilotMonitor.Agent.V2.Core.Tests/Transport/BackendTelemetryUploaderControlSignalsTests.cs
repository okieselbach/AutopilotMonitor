#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using Xunit;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// M4.6.ε — BackendTelemetryUploader parses backend-to-agent control signals out of the
    /// 2xx response body (<see cref="UploadResult.DeviceBlocked"/> / <see cref="UploadResult.UnblockAt"/> /
    /// <see cref="UploadResult.DeviceKillSignal"/> / <see cref="UploadResult.AdminAction"/> /
    /// <see cref="UploadResult.Actions"/>). Malformed bodies degrade cleanly to a plain
    /// <see cref="UploadResult.Ok"/>.
    /// </summary>
    public sealed class BackendTelemetryUploaderControlSignalsTests
    {
        private const string BaseUrl = "https://backend.test";
        private static readonly DateTime At = new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc);

        private static TelemetryItem NewEventItem(long id) => new TelemetryItem(
            kind: TelemetryItemKind.Event,
            partitionKey: "T1_S1",
            rowKey: id.ToString("D10"),
            telemetryItemId: id,
            sessionTraceOrdinal: id,
            payloadJson: "{\"EventType\":\"test\"}",
            requiresImmediateFlush: false,
            enqueuedAtUtc: At);

        private static (BackendTelemetryUploader sut, RecordingHttpMessageHandler handler) Build()
        {
            var handler = new RecordingHttpMessageHandler();
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            var sut = new BackendTelemetryUploader(
                httpClient: http,
                baseUrl: BaseUrl,
                tenantId: "T1",
                manufacturer: "m",
                model: "md",
                serialNumber: "sn",
                bootstrapToken: null,
                agentVersion: "2.0.0");
            return (sut, handler);
        }

        private static async System.Threading.Tasks.Task<UploadResult> UploadWithBody(string body)
        {
            var (sut, handler) = Build();
            handler.QueueStatus(HttpStatusCode.OK, body);
            return await sut.UploadBatchAsync(new List<TelemetryItem> { NewEventItem(1) }, CancellationToken.None);
        }

        [Fact]
        public async System.Threading.Tasks.Task Empty_body_returns_plain_ok()
        {
            var result = await UploadWithBody(string.Empty);
            Assert.True(result.Success);
            Assert.False(result.DeviceBlocked);
            Assert.False(result.DeviceKillSignal);
            Assert.Null(result.AdminAction);
            Assert.Null(result.Actions);
        }

        [Fact]
        public async System.Threading.Tasks.Task Malformed_json_body_returns_plain_ok()
        {
            var result = await UploadWithBody("{ not-json");
            Assert.True(result.Success);
            Assert.False(result.DeviceBlocked);
            Assert.False(result.DeviceKillSignal);
        }

        [Fact]
        public async System.Threading.Tasks.Task DeviceBlocked_true_is_parsed_with_unblock_timestamp()
        {
            var result = await UploadWithBody("{\"deviceBlocked\":true,\"unblockAt\":\"2026-04-21T12:00:00Z\"}");

            Assert.True(result.Success);
            Assert.True(result.DeviceBlocked);
            Assert.NotNull(result.UnblockAt);
            Assert.Equal(new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc), result.UnblockAt);
        }

        [Fact]
        public async System.Threading.Tasks.Task DeviceBlocked_accepts_PascalCase_field_names()
        {
            // Legacy IngestEventsResponse serialises PascalCase by default — V2 response parser
            // must accept both styles so M5 backend doesn't force a specific casing.
            var result = await UploadWithBody("{\"DeviceBlocked\":true}");

            Assert.True(result.DeviceBlocked);
            Assert.Null(result.UnblockAt);
        }

        [Fact]
        public async System.Threading.Tasks.Task DeviceKillSignal_true_is_parsed()
        {
            var result = await UploadWithBody("{\"deviceKillSignal\":true}");

            Assert.True(result.Success);
            Assert.True(result.DeviceKillSignal);
        }

        [Fact]
        public async System.Threading.Tasks.Task AdminAction_string_is_parsed()
        {
            var result = await UploadWithBody("{\"adminAction\":\"failed\"}");

            Assert.Equal("failed", result.AdminAction);
        }

        [Fact]
        public async System.Threading.Tasks.Task Actions_array_is_parsed_into_ServerAction_list()
        {
            var body = "{\"actions\":[{\"type\":\"rotate_config\",\"reason\":\"manual refresh\"}," +
                       "{\"type\":\"request_diagnostics\",\"reason\":\"support ticket\",\"ruleId\":\"R-42\"}]}";

            var result = await UploadWithBody(body);

            Assert.NotNull(result.Actions);
            Assert.Equal(2, result.Actions!.Count);
            Assert.Equal("rotate_config", result.Actions[0].Type);
            Assert.Equal("request_diagnostics", result.Actions[1].Type);
            Assert.Equal("R-42", result.Actions[1].RuleId);
        }

        [Fact]
        public async System.Threading.Tasks.Task Non_2xx_response_does_not_parse_control_signals()
        {
            var (sut, handler) = Build();
            handler.QueueStatus(HttpStatusCode.InternalServerError, "{\"deviceKillSignal\":true}");

            var result = await sut.UploadBatchAsync(new List<TelemetryItem> { NewEventItem(1) }, CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(result.IsTransient);
            Assert.False(result.DeviceKillSignal);
        }

        [Fact]
        public async System.Threading.Tasks.Task Body_with_only_unknown_fields_returns_plain_ok()
        {
            var result = await UploadWithBody("{\"unrelated\":\"value\",\"count\":7}");
            Assert.True(result.Success);
            Assert.False(result.DeviceBlocked);
            Assert.False(result.DeviceKillSignal);
            Assert.Null(result.Actions);
        }
    }
}
