using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Activation
{
    /// <summary>
    /// Consumer logic for <c>tenant-auto-approve</c> envelopes, separated from the queue
    /// plumbing so it is unit-testable (same split as <c>AnalyzeOnEnrollmentEndHandler</c>).
    /// <para>
    /// Every early return DROPS the message (the worker deletes on success) — that is always
    /// the safe direction: a dropped auto-approve leaves the tenant on the waitlist where the
    /// operator sees and can approve it manually. Only a thrown exception retries.
    /// </para>
    /// </summary>
    public class TenantAutoApproveHandler
    {
        /// <summary>ApprovedBy sentinel. Deliberately without '@' so the shape-based
        /// <see cref="TenantApprovalService.IsRealUserUpn"/> guard can never treat it as a
        /// promotable user.</summary>
        public const string AutoApprovedBy = "System (auto-approve)";

        private readonly ILogger<TenantAutoApproveHandler> _logger;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly PreviewWhitelistService _previewWhitelistService;
        private readonly TenantConfigurationService _tenantConfigService;
        private readonly TenantApprovalService _tenantApprovalService;
        private readonly OpsEventService _opsEventService;

        public TenantAutoApproveHandler(
            ILogger<TenantAutoApproveHandler> logger,
            AdminConfigurationService adminConfigService,
            PreviewWhitelistService previewWhitelistService,
            TenantConfigurationService tenantConfigService,
            TenantApprovalService tenantApprovalService,
            OpsEventService opsEventService)
        {
            _logger = logger;
            _adminConfigService = adminConfigService;
            _previewWhitelistService = previewWhitelistService;
            _tenantConfigService = tenantConfigService;
            _tenantApprovalService = tenantApprovalService;
            _opsEventService = opsEventService;
        }

        public virtual async Task HandleAsync(TenantAutoApproveEnvelope envelope, CancellationToken ct)
        {
            var tenantId = envelope.TenantId;

            // Authoritative, uncached flag read at processing time (fail-closed → drop).
            // Messages are enqueued unconditionally at signup; this is the single decision
            // point, so flipping the flag off takes effect for all in-flight messages
            // immediately — they are dropped, not parked, because enabling auto-approve
            // later must not retroactively activate signups the operator meant to vet.
            if (!await _adminConfigService.IsAutoApproveNewTenantsEnabledAsync().ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "TenantAutoApprove: auto-approve disabled — dropping signup for tenant {TenantId} (manual approval)",
                    tenantId);
                return;
            }

            if (await _previewWhitelistService.IsApprovedAsync(tenantId).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "TenantAutoApprove: tenant {TenantId} already activated — nothing to do", tenantId);
                return;
            }

            var (tenantConfig, tenantExists) = await _tenantConfigService.TryGetConfigurationAsync(tenantId).ConfigureAwait(false);
            if (!tenantExists)
            {
                _logger.LogWarning(
                    "TenantAutoApprove: no TenantConfiguration for tenant {TenantId} — dropping", tenantId);
                return;
            }

            if (tenantConfig.IsCurrentlyDisabled())
            {
                _logger.LogWarning(
                    "TenantAutoApprove: tenant {TenantId} is suspended — dropping (manual approval after review)",
                    tenantId);
                return;
            }

            await _tenantApprovalService.ApproveWithSideEffectsAsync(tenantId, AutoApprovedBy).ConfigureAwait(false);

            // Fire-and-forget safe (OpsEventService never throws).
            await _opsEventService.RecordTenantAutoApprovedAsync(
                tenantId, tenantConfig.DomainName, envelope.SignupUpn).ConfigureAwait(false);
        }
    }
}
