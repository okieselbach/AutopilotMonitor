using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Services.Offboarding
{
    /// <summary>
    /// Thin wrapper around <see cref="IMaintenanceRepository.EnumerateSessionsForOffboardingAsync"/>
    /// so the offboarding handler can take an injectable dependency that is trivially mockable
    /// (the repo interface has 20+ unrelated members; mocking it for every test is painful).
    /// <para>
    /// <b>Fail-loud contract:</b> any storage exception from the underlying enumerator
    /// propagates unchanged. Plan §7.4 + memory <c>feedback_storage_helpers_fail_soft</c>:
    /// silently returning an empty list during enumeration would let the handler proceed
    /// straight to wipe without cascade-backup. Tests in
    /// <c>OffboardingSessionEnumeratorTests</c> pin this behaviour.
    /// </para>
    /// </summary>
    public class OffboardingSessionEnumerator
    {
        private readonly IMaintenanceRepository _maintenance;

        public OffboardingSessionEnumerator(IMaintenanceRepository maintenance)
        {
            _maintenance = maintenance;
        }

        public virtual async IAsyncEnumerable<string> EnumerateAsync(
            string tenantId,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            await foreach (var sessionId in _maintenance.EnumerateSessionsForOffboardingAsync(tenantId, ct))
            {
                yield return sessionId;
            }
        }
    }
}
