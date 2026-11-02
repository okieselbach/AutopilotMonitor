using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>Folds what Entra answers for a tenant's app token into the tenant's Graph access state.</summary>
    public interface IGraphAccessHealthRecorder
    {
        /// <summary>Entra refused the token on an agent request. Never throws.</summary>
        Task RecordRejectedAsync(string tenantId, EntraTokenRejection rejection);

        /// <summary>A token with an application role was freshly minted for the app acting on this tenant. Never throws.</summary>
        Task RecordMintedAsync(string tenantId);
    }

    /// <summary>
    /// Two stages, so one failed request never reaches a customer. The first rejection only stamps
    /// the suspicion. A rejection at least <see cref="ConfirmAfter"/> later confirms it: only then
    /// does the portal show its notice and the operator get an alert. A freshly minted token with an
    /// application role ends the episode. A token from the cache proves nothing and never reaches
    /// this class: it stays valid for up to 55 minutes after the enterprise application is gone.
    /// Neither does a token without a role, or the notice would vanish the moment an admin signs in
    /// to read it: that sign-in re-creates the application without any permission.
    /// <para>
    /// Every decision runs on the fresh row, so across instances only one write wins and the ops
    /// event fires once. Codes that blame our own credentials never touch the tenant: an expired
    /// secret would otherwise tell every customer to repair something on their side.
    /// </para>
    /// </summary>
    public sealed class GraphAccessHealthRecorder : IGraphAccessHealthRecorder
    {
        public static readonly TimeSpan ConfirmAfter = TimeSpan.FromMinutes(15);

        private const string BackupSource = "graph-access";
        private static readonly TimeSpan CredentialAlertInterval = TimeSpan.FromHours(1);

        private readonly TenantConfigurationService _tenantConfig;
        private readonly Lazy<OpsEventService> _opsEvents;
        private readonly ILogger<GraphAccessHealthRecorder> _logger;
        private readonly TimeProvider _time;
        private long _lastCredentialAlertTicks;

        public GraphAccessHealthRecorder(
            TenantConfigurationService tenantConfig,
            Lazy<OpsEventService> opsEvents,
            ILogger<GraphAccessHealthRecorder> logger,
            TimeProvider? time = null)
        {
            _tenantConfig = tenantConfig;
            _opsEvents = opsEvents;
            _logger = logger;
            _time = time ?? TimeProvider.System;
        }

        public async Task RecordRejectedAsync(string tenantId, EntraTokenRejection rejection)
        {
            try
            {
                var now = _time.GetUtcNow().UtcDateTime;
                if (rejection.IsOurCredential)
                {
                    await AlertOurCredentialAsync(tenantId, rejection, now).ConfigureAwait(false);
                    return;
                }

                // Cheap pre-filter on the cached view; the write decides again on the fresh row.
                var cached = await _tenantConfig.GetConfigurationIfExistsAsync(tenantId).ConfigureAwait(false);
                if (cached == null || !cached.HasAnyDeviceValidation() || cached.GraphAccessRejectionConfirmedAt != null)
                    return;
                if (cached.GraphAccessRejectedSince is { } cachedSince && now - cachedSince < ConfirmAfter)
                    return;

                var confirmed = false;
                var update = await _tenantConfig.UpdateAsync(tenantId, row =>
                {
                    confirmed = false;
                    if (row.GraphAccessRejectionConfirmedAt != null)
                        return false;
                    if (row.GraphAccessRejectedSince is not { } since)
                    {
                        row.GraphAccessRejectedSince = now;
                        row.GraphAccessRejectedCode = rejection.ErrorCode;
                        return true;
                    }
                    if (now - since < ConfirmAfter)
                        return false;

                    row.GraphAccessRejectionConfirmedAt = now;
                    row.GraphAccessRejectedCode = rejection.ErrorCode;
                    confirmed = true;
                    return true;
                }, BackupSource, "Entra rejected the app token").ConfigureAwait(false);

                if (update.Status != TenantConfigUpdateStatus.Updated)
                    return;

                if (!confirmed)
                {
                    _logger.LogWarning(
                        "Graph access for tenant {TenantId} is suspect: Entra rejected the app token ({ErrorCode}, status {StatusCode})",
                        tenantId, rejection.ErrorCode, rejection.StatusCode);
                    return;
                }

                _logger.LogWarning(
                    "Graph access for tenant {TenantId} is confirmed lost: Entra still rejects the app token ({ErrorCode}, status {StatusCode})",
                    tenantId, rejection.ErrorCode, rejection.StatusCode);
                await _opsEvents.Value.RecordGraphAccessRejectedAsync(tenantId, rejection.ErrorCode, rejection.StatusCode).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recording the Graph token rejection of tenant {TenantId} failed", tenantId);
            }
        }

        public async Task RecordMintedAsync(string tenantId)
        {
            try
            {
                var cached = await _tenantConfig.GetConfigurationIfExistsAsync(tenantId).ConfigureAwait(false);
                if (cached?.GraphAccessRejectedSince == null)
                    return;

                var wasConfirmed = false;
                string? code = null;
                var update = await _tenantConfig.UpdateAsync(tenantId, row =>
                {
                    if (row.GraphAccessRejectedSince == null)
                        return false;
                    wasConfirmed = row.GraphAccessRejectionConfirmedAt != null;
                    code = row.GraphAccessRejectedCode;
                    row.GraphAccessRejectedSince = null;
                    row.GraphAccessRejectedCode = null;
                    row.GraphAccessRejectionConfirmedAt = null;
                    return true;
                }, BackupSource, "Entra issued an app token again").ConfigureAwait(false);

                if (update.Status != TenantConfigUpdateStatus.Updated || !wasConfirmed)
                    return;

                _logger.LogWarning("Graph access for tenant {TenantId} is back: Entra issued an app token again (was {ErrorCode})", tenantId, code);
                await _opsEvents.Value.RecordGraphAccessRecoveredAsync(tenantId, code).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recording the recovered Graph access of tenant {TenantId} failed", tenantId);
            }
        }

        private async Task AlertOurCredentialAsync(string tenantId, EntraTokenRejection rejection, DateTime now)
        {
            // Hits every tenant at once: one alert per instance and hour says everything.
            var last = Interlocked.Read(ref _lastCredentialAlertTicks);
            if (now.Ticks - last < CredentialAlertInterval.Ticks)
                return;
            if (Interlocked.CompareExchange(ref _lastCredentialAlertTicks, now.Ticks, last) != last)
                return;

            _logger.LogError(
                "Entra rejects our app credentials ({ErrorCode}, status {StatusCode}, seen on tenant {TenantId}); device validation fails for every tenant on that app",
                rejection.ErrorCode, rejection.StatusCode, tenantId);
            await _opsEvents.Value.RecordGraphAppCredentialRejectedAsync(tenantId, rejection.ErrorCode, rejection.StatusCode).ConfigureAwait(false);
        }
    }
}
