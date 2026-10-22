using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Cuts the live SignalR streams of a revoked grant. A connection is negotiated under its caller's sign-in tenant
/// plus principal key (<see cref="Helpers.SignalRGroupHelper.UserId"/>), and every grant resolves under exactly one
/// such tenant, so each cut names it: a tenant membership (and a self-service delegation, which resolves only under
/// its owner — see <see cref="DelegatedAdminService"/>) that tenant; a platform role or an operator-granted
/// delegation the tenant of the UPN's identity binding. An action in one tenant therefore never reaches the same UPN
/// signed in to another. The one caller of <see cref="ISignalRNotificationService.DisconnectUserAsync"/>.
/// </summary>
public class LiveStreamRevoker
{
    private readonly ISignalRNotificationService _signalR;
    private readonly AdminIdentityBindingService _bindings;
    private readonly ILogger<LiveStreamRevoker> _logger;

    public LiveStreamRevoker(ISignalRNotificationService signalR, AdminIdentityBindingService bindings, ILogger<LiveStreamRevoker> logger)
    {
        _signalR = signalR;
        _bindings = bindings;
        _logger = logger;
    }

    /// <summary>Cuts the connections of <paramref name="principalKey"/> signed in to <paramref name="tenantId"/>.</summary>
    public Task CutAsync(string tenantId, string principalKey) => _signalR.DisconnectUserAsync(tenantId, principalKey);

    /// <summary>
    /// Cuts the connections a platform role or operator-granted delegation of <paramref name="principalKey"/> was
    /// exercised under: the tenant of its identity binding, the only tenant such a grant resolves under. No binding
    /// ⇒ the grant never resolved ⇒ nothing to cut. Fail-soft like the cut itself — the revoke has already happened.
    /// </summary>
    public async Task CutBoundIdentityAsync(string principalKey)
    {
        AdminIdentityBinding? binding;
        try
        {
            binding = await _bindings.GetAsync(principalKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the identity binding of {Upn} failed — its live streams were not cut", principalKey);
            return;
        }

        if (binding != null)
            await _signalR.DisconnectUserAsync(binding.TenantId, principalKey);
    }
}
