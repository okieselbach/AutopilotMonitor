using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services.Ime
{
    /// <summary>
    /// Enqueues an IME-installer archive job when a new IME version is first sighted.
    /// Implementations must be fail-soft: the caller sits on the ingest hot path's
    /// fire-and-forget continuation and must never observe an exception.
    /// </summary>
    public interface IImeMsiArchiveProducer
    {
        Task EnqueueAsync(ImeMsiArchiveEnvelope envelope, CancellationToken cancellationToken = default);
    }
}
