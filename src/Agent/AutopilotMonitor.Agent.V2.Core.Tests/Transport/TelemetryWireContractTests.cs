#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Events;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Signals;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Transitions;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.Events;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// The agent → backend wire contract of <c>POST /api/agent/telemetry</c>, proven from the
    /// WRITER side. <see cref="TelemetryItemDto"/> (Shared) is the one class both ends serialise;
    /// <see cref="TelemetryItem.ToWire"/> is the only mapping onto it.
    /// <list type="bullet">
    ///   <item>Round trip + reflection: every DTO property is populated by <c>ToWire()</c> and
    ///   survives Newtonsoft defaults — a DTO field added without a mapping fails here.</item>
    ///   <item>Frozen body: the real batch serialiser's output for one item per
    ///   <see cref="TelemetryItemKind"/> is pinned under <c>tests/fixtures/telemetry-wire/</c>.
    ///   The backend's <c>TelemetryWireFixtureTests</c> reads the same files and demands zero
    ///   rejections, so a rename on either side breaks a test until both sides and the fixture
    ///   agree. Regenerate with <c>AM_WRITE_WIRE_FIXTURES=1</c>.</item>
    ///   <item>Poison body: the exact 422 shape the backend writes (pinned there by
    ///   <c>TelemetryWireFixtureTests</c>) is parsed as an item-level poison signal here.</item>
    /// </list>
    /// </summary>
    public sealed class TelemetryWireContractTests
    {
        private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
        private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
        private const string FixtureFile = "agent-v2.json";

        private static readonly DateTime At = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private static TelemetryItem FullyPopulatedItem() =>
            new TelemetryItem(
                kind: TelemetryItemKind.DecisionTransition,
                partitionKey: $"{TenantId}_{SessionId}",
                rowKey: "0000000007",
                telemetryItemId: 42,
                sessionTraceOrdinal: 7,
                payloadJson: "{\"StepIndex\":7}",
                requiresImmediateFlush: true,
                enqueuedAtUtc: At,
                retryCount: 3);

        // ============================================================ ToWire mapping

        [Fact]
        public void ToWire_round_trips_every_property_through_Newtonsoft_defaults()
        {
            var item = FullyPopulatedItem();

            var json = JsonConvert.SerializeObject(item.ToWire());
            var dto = JsonConvert.DeserializeObject<TelemetryItemDto>(json)!;

            Assert.Equal("DecisionTransition", dto.Kind);
            Assert.Equal(item.PartitionKey, dto.PartitionKey);
            Assert.Equal(item.RowKey, dto.RowKey);
            Assert.Equal(item.TelemetryItemId, dto.TelemetryItemId);
            Assert.Equal(item.SessionTraceOrdinal, dto.SessionTraceOrdinal);
            Assert.Equal(item.PayloadJson, dto.PayloadJson);
            Assert.Equal(item.RequiresImmediateFlush, dto.RequiresImmediateFlush);
            Assert.Equal(item.EnqueuedAtUtc, dto.EnqueuedAtUtc);
            Assert.Equal(DateTimeKind.Utc, dto.EnqueuedAtUtc.Kind);
            Assert.Equal(item.RetryCount, dto.RetryCount);
        }

        [Fact]
        public void ToWire_populates_every_TelemetryItemDto_property()
        {
            // A DTO property that ToWire() does not set keeps its default — this catches a field
            // added to the wire type without a mapping (the backend would read the default forever).
            var dto = FullyPopulatedItem().ToWire();

            var unset = typeof(TelemetryItemDto)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => IsDefault(p.GetValue(dto), p.PropertyType))
                .Select(p => p.Name)
                .ToList();

            Assert.True(unset.Count == 0,
                "TelemetryItem.ToWire() leaves these TelemetryItemDto properties at their default: " + string.Join(", ", unset));
        }

        [Fact]
        public void TelemetryItemDto_properties_mirror_TelemetryItem()
        {
            var itemProps = typeof(TelemetryItem).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToDictionary(p => p.Name, p => p.PropertyType);

            foreach (var dtoProp in typeof(TelemetryItemDto).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.True(itemProps.TryGetValue(dtoProp.Name, out var itemType),
                    $"TelemetryItemDto.{dtoProp.Name} has no counterpart on TelemetryItem");
                var expected = dtoProp.Name == nameof(TelemetryItemDto.Kind) ? typeof(TelemetryItemKind) : dtoProp.PropertyType;
                Assert.True(itemType == expected,
                    $"TelemetryItemDto.{dtoProp.Name} is {dtoProp.PropertyType.Name}, TelemetryItem.{dtoProp.Name} is {itemType.Name}");
            }
        }

        [Fact]
        public void Every_kind_serialises_by_name_on_the_wire_and_on_the_spool_line()
        {
            foreach (var kind in TelemetryItemKinds.All)
            {
                var item = new TelemetryItem(kind, "T_S", "rk", 1, 1, "{}", false, At);

                var wire = JObject.Parse(JsonConvert.SerializeObject(item.ToWire()));
                Assert.Equal(kind.ToString(), (string?)wire["Kind"]);

                // The spool line (TelemetryItemSerializer) has always written the NAME too; the enum's
                // move to Shared must not turn it into an ordinal (a crash-restart rehydrates it).
                var spool = JObject.Parse(TelemetryItemSerializer.Serialize(item));
                Assert.Equal(kind.ToString(), (string?)spool["Kind"]);
                Assert.Equal(kind, TelemetryItemSerializer.Deserialize(spool.ToString(Formatting.None)).Kind);

                Assert.True(TelemetryItemKinds.TryParse(kind.ToString(), out var parsed));
                Assert.Equal(kind, parsed);
            }
        }

        // ============================================================ Frozen wire body

        [Fact]
        public void Frozen_wire_body_matches_the_current_serializer()
        {
            var items = OneItemPerKind();
            Assert.Equal(TelemetryItemKinds.All.OrderBy(k => k), items.Select(i => i.Kind).Distinct().OrderBy(k => k));

            var body = BackendTelemetryUploader.SerializeBatch(items);
            var path = Path.Combine(RepoPaths.Fixtures("telemetry-wire"), FixtureFile);

            if (Environment.GetEnvironmentVariable("AM_WRITE_WIRE_FIXTURES") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, body + "\n", new UTF8Encoding(false));
            }

            Assert.True(File.Exists(path), "Missing wire fixture " + path + " — run this test with AM_WRITE_WIRE_FIXTURES=1 to create it.");
            var frozen = File.ReadAllText(path).TrimEnd('\r', '\n');
            Assert.True(string.Equals(frozen, body, StringComparison.Ordinal),
                "The agent's telemetry wire body changed. If the change is intended, update the backend ingest " +
                "(TelemetryWireFixtureTests must still dispatch every frozen body) and regenerate with " +
                "AM_WRITE_WIRE_FIXTURES=1.\nExpected: " + frozen + "\nActual:   " + body);
        }

        /// <summary>
        /// One item per kind through the REAL emitters (payload shapes are theirs, not hand-typed),
        /// re-based onto fixed transport fields so the body is byte-stable.
        /// </summary>
        private static IReadOnlyList<TelemetryItem> OneItemPerKind()
        {
            using var tmp = new TempDirectory();
            var transport = new FakeTelemetryTransport();
            var counter = new EventSequenceCounter(new EventSequencePersistence(tmp.File("seq.json")));

            new TelemetryEventEmitter(transport, counter, SessionId, TenantId).Emit(new EnrollmentEvent
            {
                EventId = "00000000-0000-4000-8000-000000000001",
                EventType = Constants.EventTypes.AgentStarted,
                Severity = EventSeverity.Info,
                Source = "Agent",
                Timestamp = At,
                Phase = EnrollmentPhase.Unknown,
                Message = "Agent started",
                Data = new Dictionary<string, object> { ["agentVersion"] = "2.0.0" },
            });

            new TelemetrySignalEmitter(transport, SessionId, TenantId).Emit(new DecisionSignal(
                sessionSignalOrdinal: 1,
                sessionTraceOrdinal: 1,
                kind: DecisionSignalKind.SessionStarted,
                kindSchemaVersion: 1,
                occurredAtUtc: At,
                sourceOrigin: "Fixture",
                evidence: new Evidence(EvidenceKind.Raw, "raw-1", "session start")));

            new TelemetryTransitionEmitter(transport, SessionId, TenantId).Emit(new DecisionTransition(
                stepIndex: 1,
                sessionTraceOrdinal: 2,
                signalOrdinalRef: 1,
                occurredAtUtc: At,
                trigger: "SessionStarted",
                fromStage: SessionStage.Unknown,
                toStage: SessionStage.AwaitingEspPhaseChange,
                taken: true,
                deadEndReason: null,
                reducerVersion: "1.0.0"));

            return transport.Enqueued
                .Select(e => new TelemetryItem(e.Kind, e.PartitionKey, e.RowKey, e.TelemetryItemId, e.SessionTraceOrdinal,
                    e.PayloadJson, e.RequiresImmediateFlush, At))
                .ToList();
        }

        // ============================================================ Poison body

        /// <summary>
        /// The exact 422 body the backend writes for rejected items (pinned byte-for-byte in the
        /// backend's TelemetryWireFixtureTests). camelCase because the backend serialises with
        /// its wire options; the agent's parser accepts both casings.
        /// </summary>
        internal const string BackendPoisonBody =
            "{\"error\":\"2 of 3 telemetry item(s) cannot be ingested (unknown Kind or unusable payload); they were not stored.\"," +
            "\"code\":\"TelemetryItemsRejected\",\"correlationId\":\"cid-1\",\"poison\":true,\"rejectedRowKeys\":[\"rk-1\",\"rk-3\"]," +
            "\"reason\":\"unknown_kind=1;unparseable=1\",\"received\":3,\"rejected\":2}";

        [Fact]
        public async Task Backend_422_poison_body_is_parsed_as_item_level_poison()
        {
            using var handler = new RecordingHttpMessageHandler().QueueStatus((HttpStatusCode)422, BackendPoisonBody);
            using var http = new HttpClient(handler);
            var sut = new BackendTelemetryUploader(http, baseUrl: "https://backend.example.invalid", tenantId: TenantId, agentVersion: "2.0.0");

            var result = await sut.UploadBatchAsync(new[] { FullyPopulatedItem() }, CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(result.IsPoison);
            Assert.Equal(new[] { "rk-1", "rk-3" }, result.PoisonRowKeys);
            Assert.Equal("unknown_kind=1;unparseable=1", result.ErrorReason);
        }

        private static bool IsDefault(object? value, Type type)
        {
            if (value == null) return true;
            if (type == typeof(string)) return ((string)value).Length == 0;
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            return underlying.IsValueType && value.Equals(Activator.CreateInstance(underlying));
        }
    }
}
