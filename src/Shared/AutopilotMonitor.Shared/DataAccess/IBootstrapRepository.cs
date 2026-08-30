using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for bootstrap session management.
    /// Covers: BootstrapSessions table.
    /// </summary>
    public interface IBootstrapRepository
    {
        Task<bool> CreateBootstrapSessionAsync(BootstrapSession session);
        Task<BootstrapSession?> GetBootstrapSessionByCodeAsync(string shortCode);
        Task<BootstrapSession?> ValidateBootstrapTokenAsync(string token);
        Task<List<BootstrapSession>> GetBootstrapSessionsAsync(string tenantId);
        /// <summary>
        /// Revokes a bootstrap session only when the code belongs to <paramref name="tenantId"/>.
        /// Returns false (indistinguishable from "not found") when the code does not exist
        /// or is owned by another tenant — the short code is a platform-global identifier
        /// and the caller's tenant scope must be enforced here, not just at the route.
        /// </summary>
        Task<bool> RevokeBootstrapSessionAsync(string tenantId, string shortCode);
        Task<bool> IncrementBootstrapUsageAsync(string shortCode);
        Task<int> CleanupExpiredAsync();
    }
}
