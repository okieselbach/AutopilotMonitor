using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Read-only access surface used by <see cref="DeletionManifestBuilder"/> to enumerate every
    /// table targeted by the cascade. Narrow on purpose: the builder gets exactly the four
    /// shapes it needs, nothing more — keeps the test stub trivial. Production implementation is
    /// the <c>TableStorageService.Deletion.cs</c> partial; tests inject a Moq.
    /// </summary>
    public interface ISessionDeletionInventoryReader
    {
        /// <summary>
        /// Loads the canonical Sessions row, used for <c>IndexRowKey</c> lookup +
        /// <c>DiagnosticsBlobName</c> capture (PR1) and <c>DeletionState</c>/<c>PendingDeletionManifestId</c>
        /// (PR3+ — ignored in PR1). Returns null when the row does not exist.
        /// </summary>
        Task<TableEntity?> GetSessionRowAsync(string tenantId, string sessionId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Loads a SessionsIndex row by its (tenantId, indexRowKey) coordinate. Used for the
        /// FINAL tombstone step's row dump. Returns null when missing (treated as already-gone).
        /// </summary>
        Task<TableEntity?> GetSessionsIndexRowAsync(string tenantId, string indexRowKey, CancellationToken cancellationToken = default);

        /// <summary>
        /// Streams entities matching <paramref name="filter"/> on <paramref name="tableName"/>.
        /// The builder formulates the OData filter per step class; this method is a pure passthrough.
        /// </summary>
        IAsyncEnumerable<TableEntity> QueryAsync(string tableName, string filter, CancellationToken cancellationToken = default);

        /// <summary>
        /// Direct (PK, RK) lookup against <paramref name="tableName"/>. Returns null on 404,
        /// rethrows any other <see cref="Azure.RequestFailedException"/>. Used for PK_RK_EXACT
        /// classes (VulnerabilityReports, DeviceSnapshot, EventSessionIndex, SessionInventoryContributions).
        /// </summary>
        Task<TableEntity?> GetEntityOrNullAsync(string tableName, string partitionKey, string rowKey, CancellationToken cancellationToken = default);
    }
}
