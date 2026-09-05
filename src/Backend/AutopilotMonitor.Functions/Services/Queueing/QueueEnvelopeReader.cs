using System;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Queueing
{
    /// <summary>
    /// Envelope decoding shared by the <c>[QueueTrigger]</c> functions under <c>Functions/Queue/</c>.
    /// The functions bind the raw <see cref="QueueMessage"/> rather than a POCO so the producers'
    /// Newtonsoft contract stays the single wire format and <see cref="QueueMessage.DequeueCount"/>
    /// reaches the log line.
    /// <para>
    /// A <c>false</c> result means "permanently unusable": malformed JSON, a <c>null</c> literal,
    /// or a failed validation. The caller returns normally, the host deletes the message — the
    /// same drop the self-managed poll loop performs, without burning a dequeue attempt.
    /// </para>
    /// </summary>
    public static class QueueEnvelopeReader
    {
        public static bool TryRead<TEnvelope>(
            QueueMessage message,
            ILogger logger,
            out TEnvelope envelope,
            Func<TEnvelope, bool>? validate = null)
            where TEnvelope : class
        {
            if (message is null) throw new ArgumentNullException(nameof(message));
            if (logger is null) throw new ArgumentNullException(nameof(logger));

            envelope = null!;
            var name = typeof(TEnvelope).Name;

            TEnvelope? parsed;
            try
            {
                parsed = JsonConvert.DeserializeObject<TEnvelope>(message.Body.ToString());
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex,
                    "{Envelope}: malformed envelope JSON — dropping (msg {Id}, dequeue {N})",
                    name, message.MessageId, message.DequeueCount);
                return false;
            }

            if (parsed is null)
            {
                logger.LogWarning(
                    "{Envelope}: null envelope after deserialization — dropping (msg {Id}, dequeue {N})",
                    name, message.MessageId, message.DequeueCount);
                return false;
            }

            if (validate is not null && !validate(parsed))
            {
                logger.LogWarning(
                    "{Envelope}: envelope failed validation (missing required fields) — dropping (msg {Id}, dequeue {N})",
                    name, message.MessageId, message.DequeueCount);
                return false;
            }

            envelope = parsed;
            return true;
        }
    }
}
