using System;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Thrown by <c>TableStorageService.StoreSessionAsync</c> when a registration names a sessionId
    /// whose SessionTenantLookup row is already owned by a DIFFERENT tenant. The lookup is the
    /// authority every global-scope cross-tenant session resolve trusts, so its write is
    /// first-writer-wins: a tenant can never overwrite (poison) another tenant's mapping.
    /// Session ids are 122-bit random — a genuine cross-tenant collision does not happen, so a
    /// conflict is either a forged registration or an agent bug, and both must fail loudly
    /// (409 Conflict) instead of silently retargeting the platform-operator data plane.
    /// </summary>
    public class SessionTenantConflictException : InvalidOperationException
    {
        public string SessionId { get; }
        public string RequestedTenantId { get; }
        public string OwningTenantId { get; }

        public SessionTenantConflictException(string sessionId, string requestedTenantId, string owningTenantId)
            : base($"Session {sessionId} is already registered to another tenant; registration for tenant {requestedTenantId} refused")
        {
            SessionId = sessionId;
            RequestedTenantId = requestedTenantId;
            OwningTenantId = owningTenantId;
        }
    }
}
