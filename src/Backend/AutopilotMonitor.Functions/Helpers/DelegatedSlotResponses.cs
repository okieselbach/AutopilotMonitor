using System.Net;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker.Http;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>The one 409 shape every slot-limited mutation writes (see <see cref="DelegatedSlotLimitReachedResponse"/>).</summary>
public static class DelegatedSlotResponses
{
    public static DelegatedSlotLimitReachedResponse Build(DelegatedSlotViolation violation)
    {
        var who = string.IsNullOrWhiteSpace(violation.HomeTenantDomain) ? violation.HomeTenantId : violation.HomeTenantDomain;
        return new DelegatedSlotLimitReachedResponse
        {
            Error = $"Delegated tenant slot limit reached for {who}: {violation.Used} of {violation.Limit} slot(s) in use, {violation.Required} more needed. Raise the tenant's limit (plan package or Global Admin override) and retry.",
            HomeTenantId = violation.HomeTenantId,
            HomeTenantDomain = violation.HomeTenantDomain,
            Used = violation.Used,
            Limit = violation.Limit,
            Required = violation.Required,
        };
    }

    public static async Task<HttpResponseData> ConflictAsync(HttpRequestData req, DelegatedSlotViolation violation)
    {
        var conflict = req.CreateResponse(HttpStatusCode.Conflict);
        await conflict.WriteAsJsonAsync(Build(violation));
        return conflict;
    }
}
