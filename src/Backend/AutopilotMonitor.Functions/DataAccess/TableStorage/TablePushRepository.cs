using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage;

/// <summary>
/// Table-storage implementation of <see cref="IPushDeviceRepository"/> over the four push tables.
/// Unlike the older helpers this repository propagates storage exceptions: every row here is a
/// credential, and a swallowed write failure would leave a phone silently unpaired or a code
/// silently reusable. Callers wrap what must be fail-soft.
/// </summary>
public class TablePushRepository : IPushDeviceRepository
{
    private const string GrantPartition = "grant";
    private const int CasRetries = 4;

    private readonly TableClient _devices;
    private readonly TableClient _grants;
    private readonly TableClient _owners;
    private readonly TableClient _watches;
    private readonly ILogger<TablePushRepository> _logger;

    public TablePushRepository(TableStorageService storage, ILogger<TablePushRepository> logger)
    {
        _devices = storage.GetTableClient(Constants.TableNames.PushDevices);
        _grants = storage.GetTableClient(Constants.TableNames.PushPairingGrants);
        _owners = storage.GetTableClient(Constants.TableNames.PushOwners);
        _watches = storage.GetTableClient(Constants.TableNames.PushSessionWatches);
        _logger = logger;
    }

    // ── Devices ─────────────────────────────────────────────────────────────

    public async Task<PushDevice?> GetDeviceAsync(string scope, string deviceId)
    {
        try
        {
            var entity = await _devices.GetEntityAsync<TableEntity>(scope, deviceId).ConfigureAwait(false);
            return MapDevice(entity.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<List<PushDevice>> GetDevicesAsync(string scope)
    {
        var result = new List<PushDevice>();
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {scope}");
        await foreach (var entity in _devices.QueryAsync<TableEntity>(filter).ConfigureAwait(false))
            result.Add(MapDevice(entity));
        return result;
    }

    public Task AddDeviceAsync(PushDevice device)
        => _devices.AddEntityAsync(StoreDevice(device));

    public async Task<bool> MutateDeviceAsync(
        string scope, string deviceId, Func<PushDevice, IReadOnlyDictionary<string, object?>?> patch)
    {
        for (var attempt = 1; attempt <= CasRetries; attempt++)
        {
            TableEntity read;
            try
            {
                read = (await _devices.GetEntityAsync<TableEntity>(scope, deviceId).ConfigureAwait(false)).Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }

            var changes = patch(MapDevice(read));
            if (changes == null || changes.Count == 0)
                return false;

            // A Merge ignores null-valued properties (the service cannot clear a column that way),
            // so a cleared StatusReason is written as "" and mapped back to null; other nulls are
            // dropped from the patch (nothing to say).
            var merge = new TableEntity(scope, deviceId);
            foreach (var (key, value) in changes)
            {
                if (value == null)
                {
                    if (key == "StatusReason")
                        merge[key] = string.Empty;
                    continue;
                }
                merge[key] = value is DateTime dt ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : value;
            }

            try
            {
                await _devices.UpdateEntityAsync(merge, read.ETag, TableUpdateMode.Merge).ConfigureAwait(false);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // Lost the CAS race: re-read and try again; the last loss falls through to "no write landed".
                if (attempt < CasRetries)
                {
                    _logger.LogInformation("Push device {Scope}/{DeviceId} changed concurrently; retrying", scope, deviceId);
                    await Task.Delay(30 * attempt).ConfigureAwait(false);
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        _logger.LogWarning("Push device {Scope}/{DeviceId} lost the CAS race {Retries} times", scope, deviceId, CasRetries);
        return false;
    }

    public async Task DeleteDeviceAsync(string scope, string deviceId)
    {
        try
        {
            await _devices.DeleteEntityAsync(scope, deviceId).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    public async Task<List<PushDevice>> GetAllDevicesAsync()
    {
        var result = new List<PushDevice>();
        await foreach (var entity in _devices.QueryAsync<TableEntity>().ConfigureAwait(false))
            result.Add(MapDevice(entity));
        return result;
    }

    // ── Grants ──────────────────────────────────────────────────────────────

    public async Task<PushPairingGrant?> GetGrantAsync(string codeHash)
    {
        try
        {
            var entity = await _grants.GetEntityAsync<TableEntity>(GrantPartition, codeHash).ConfigureAwait(false);
            return MapGrant(entity.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public Task AddGrantAsync(PushPairingGrant grant)
        => _grants.AddEntityAsync(StoreGrant(grant));

    public async Task<bool> TryUpdateGrantAsync(PushPairingGrant grant)
    {
        if (string.IsNullOrEmpty(grant.ETag))
            throw new ArgumentException("A grant update needs the ETag it was read with.", nameof(grant));

        try
        {
            await _grants.UpdateEntityAsync(StoreGrant(grant), new ETag(grant.ETag), TableUpdateMode.Replace).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 412 || ex.Status == 404)
        {
            return false;
        }
    }

    public async Task DeleteGrantAsync(string codeHash)
    {
        try
        {
            await _grants.DeleteEntityAsync(GrantPartition, codeHash).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    public async Task<List<PushPairingGrant>> GetExpiredGrantsAsync(DateTime cutoffUtc)
    {
        var result = new List<PushPairingGrant>();
        var cutoff = new DateTimeOffset(DateTime.SpecifyKind(cutoffUtc, DateTimeKind.Utc));
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {GrantPartition} and ExpiresUtc lt {cutoff}");
        await foreach (var entity in _grants.QueryAsync<TableEntity>(filter).ConfigureAwait(false))
            result.Add(MapGrant(entity));
        return result;
    }

    // ── Owners ──────────────────────────────────────────────────────────────

    public async Task<PushOwner?> GetOwnerAsync(string homeTenantId, string objectId)
    {
        try
        {
            var entity = await _owners.GetEntityAsync<TableEntity>(homeTenantId.ToLowerInvariant(), objectId.ToLowerInvariant()).ConfigureAwait(false);
            return new PushOwner
            {
                HomeTenantId = entity.Value.PartitionKey,
                ObjectId = entity.Value.RowKey,
                Upn = entity.Value.GetString("Upn") ?? string.Empty,
                LastSignInUtc = entity.Value.GetDateTime("LastSignInUtc") ?? DateTime.MinValue,
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public Task StampOwnerSignInAsync(string homeTenantId, string objectId, string upn, DateTime signInUtc)
        => _owners.UpsertEntityAsync(new TableEntity(homeTenantId.ToLowerInvariant(), objectId.ToLowerInvariant())
        {
            ["Upn"] = upn.ToLowerInvariant(),
            ["LastSignInUtc"] = DateTime.SpecifyKind(signInUtc, DateTimeKind.Utc),
        }, TableUpdateMode.Merge);

    // ── Watches ─────────────────────────────────────────────────────────────

    private static string WatchRowKey(string sessionId, string objectId) => $"{sessionId}_{objectId.ToLowerInvariant()}";

    public Task UpsertWatchAsync(PushSessionWatch watch)
        => _watches.UpsertEntityAsync(StoreWatch(watch), TableUpdateMode.Replace);

    public async Task DeleteWatchAsync(string tenantId, string sessionId, string objectId)
    {
        try
        {
            await _watches.DeleteEntityAsync(tenantId.ToLowerInvariant(), WatchRowKey(sessionId, objectId)).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    public async Task<PushSessionWatch?> GetWatchAsync(string tenantId, string sessionId, string objectId)
    {
        try
        {
            var entity = await _watches.GetEntityAsync<TableEntity>(tenantId.ToLowerInvariant(), WatchRowKey(sessionId, objectId)).ConfigureAwait(false);
            return MapWatch(entity.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<List<PushSessionWatch>> GetWatchesForSessionAsync(string tenantId, string sessionId)
    {
        var result = new List<PushSessionWatch>();
        var lower = sessionId + "_";
        var upper = sessionId + "`";
        var filter = TableClient.CreateQueryFilter(
            $"PartitionKey eq {tenantId.ToLowerInvariant()} and RowKey ge {lower} and RowKey lt {upper}");
        await foreach (var entity in _watches.QueryAsync<TableEntity>(filter).ConfigureAwait(false))
            result.Add(MapWatch(entity));
        return result.OrderByDescending(w => w.CreatedUtc).ToList();
    }

    public async Task<List<PushSessionWatch>> GetExpiredWatchesAsync(DateTime cutoffUtc)
    {
        var result = new List<PushSessionWatch>();
        var cutoff = new DateTimeOffset(DateTime.SpecifyKind(cutoffUtc, DateTimeKind.Utc));
        var filter = TableClient.CreateQueryFilter($"ExpiresUtc lt {cutoff}");
        await foreach (var entity in _watches.QueryAsync<TableEntity>(filter).ConfigureAwait(false))
            result.Add(MapWatch(entity));
        return result;
    }

    // ── Store / Map (memory: feedback_table_storage_serialization) ──────────
    // Internal so the round-trip test can prove every property survives Store → Map.

    internal static TableEntity StoreDevice(PushDevice d) =>
        new(d.Scope, d.DeviceId)
        {
            ["OwnerUpn"] = d.OwnerUpn,
            ["OwnerObjectId"] = d.OwnerObjectId,
            ["OwnerHomeTenantId"] = d.OwnerHomeTenantId,
            ["Kind"] = d.Kind,
            ["Endpoint"] = d.Endpoint,
            ["P256dh"] = d.P256dh,
            ["Auth"] = d.Auth,
            ["DeviceSecretHash"] = d.DeviceSecretHash,
            ["VapidKid"] = d.VapidKid,
            ["Label"] = d.Label,
            ["Platform"] = d.Platform,
            ["AppVersion"] = d.AppVersion,
            ["Status"] = d.Status,
            ["StatusReason"] = d.StatusReason,
            ["PairedUtc"] = DateTime.SpecifyKind(d.PairedUtc, DateTimeKind.Utc),
            ["ConfirmedUtc"] = Utc(d.ConfirmedUtc),
            ["LastDeliveredUtc"] = Utc(d.LastDeliveredUtc),
            ["LastOpenedUtc"] = Utc(d.LastOpenedUtc),
            ["StatusChangedUtc"] = Utc(d.StatusChangedUtc),
            ["LastStatusCode"] = d.LastStatusCode,
            ["ConsecutiveFailures"] = d.ConsecutiveFailures,
            ["WindowStartUtc"] = Utc(d.WindowStartUtc),
            ["WindowCount"] = d.WindowCount,
            ["SuppressedCount"] = d.SuppressedCount,
        };

    internal static PushDevice MapDevice(TableEntity e) => new()
    {
        Scope = e.PartitionKey,
        DeviceId = e.RowKey,
        OwnerUpn = e.GetString("OwnerUpn") ?? string.Empty,
        OwnerObjectId = e.GetString("OwnerObjectId") ?? string.Empty,
        OwnerHomeTenantId = e.GetString("OwnerHomeTenantId") ?? string.Empty,
        Kind = e.GetString("Kind") ?? "webpush",
        Endpoint = e.GetString("Endpoint") ?? string.Empty,
        P256dh = e.GetString("P256dh") ?? string.Empty,
        Auth = e.GetString("Auth") ?? string.Empty,
        DeviceSecretHash = e.GetString("DeviceSecretHash") ?? string.Empty,
        VapidKid = e.GetString("VapidKid") ?? string.Empty,
        Label = e.GetString("Label") ?? string.Empty,
        Platform = e.GetString("Platform") ?? "other",
        AppVersion = e.GetString("AppVersion") ?? string.Empty,
        Status = e.GetString("Status") ?? Constants.Push.DeviceStatus.Pending,
        StatusReason = string.IsNullOrEmpty(e.GetString("StatusReason")) ? null : e.GetString("StatusReason"),
        PairedUtc = e.GetDateTime("PairedUtc") ?? DateTime.MinValue,
        ConfirmedUtc = e.GetDateTime("ConfirmedUtc"),
        LastDeliveredUtc = e.GetDateTime("LastDeliveredUtc"),
        LastOpenedUtc = e.GetDateTime("LastOpenedUtc"),
        StatusChangedUtc = e.GetDateTime("StatusChangedUtc"),
        LastStatusCode = e.GetInt32("LastStatusCode"),
        ConsecutiveFailures = e.GetInt32("ConsecutiveFailures") ?? 0,
        WindowStartUtc = e.GetDateTime("WindowStartUtc"),
        WindowCount = e.GetInt32("WindowCount") ?? 0,
        SuppressedCount = e.GetInt32("SuppressedCount") ?? 0,
    };

    internal static TableEntity StoreGrant(PushPairingGrant g) =>
        new(GrantPartition, g.CodeHash)
        {
            ["TenantId"] = g.TenantId,
            ["OwnerUpn"] = g.OwnerUpn,
            ["OwnerObjectId"] = g.OwnerObjectId,
            ["OwnerHomeTenantId"] = g.OwnerHomeTenantId,
            ["Status"] = g.Status,
            ["CreatedUtc"] = DateTime.SpecifyKind(g.CreatedUtc, DateTimeKind.Utc),
            ["ExpiresUtc"] = DateTime.SpecifyKind(g.ExpiresUtc, DateTimeKind.Utc),
            ["RedeemedUtc"] = Utc(g.RedeemedUtc),
            ["DeviceId"] = g.DeviceId,
            ["FailedAttempts"] = g.FailedAttempts,
        };

    internal static PushPairingGrant MapGrant(TableEntity e) => new()
    {
        CodeHash = e.RowKey,
        TenantId = e.GetString("TenantId") ?? string.Empty,
        OwnerUpn = e.GetString("OwnerUpn") ?? string.Empty,
        OwnerObjectId = e.GetString("OwnerObjectId") ?? string.Empty,
        OwnerHomeTenantId = e.GetString("OwnerHomeTenantId") ?? string.Empty,
        Status = e.GetString("Status") ?? Constants.Push.PairingStatus.Pending,
        CreatedUtc = e.GetDateTime("CreatedUtc") ?? DateTime.MinValue,
        ExpiresUtc = e.GetDateTime("ExpiresUtc") ?? DateTime.MinValue,
        RedeemedUtc = e.GetDateTime("RedeemedUtc"),
        DeviceId = e.GetString("DeviceId"),
        FailedAttempts = e.GetInt32("FailedAttempts") ?? 0,
        ETag = e.ETag.ToString(),
    };

    internal static TableEntity StoreWatch(PushSessionWatch w) =>
        new(w.TenantId.ToLowerInvariant(), WatchRowKey(w.SessionId, w.OwnerObjectId))
        {
            ["SessionId"] = w.SessionId,
            ["OwnerObjectId"] = w.OwnerObjectId.ToLowerInvariant(),
            ["OwnerUpn"] = w.OwnerUpn.ToLowerInvariant(),
            ["OwnerScopes"] = w.OwnerScopes,
            ["CreatedUtc"] = DateTime.SpecifyKind(w.CreatedUtc, DateTimeKind.Utc),
            ["ExpiresUtc"] = DateTime.SpecifyKind(w.ExpiresUtc, DateTimeKind.Utc),
        };

    internal static PushSessionWatch MapWatch(TableEntity e) => new()
    {
        TenantId = e.PartitionKey,
        SessionId = e.GetString("SessionId") ?? string.Empty,
        OwnerObjectId = e.GetString("OwnerObjectId") ?? string.Empty,
        OwnerUpn = e.GetString("OwnerUpn") ?? string.Empty,
        OwnerScopes = e.GetString("OwnerScopes") ?? string.Empty,
        CreatedUtc = e.GetDateTime("CreatedUtc") ?? DateTime.MinValue,
        ExpiresUtc = e.GetDateTime("ExpiresUtc") ?? DateTime.MinValue,
    };

    // Edm.DateTime cannot hold default(DateTime); nullable columns stay absent (null) instead.
    private static DateTime? Utc(DateTime? value)
        => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;
}
