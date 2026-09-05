using System;
using AutopilotMonitor.Functions.Services.Queueing;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The shared decode step of every [QueueTrigger] function: a <c>false</c> means the message is
/// permanently unusable and the function returns so the host deletes it — never a throw, which
/// would burn dequeue attempts on a message that can never succeed.
/// </summary>
public class QueueEnvelopeReaderTests
{
    private sealed class Envelope
    {
        public string TenantId { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    [Fact]
    public void Valid_json_yields_envelope()
    {
        var msg = Message(JsonConvert.SerializeObject(new Envelope { TenantId = "t1", Count = 7 }));

        var ok = QueueEnvelopeReader.TryRead<Envelope>(msg, NullLogger.Instance, out var envelope);

        Assert.True(ok);
        Assert.Equal("t1", envelope.TenantId);
        Assert.Equal(7, envelope.Count);
    }

    [Fact]
    public void Malformed_json_is_rejected_without_throwing()
    {
        var msg = Message("{ not json");

        var ok = QueueEnvelopeReader.TryRead<Envelope>(msg, NullLogger.Instance, out var envelope);

        Assert.False(ok);
        Assert.Null(envelope);
    }

    [Fact]
    public void Null_literal_is_rejected()
    {
        var msg = Message("null");

        var ok = QueueEnvelopeReader.TryRead<Envelope>(msg, NullLogger.Instance, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Failed_validation_is_rejected()
    {
        var msg = Message(JsonConvert.SerializeObject(new Envelope { TenantId = string.Empty }));

        var ok = QueueEnvelopeReader.TryRead<Envelope>(
            msg, NullLogger.Instance, out _, validate: e => !string.IsNullOrEmpty(e.TenantId));

        Assert.False(ok);
    }

    [Fact]
    public void Validation_passes_when_predicate_holds()
    {
        var msg = Message(JsonConvert.SerializeObject(new Envelope { TenantId = "t1" }));

        var ok = QueueEnvelopeReader.TryRead<Envelope>(
            msg, NullLogger.Instance, out var envelope, validate: e => !string.IsNullOrEmpty(e.TenantId));

        Assert.True(ok);
        Assert.Equal("t1", envelope.TenantId);
    }

    private static QueueMessage Message(string body) => QueuesModelFactory.QueueMessage(
        messageId: "msg-" + Guid.NewGuid().ToString("N"),
        popReceipt: "pop",
        body: new BinaryData(body),
        dequeueCount: 1);
}
