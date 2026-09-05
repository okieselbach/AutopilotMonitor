using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services.Ime
{
    /// <summary>
    /// Enqueues an IME-installer archive job when a new IME version is first sighted.
    /// Implementations must be fail-soft: the caller sits on the ingest hot path's
    /// fire-and-forget continuation and must never observe an exception.
    /// <paramref name="visibilityDelay"/> parks the message: the queue function uses it to
    /// re-enqueue while archiving is switched off.
    /// </summary>
    public interface IImeMsiArchiveProducer
    {
        Task EnqueueAsync(
            ImeMsiArchiveEnvelope envelope,
            TimeSpan? visibilityDelay = null,
            CancellationToken cancellationToken = default);
    }
}
