using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>Which authority tier is driving a config patch/revert — selects the field deny-list.</summary>
    public enum TenantConfigCallerTier
    {
        GlobalAdmin,
        // Phase 2 (not wired yet): tenant admins / delegated (MSP) admins get the stricter
        // deny-list that additionally blocks the GA-only toggles. The lists and tests exist
        // now so the later rollout is a policy-catalog flip, not new logic.
        TenantAdmin,
        DelegatedAdmin,
    }

    public enum PatchFailure
    {
        None,
        NotFound,
        InvalidField,
        ValidationFailed,
        BackupFailed,
        WriteConflict,
        DriftRolledBack,
        DriftRollbackFailed,
    }

    /// <summary>
    /// Result of a transactional patch/revert. Never carries the full configuration —
    /// responses reach model context via MCP, so only field NAMES and the masked diff
    /// (ConfigDiffHelper output: secrets masked, values truncated) are exposed.
    /// </summary>
    public sealed record PatchOutcome(
        bool Success,
        PatchFailure Failure,
        string? Error,
        string? BackupId,
        IReadOnlyCollection<string> AppliedFields,
        Dictionary<string, string>? MaskedDiff,
        IReadOnlyCollection<string>? Drift)
    {
        internal static PatchOutcome Fail(PatchFailure failure, string error, string? backupId = null, IReadOnlyCollection<string>? drift = null)
            => new(false, failure, error, backupId, Array.Empty<string>(), null, drift);
    }

    /// <summary>
    /// Transactional field-level writes against a tenant's configuration row:
    /// fresh ETag read → field gate → patch onto a clone → shared validation →
    /// fail-CLOSED backup → conditional replace (CAS) → fresh re-read →
    /// verify that EXACTLY the intended fields changed → automatic rollback on drift.
    /// <para>
    /// The verify step is not paranoia theatre: with CAS a concurrent writer cannot
    /// corrupt the row, but a Store/Map serialization asymmetry (a field present in one
    /// converter and not the other) silently mangles unrelated fields on every save —
    /// this flow detects that as unexpected drift and rolls back.
    /// </para>
    /// </summary>
    public class TenantConfigPatchService
    {
        internal const int MaxCasAttempts = 3;

        private readonly IConfigRepository _configRepo;
        private readonly IConfigBackupRepository _backupRepo;
        private readonly TenantConfigurationService _configService;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly ILogger<TenantConfigPatchService> _logger;

        public TenantConfigPatchService(
            IConfigRepository configRepo,
            IConfigBackupRepository backupRepo,
            TenantConfigurationService configService,
            IMaintenanceRepository maintenanceRepo,
            ILogger<TenantConfigPatchService> logger)
        {
            _configRepo = configRepo;
            _backupRepo = backupRepo;
            _configService = configService;
            _maintenanceRepo = maintenanceRepo;
            _logger = logger;
        }

        /// <summary>
        /// Fields no caller may write through the patch endpoint. Identity/plumbing, fields
        /// with dedicated endpoints (plan/trial), and system-written provenance:
        /// HomedAppClientId caused the 2026-07-31 prod incident when a stale round-trip
        /// reverted it — only the app-homing flow writes it; LastAuthClientId*/OnboardedBy/
        /// OnboardedAt are AuthFunction-owned; LastUpdated/UpdatedBy are stamped server-side.
        /// </summary>
        internal static readonly HashSet<string> BaseDeniedFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "TenantId", "DomainName", "PartitionKey", "RowKey", "Timestamp", "ETag",
            "LastUpdated", "UpdatedBy", "OnboardedAt", "OnboardedBy",
            "HomedAppClientId", "LastAuthClientId", "LastAuthClientIdSince",
            "PlanTier", "TrialExpiresUtc", "TrialStartedUtc", "TrialConsumed", "TrialGrantedBy",
        };

        /// <summary>
        /// Additional deny-list for non-GA tiers (phase 2): the GA-only toggles the PUT
        /// endpoint silently reverts for tenant admins are an explicit 400 here.
        /// </summary>
        internal static readonly HashSet<string> GaOnlyFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "Disabled", "DisabledReason", "DisabledUntil",
            "AllowInsecureAgentRequests", "BootstrapTokenEnabled",
            "UnrestrictedModeEnabled", "EntraAppRolesEnabled",
            "CustomRateLimitRequestsPerMinute", "CustomUserRateLimitRequestsPerMinute",
            "ValidateDeviceAssociation", "MaxNdjsonPayloadSizeMB",
        };

        /// <summary>
        /// Fields a revert preserves from the CURRENT row unless the caller explicitly opts
        /// into restoring them (GA-only flag): time-traveling plan/trial or app-homing state
        /// via an old snapshot is almost never the intent and was the exact 07-31 failure mode.
        /// TenantId/DomainName are ALWAYS taken from current, flag or not.
        /// </summary>
        internal static readonly HashSet<string> RevertProtectedFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "HomedAppClientId", "LastAuthClientId", "LastAuthClientIdSince",
            "OnboardedBy", "OnboardedAt",
            "PlanTier", "TrialExpiresUtc", "TrialStartedUtc", "TrialConsumed", "TrialGrantedBy",
        };

        private static readonly Dictionary<string, PropertyInfo> ModelProperties =
            typeof(TenantConfiguration)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
                .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

        public async Task<PatchOutcome> ApplyFieldPatchAsync(
            string tenantId, JObject fields, string updatedBy, string source, string? reason,
            TenantConfigCallerTier callerTier = TenantConfigCallerTier.GlobalAdmin)
        {
            if (fields == null || !fields.Properties().Any())
                return PatchOutcome.Fail(PatchFailure.InvalidField, "No fields provided — nothing to patch.");

            // Field gate BEFORE any storage work: reject unknown and non-writable keys with
            // the offending name, so a caller (or a model driving the MCP tool) can self-correct.
            var denied = callerTier == TenantConfigCallerTier.GlobalAdmin
                ? BaseDeniedFields
                : new HashSet<string>(BaseDeniedFields.Concat(GaOnlyFields), StringComparer.OrdinalIgnoreCase);
            foreach (var prop in fields.Properties())
            {
                if (!ModelProperties.ContainsKey(prop.Name))
                    return PatchOutcome.Fail(PatchFailure.InvalidField, $"Unknown field \"{prop.Name}\".");
                if (denied.Contains(prop.Name))
                    return PatchOutcome.Fail(PatchFailure.InvalidField,
                        $"Field \"{prop.Name}\" is not writable via the field patch (identity, plan/trial, or system-owned — use the dedicated endpoint if one exists).");
                if (ContainsRedactedPlaceholder(prop.Value))
                    return PatchOutcome.Fail(PatchFailure.InvalidField,
                        $"Field \"{prop.Name}\" carries the \"{Constants.RedactedSecretPlaceholder}\" placeholder — a redacted read must never be written back. Provide the real value.");
            }

            for (var attempt = 1; attempt <= MaxCasAttempts; attempt++)
            {
                var read = await _configRepo.GetTenantConfigurationWithEtagAsync(tenantId);
                if (read == null)
                    return PatchOutcome.Fail(PatchFailure.NotFound, $"Tenant {tenantId} has no configuration row.");
                var (initial, etag) = read.Value;

                // Patch onto a deep clone; MissingMemberHandling.Error is belt-and-braces on
                // top of the key gate above. JSON null = explicit clear; omitted = untouched;
                // camelCase input binds case-insensitively to the PascalCase model.
                var clone = DeepClone(initial);
                try
                {
                    JsonConvert.PopulateObject(fields.ToString(), clone, new JsonSerializerSettings
                    {
                        MissingMemberHandling = MissingMemberHandling.Error,
                    });
                }
                catch (JsonException ex)
                {
                    return PatchOutcome.Fail(PatchFailure.InvalidField, $"Field patch failed to apply: {ex.Message}");
                }

                // Same normalization as the PUT: never store surrounding whitespace, and an
                // all-whitespace submission clears the field.
                clone.ContactEmail = string.IsNullOrWhiteSpace(clone.ContactEmail) ? null : clone.ContactEmail.Trim();

                // The PUT silently flips UnrestrictedMode off when the GA gate is off. A silent
                // flip would poison the exactly-these-fields verify below, so here the
                // inconsistency is an explicit error instead.
                if (clone.UnrestrictedMode && !clone.UnrestrictedModeEnabled)
                    return PatchOutcome.Fail(PatchFailure.ValidationFailed,
                        "UnrestrictedMode cannot be enabled while UnrestrictedModeEnabled (the GA gate) is off.");

                var validationError = TenantConfigValidation.ValidateModel(
                    clone, initial, isGlobalAdmin: callerTier == TenantConfigCallerTier.GlobalAdmin);
                if (validationError != null)
                    return PatchOutcome.Fail(PatchFailure.ValidationFailed, validationError);

                // Intended change set is COMPUTED, not taken from the request keys: a patched
                // key whose value equals the stored value produces no diff and must not be
                // "expected" by the verify step. The server-side stamps are excluded — the
                // write overwrites them regardless, and the verify treats them as allowed.
                var expected = ConfigPropertyComparer.GetChangedPropertyNames(initial, clone);
                expected.Remove("LastUpdated");
                expected.Remove("UpdatedBy");
                if (expected.Count == 0)
                    return new PatchOutcome(true, PatchFailure.None, null, null,
                        Array.Empty<string>(), new Dictionary<string, string>(), null);

                var outcome = await WriteVerifiedAsync(
                    tenantId, initial, etag, clone, expected, updatedBy, source, reason,
                    auditAction: "PATCH",
                    retriesLeft: MaxCasAttempts - attempt);
                if (outcome != null)
                    return outcome;
                await NextAttemptDelayAsync();
            }

            return PatchOutcome.Fail(PatchFailure.WriteConflict,
                $"Concurrent configuration writes for tenant {tenantId} — {MaxCasAttempts} conditional-write attempts lost the race. Retry shortly.");
        }

        public async Task<PatchOutcome> RevertAsync(
            string tenantId, string? backupId, bool includeProtectedFields,
            string updatedBy, string source, string? reason,
            TenantConfigCallerTier callerTier = TenantConfigCallerTier.GlobalAdmin)
        {
            var backup = backupId != null
                ? await _backupRepo.TryGetAsync(tenantId, backupId)
                : (await _backupRepo.ListByPartitionAsync(tenantId, max: 1)).FirstOrDefault();
            if (backup == null)
                return PatchOutcome.Fail(PatchFailure.NotFound,
                    backupId != null
                        ? $"Backup \"{backupId}\" not found for tenant {tenantId}."
                        : $"Tenant {tenantId} has no configuration backups yet.");

            TenantConfiguration target;
            try
            {
                target = TableConfigRepository.ConvertFromTenantTableEntity(
                    RehydrateTenantConfigEntity(backup.EntityJson, tenantId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Config backup {BackupId} for tenant {TenantId} failed to rehydrate", backup.RowKey, tenantId);
                return PatchOutcome.Fail(PatchFailure.ValidationFailed,
                    $"Backup \"{backup.RowKey}\" could not be rehydrated — not reverting.");
            }

            for (var attempt = 1; attempt <= MaxCasAttempts; attempt++)
            {
                var read = await _configRepo.GetTenantConfigurationWithEtagAsync(tenantId);
                if (read == null)
                    return PatchOutcome.Fail(PatchFailure.NotFound, $"Tenant {tenantId} has no configuration row.");
                var (current, etag) = read.Value;

                var candidate = DeepClone(target);
                // Identity is never restored from a snapshot; protected/system-owned fields
                // only with the explicit GA opt-in.
                candidate.TenantId = current.TenantId;
                candidate.DomainName = current.DomainName;
                if (!includeProtectedFields)
                {
                    foreach (var name in RevertProtectedFields)
                    {
                        var prop = ModelProperties[name];
                        prop.SetValue(candidate, prop.GetValue(current));
                    }
                }

                // A snapshot predating a validation rule must not bypass it on the way back in.
                var validationError = TenantConfigValidation.ValidateModel(
                    candidate, current, isGlobalAdmin: callerTier == TenantConfigCallerTier.GlobalAdmin);
                if (validationError != null)
                    return PatchOutcome.Fail(PatchFailure.ValidationFailed,
                        $"Backup \"{backup.RowKey}\" no longer passes validation: {validationError}");

                // Bookkeeping stamps are excluded: the write re-stamps them anyway, and a
                // snapshot differing ONLY in LastUpdated/UpdatedBy is a no-op revert.
                var expected = ConfigPropertyComparer.GetChangedPropertyNames(current, candidate);
                expected.Remove("LastUpdated");
                expected.Remove("UpdatedBy");
                if (expected.Count == 0)
                    return new PatchOutcome(true, PatchFailure.None, null, null,
                        Array.Empty<string>(), new Dictionary<string, string>(), null);

                var outcome = await WriteVerifiedAsync(
                    tenantId, current, etag, candidate, expected, updatedBy, source,
                    reason ?? $"revert to backup {backup.RowKey}",
                    auditAction: "REVERT",
                    retriesLeft: MaxCasAttempts - attempt);
                if (outcome != null)
                    return outcome;
                await NextAttemptDelayAsync();
            }

            return PatchOutcome.Fail(PatchFailure.WriteConflict,
                $"Concurrent configuration writes for tenant {tenantId} — {MaxCasAttempts} conditional-write attempts lost the race. Retry shortly.");
        }

        /// <summary>
        /// Backup (fail-closed) → stamp → CAS write → cache invalidation → fresh re-read →
        /// exactly-these-fields verify → rollback on drift → audit. Returns null when the CAS
        /// write lost the race and the caller should re-read and retry.
        /// </summary>
        private async Task<PatchOutcome?> WriteVerifiedAsync(
            string tenantId, TenantConfiguration initial, string etag, TenantConfiguration candidate,
            HashSet<string> expected, string updatedBy, string source, string? reason,
            string auditAction, int retriesLeft)
        {
            // Fail-CLOSED backup of the state we are about to replace. The CAS ETag guarantees
            // the row still IS this state when the write lands; a lost race at worst leaves a
            // duplicate snapshot that pruning ages out.
            string backupId;
            try
            {
                var snapshot = TableConfigRepository.BuildBackupEntry(
                    TableConfigRepository.ConvertToTenantTableEntity(initial),
                    tenantId, updatedBy, source, reason,
                    System.Text.Json.JsonSerializer.Serialize(ConfigDiffHelper.GetChanges(initial, candidate)));
                await _backupRepo.UpsertAsync(snapshot);
                backupId = snapshot.RowKey;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fail-closed config backup failed for tenant {TenantId} — refusing to write", tenantId);
                return PatchOutcome.Fail(PatchFailure.BackupFailed,
                    "Backup storage is unavailable — refusing to write without a pre-write snapshot. Retry later.");
            }

            candidate.LastUpdated = DateTime.UtcNow;
            candidate.UpdatedBy = updatedBy;

            var replaced = await _configRepo.TryReplaceTenantConfigurationAsync(candidate, etag);
            _configService.InvalidateCache(tenantId);
            if (!replaced)
            {
                _logger.LogInformation(
                    "Config CAS write for tenant {TenantId} lost the race ({RetriesLeft} retries left)",
                    tenantId, retriesLeft);
                return null; // caller re-reads and retries
            }

            // Fresh re-read via the repo (never the cache) + exactly-these-fields verify.
            var reread = await _configRepo.GetTenantConfigurationWithEtagAsync(tenantId);
            if (reread == null)
            {
                // Row vanished between write and verify (offboarding). Nothing to roll back onto.
                return PatchOutcome.Fail(PatchFailure.DriftRollbackFailed,
                    $"Configuration row for tenant {tenantId} disappeared after the write (offboarding?). Backup {backupId} holds the pre-write state.",
                    backupId);
            }

            var actual = ConfigPropertyComparer.GetChangedPropertyNames(initial, reread.Value.Config);
            var expectedFinal = new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase) { "LastUpdated", "UpdatedBy" };
            // actual must cover every intended field AND introduce nothing beyond them (+stamps).
            var missing = expected.Where(f => !actual.Contains(f)).ToList();
            var unexpected = actual.Where(f => !expectedFinal.Contains(f)).ToList();
            if (missing.Count > 0 || unexpected.Count > 0)
            {
                var drift = missing.Select(f => $"missing:{f}").Concat(unexpected.Select(f => $"unexpected:{f}")).ToList();
                _logger.LogError(
                    "Config write verification FAILED for tenant {TenantId} (action {Action}): {Drift} — rolling back",
                    tenantId, auditAction, string.Join(", ", drift));

                // Roll back to the initial state, conditional on the post-write ETag so a
                // concurrent writer that slipped in after our write is never clobbered.
                var rolledBack = await _configRepo.TryReplaceTenantConfigurationAsync(initial, reread.Value.ETag);
                _configService.InvalidateCache(tenantId);
                if (!rolledBack)
                    return PatchOutcome.Fail(PatchFailure.DriftRollbackFailed,
                        $"Write verification failed ({string.Join(", ", drift)}) and the rollback lost a concurrent-write race. Restore manually from backup {backupId}.",
                        backupId, drift);

                return PatchOutcome.Fail(PatchFailure.DriftRolledBack,
                    $"Write verification failed — the row changed beyond the intended fields ({string.Join(", ", drift)}). The write was rolled back; nothing persisted. This usually means Store/Map serialization drift — investigate before retrying.",
                    backupId, drift);
            }

            var maskedDiff = ConfigDiffHelper.GetChanges(initial, reread.Value.Config);
            var auditDetails = new Dictionary<string, string>(maskedDiff) { ["BackupId"] = backupId };
            await _maintenanceRepo.LogAuditEntryAsync(
                tenantId, auditAction, "TenantConfiguration", tenantId, updatedBy, auditDetails);

            var applied = expected.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            return new PatchOutcome(true, PatchFailure.None, null, backupId, applied, maskedDiff, null);
        }

        /// <summary>
        /// Schema-aware rehydration of a raw EntityJson snapshot back into a TableEntity.
        /// Strings are converted to DateTime ONLY for columns the model declares as DateTime —
        /// an arbitrary string field that merely looks like a date stays a string. Numbers are
        /// forced to double for columns the model declares as decimal/double/float: JSON cannot
        /// distinguish 95.0 from 95, so a whole-valued double column would otherwise rehydrate
        /// as Int32 and TableEntity.GetDouble throws on the type mismatch (prod finding
        /// 2026-08-03, SLA rate columns).
        /// </summary>
        internal static TableEntity RehydrateTenantConfigEntity(string entityJson, string tenantId)
        {
            var dateColumns = ModelProperties.Values
                .Where(p => p.PropertyType == typeof(DateTime) || p.PropertyType == typeof(DateTime?))
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var doubleColumns = ModelProperties.Values
                .Where(p =>
                {
                    var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                    return t == typeof(decimal) || t == typeof(double) || t == typeof(float);
                })
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            using var doc = System.Text.Json.JsonDocument.Parse(entityJson);
            var entity = new TableEntity(tenantId, "config");
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name is "PartitionKey" or "RowKey") continue;
                entity[prop.Name] = prop.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.Null => null,
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    System.Text.Json.JsonValueKind.Number =>
                        doubleColumns.Contains(prop.Name) ? prop.Value.GetDouble()
                        : prop.Value.TryGetInt32(out var i) ? i
                        : prop.Value.TryGetInt64(out var l) ? (object)l
                        : prop.Value.GetDouble(),
                    System.Text.Json.JsonValueKind.String =>
                        dateColumns.Contains(prop.Name) && prop.Value.TryGetDateTimeOffset(out var dto)
                            ? dto.UtcDateTime
                            : prop.Value.GetString(),
                    _ => prop.Value.GetRawText(),
                };
            }
            return entity;
        }

        private static bool ContainsRedactedPlaceholder(JToken value)
            => value.Type == JTokenType.String
               && ((string?)value)?.Contains(Constants.RedactedSecretPlaceholder, StringComparison.Ordinal) == true;

        private static TenantConfiguration DeepClone(TenantConfiguration config)
            => JsonConvert.DeserializeObject<TenantConfiguration>(JsonConvert.SerializeObject(config))!;

        // Tiny spacing between CAS retries; keeps the loop honest without a TimeProvider dance.
        private static Task NextAttemptDelayAsync() => Task.Delay(TimeSpan.FromMilliseconds(150));
    }
}
