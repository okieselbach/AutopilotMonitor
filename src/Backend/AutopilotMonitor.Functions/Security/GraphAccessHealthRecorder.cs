using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>Folds what Entra and Graph answer for a tenant's app token into the tenant's Graph access state.</summary>
    public interface IGraphAccessHealthRecorder
    {
        /// <summary>Entra refused the token on an agent request. Never throws.</summary>
        Task RecordRejectedAsync(string tenantId, EntraTokenRejection rejection);

        /// <summary>
        /// A token with an application role was freshly minted for the app acting on this tenant;
        /// <paramref name="roles"/> are its application roles. Never throws.
        /// </summary>
        Task RecordMintedAsync(string tenantId, IReadOnlyCollection<string> roles);

        /// <summary>
        /// An agent request was not admitted while these switched-on validations lacked their optional
        /// Graph permission. Never throws.
        /// </summary>
        Task RecordAddOnPermissionMissingAsync(string tenantId, IReadOnlyList<AddOnValidation> addOns);

        /// <summary>Ends the missing-permission episodes of validations that are switched off. Never throws.</summary>
        Task ReconcileSwitchedOffAddOnsAsync(string tenantId);
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
    /// The add-on validations (<see cref="AddOnValidation"/>) run the same two stages per validation:
    /// a device that was not admitted because the switched-on validation lacks its optional
    /// permission stamps the suspicion; a further one, or a fresh token still without the permission,
    /// at least <see cref="ConfirmAfter"/> later confirms it. A fresh token that carries the
    /// permission, or the switch turned off, ends the episode.
    /// </para>
    /// <para>
    /// Every decision runs on the fresh row, so across instances only one write wins and the ops
    /// event fires once. Codes that blame our own credentials never touch the tenant: an expired
    /// secret would otherwise tell every customer to repair something on their side.
    /// </para>
    /// </summary>
    public sealed class GraphAccessHealthRecorder : IGraphAccessHealthRecorder
    {
        public static readonly TimeSpan ConfirmAfter = TimeSpan.FromMinutes(15);

        // Resolutions of an add-on episode, as AddOnPermissionResolved reports them.
        internal const string ResolvedByGrant = "granted";
        internal const string ResolvedBySwitchOff = "switched_off";
        internal const string ResolvedByAppHomingFlip = "app_homing_flip";

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

        private enum Stage { Unchanged, Suspect, Confirmed }

        private enum AddOnChange { None, Confirm, ResolveGranted, ResolveSwitchedOff }

        private sealed record AddOnOutcome(AddOnValidation AddOn, AddOnChange Change, DateTime Since, bool WasConfirmed);

        /// <summary>
        /// One sighting of an episode: the first stamps the suspicion, one at least
        /// <see cref="ConfirmAfter"/> later confirms it, anything else changes nothing.
        /// </summary>
        private static Stage Advance(DateTime? since, DateTime? confirmedAt, DateTime now)
        {
            if (confirmedAt != null)
                return Stage.Unchanged;
            if (since is not { } s)
                return Stage.Suspect;
            return now - s < ConfirmAfter ? Stage.Unchanged : Stage.Confirmed;
        }

        /// <summary>
        /// What a fresh token (<paramref name="roles"/>), or a switch check without one (null),
        /// means for an open add-on episode.
        /// </summary>
        private static AddOnChange PlanAddOnChange(AddOnValidation addOn, TenantConfiguration row, IReadOnlyCollection<string>? roles, DateTime now)
        {
            if (addOn.MissingSince(row) is not { } since)
                return AddOnChange.None;
            if (!addOn.IsEnabled(row))
                return AddOnChange.ResolveSwitchedOff;
            if (roles == null)
                return AddOnChange.None;
            if (addOn.IsGranted(roles))
                return AddOnChange.ResolveGranted;
            return Advance(since, addOn.MissingConfirmedAt(row), now) == Stage.Confirmed ? AddOnChange.Confirm : AddOnChange.None;
        }

        private static void ApplyAddOnChanges(TenantConfiguration row, IReadOnlyCollection<string>? roles, DateTime now, List<AddOnOutcome> outcomes)
        {
            foreach (var addOn in AddOnValidation.All)
            {
                var change = PlanAddOnChange(addOn, row, roles, now);
                if (change == AddOnChange.None)
                    continue;
                outcomes.Add(new AddOnOutcome(addOn, change, addOn.MissingSince(row)!.Value, addOn.MissingConfirmedAt(row) != null));
                if (change == AddOnChange.Confirm)
                    addOn.StampConfirmed(row, now);
                else
                    addOn.ClearEpisode(row);
            }
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
                if (cached == null || !cached.HasAnyDeviceValidation()
                    || Advance(cached.GraphAccessRejectedSince, cached.GraphAccessRejectionConfirmedAt, now) == Stage.Unchanged)
                    return;

                var stage = Stage.Unchanged;
                var update = await _tenantConfig.UpdateAsync(tenantId, row =>
                {
                    stage = Advance(row.GraphAccessRejectedSince, row.GraphAccessRejectionConfirmedAt, now);
                    if (stage == Stage.Unchanged)
                        return false;
                    if (stage == Stage.Suspect)
                        row.GraphAccessRejectedSince = now;
                    else
                        row.GraphAccessRejectionConfirmedAt = now;
                    row.GraphAccessRejectedCode = rejection.ErrorCode;
                    return true;
                }, BackupSource, "Entra rejected the app token").ConfigureAwait(false);

                if (update.Status != TenantConfigUpdateStatus.Updated)
                    return;

                if (stage == Stage.Suspect)
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

        public async Task RecordMintedAsync(string tenantId, IReadOnlyCollection<string> roles)
        {
            try
            {
                var now = _time.GetUtcNow().UtcDateTime;
                var cached = await _tenantConfig.GetConfigurationIfExistsAsync(tenantId).ConfigureAwait(false);
                if (cached == null
                    || (cached.GraphAccessRejectedSince == null
                        && AddOnValidation.All.All(a => PlanAddOnChange(a, cached, roles, now) == AddOnChange.None)))
                    return;

                var tokenCleared = false;
                var tokenWasConfirmed = false;
                string? code = null;
                var outcomes = new List<AddOnOutcome>();
                var blocksAll = false;
                var update = await _tenantConfig.UpdateAsync(tenantId, row =>
                {
                    tokenCleared = false;
                    outcomes.Clear();
                    if (row.GraphAccessRejectedSince != null)
                    {
                        tokenCleared = true;
                        tokenWasConfirmed = row.GraphAccessRejectionConfirmedAt != null;
                        code = row.GraphAccessRejectedCode;
                        row.GraphAccessRejectedSince = null;
                        row.GraphAccessRejectedCode = null;
                        row.GraphAccessRejectionConfirmedAt = null;
                    }
                    ApplyAddOnChanges(row, roles, now, outcomes);
                    blocksAll = AddOnValidation.BlocksAllValidation(row);
                    return tokenCleared || outcomes.Count > 0;
                }, BackupSource, "A fresh app token arrived").ConfigureAwait(false);

                if (update.Status != TenantConfigUpdateStatus.Updated)
                    return;

                if (tokenCleared && tokenWasConfirmed)
                {
                    _logger.LogWarning("Graph access for tenant {TenantId} is back: Entra issued an app token again (was {ErrorCode})", tenantId, code);
                    await _opsEvents.Value.RecordGraphAccessRecoveredAsync(tenantId, code).ConfigureAwait(false);
                }
                await ReportAddOnOutcomesAsync(tenantId, outcomes, blocksAll).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recording the fresh app token of tenant {TenantId} failed", tenantId);
            }
        }

        public async Task RecordAddOnPermissionMissingAsync(string tenantId, IReadOnlyList<AddOnValidation> addOns)
        {
            if (addOns.Count == 0)
                return;
            try
            {
                var now = _time.GetUtcNow().UtcDateTime;
                var cached = await _tenantConfig.GetConfigurationIfExistsAsync(tenantId).ConfigureAwait(false);
                if (cached == null
                    || !addOns.Any(a => a.IsEnabled(cached) && Advance(a.MissingSince(cached), a.MissingConfirmedAt(cached), now) != Stage.Unchanged))
                    return;

                var stages = new List<(AddOnValidation AddOn, Stage Stage, DateTime Since)>();
                var blocksAll = false;
                var update = await _tenantConfig.UpdateAsync(tenantId, row =>
                {
                    stages.Clear();
                    foreach (var addOn in addOns)
                    {
                        if (!addOn.IsEnabled(row))
                            continue;
                        var stage = Advance(addOn.MissingSince(row), addOn.MissingConfirmedAt(row), now);
                        if (stage == Stage.Suspect)
                            addOn.StampSince(row, now);
                        else if (stage == Stage.Confirmed)
                            addOn.StampConfirmed(row, now);
                        else
                            continue;
                        stages.Add((addOn, stage, addOn.MissingSince(row)!.Value));
                    }
                    blocksAll = AddOnValidation.BlocksAllValidation(row);
                    return stages.Count > 0;
                }, BackupSource, "An optional Graph permission is missing").ConfigureAwait(false);

                if (update.Status != TenantConfigUpdateStatus.Updated)
                    return;

                foreach (var (addOn, stage, since) in stages)
                {
                    if (stage == Stage.Suspect)
                    {
                        _logger.LogWarning(
                            "Add-on permission for tenant {TenantId} is suspect: {Feature} is switched on, but the app token lacks {Permission} and a device was not admitted",
                            tenantId, addOn.Feature, addOn.Permission);
                        continue;
                    }
                    await ReportConfirmedAsync(tenantId, addOn, since, blocksAll).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recording the missing add-on permission of tenant {TenantId} failed", tenantId);
            }
        }

        public async Task ReconcileSwitchedOffAddOnsAsync(string tenantId)
        {
            try
            {
                var now = _time.GetUtcNow().UtcDateTime;
                var cached = await _tenantConfig.GetConfigurationIfExistsAsync(tenantId).ConfigureAwait(false);
                if (cached == null || AddOnValidation.All.All(a => PlanAddOnChange(a, cached, null, now) == AddOnChange.None))
                    return;

                var outcomes = new List<AddOnOutcome>();
                var update = await _tenantConfig.UpdateAsync(tenantId, row =>
                {
                    outcomes.Clear();
                    ApplyAddOnChanges(row, null, now, outcomes);
                    return outcomes.Count > 0;
                }, BackupSource, "A validation that lacked its optional Graph permission was switched off").ConfigureAwait(false);

                if (update.Status == TenantConfigUpdateStatus.Updated)
                    await ReportAddOnOutcomesAsync(tenantId, outcomes, blocksAll: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconciling the add-on permission state of tenant {TenantId} failed", tenantId);
            }
        }

        private async Task ReportAddOnOutcomesAsync(string tenantId, List<AddOnOutcome> outcomes, bool blocksAll)
        {
            foreach (var outcome in outcomes)
            {
                if (outcome.Change == AddOnChange.Confirm)
                {
                    await ReportConfirmedAsync(tenantId, outcome.AddOn, outcome.Since, blocksAll).ConfigureAwait(false);
                    continue;
                }
                // An episode that was never confirmed was never shown: it ends without a trace.
                if (!outcome.WasConfirmed)
                    continue;
                var resolution = outcome.Change == AddOnChange.ResolveGranted ? ResolvedByGrant : ResolvedBySwitchOff;
                _logger.LogWarning(
                    "Add-on permission for tenant {TenantId} is resolved: {Feature} ({Resolution})",
                    tenantId, outcome.AddOn.Feature, resolution);
                await _opsEvents.Value.RecordAddOnPermissionResolvedAsync(tenantId, outcome.AddOn.Feature, resolution).ConfigureAwait(false);
            }
        }

        private async Task ReportConfirmedAsync(string tenantId, AddOnValidation addOn, DateTime since, bool blocksAll)
        {
            _logger.LogWarning(
                "Add-on permission for tenant {TenantId} is confirmed missing: {Feature} still lacks {Permission} (since {Since:o}, blocks all validation: {BlocksAll})",
                tenantId, addOn.Feature, addOn.Permission, since, blocksAll);
            await _opsEvents.Value.RecordAddOnPermissionMissingAsync(tenantId, addOn.Feature, addOn.Permission, since, blocksAll).ConfigureAwait(false);
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
