using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>
/// In-memory <see cref="IPushDeviceRepository"/> with the one storage semantic the push flow
/// depends on: the grant ETag. <see cref="TryUpdateGrantAsync"/> succeeds only with the ETag the
/// grant was read with, so the one-shot redeem race is testable.
/// </summary>
internal sealed class InMemoryPushDeviceRepository : IPushDeviceRepository
{
    public Dictionary<(string Scope, string DeviceId), PushDevice> Devices { get; } = new();
    public Dictionary<string, (PushPairingGrant Grant, int Version)> Grants { get; } = new();
    public Dictionary<(string Tenant, string Oid), PushOwner> Owners { get; } = new();
    public Dictionary<(string Tenant, string Session, string Oid), PushSessionWatch> Watches { get; } = new();

    public Task<PushDevice?> GetDeviceAsync(string scope, string deviceId)
        => Task.FromResult(Devices.TryGetValue((scope, deviceId), out var d) ? Clone(d) : null);

    public Task<List<PushDevice>> GetDevicesAsync(string scope)
        => Task.FromResult(Devices.Values.Where(d => d.Scope == scope).Select(Clone).ToList());

    public Task AddDeviceAsync(PushDevice device)
    {
        if (Devices.ContainsKey((device.Scope, device.DeviceId)))
            throw new InvalidOperationException("duplicate device");
        Devices[(device.Scope, device.DeviceId)] = Clone(device);
        return Task.CompletedTask;
    }

    public Task<bool> MutateDeviceAsync(string scope, string deviceId, Func<PushDevice, IReadOnlyDictionary<string, object?>?> patch)
    {
        if (!Devices.TryGetValue((scope, deviceId), out var current))
            return Task.FromResult(false);
        var changes = patch(Clone(current));
        if (changes == null || changes.Count == 0)
            return Task.FromResult(false);
        foreach (var (key, value) in changes)
            Apply(current, key, value);
        return Task.FromResult(true);
    }

    public Task DeleteDeviceAsync(string scope, string deviceId)
    {
        Devices.Remove((scope, deviceId));
        return Task.CompletedTask;
    }

    public Task<PushPairingGrant?> GetGrantAsync(string codeHash)
    {
        if (!Grants.TryGetValue(codeHash, out var entry))
            return Task.FromResult<PushPairingGrant?>(null);
        var copy = Clone(entry.Grant);
        copy.ETag = entry.Version.ToString();
        return Task.FromResult<PushPairingGrant?>(copy);
    }

    public Task AddGrantAsync(PushPairingGrant grant)
    {
        Grants[grant.CodeHash] = (Clone(grant), 1);
        return Task.CompletedTask;
    }

    /// <summary>Runs once before the next grant update — a concurrent writer between read and write.</summary>
    public Action? BeforeGrantUpdate { get; set; }

    /// <summary>Bumps the stored version of a grant as a concurrent writer would.</summary>
    public void TouchGrant(string codeHash)
    {
        if (Grants.TryGetValue(codeHash, out var entry))
            Grants[codeHash] = (entry.Grant, entry.Version + 1);
    }

    public Task<bool> TryUpdateGrantAsync(PushPairingGrant grant)
    {
        var hook = BeforeGrantUpdate;
        BeforeGrantUpdate = null;
        hook?.Invoke();

        if (!Grants.TryGetValue(grant.CodeHash, out var entry) || entry.Version.ToString() != grant.ETag)
            return Task.FromResult(false);
        Grants[grant.CodeHash] = (Clone(grant), entry.Version + 1);
        return Task.FromResult(true);
    }

    public Task DeleteGrantAsync(string codeHash)
    {
        Grants.Remove(codeHash);
        return Task.CompletedTask;
    }

    public Task<List<PushPairingGrant>> GetExpiredGrantsAsync(DateTime cutoffUtc)
        => Task.FromResult(Grants.Values.Where(g => g.Grant.ExpiresUtc < cutoffUtc).Select(g => Clone(g.Grant)).ToList());

    public Task<PushOwner?> GetOwnerAsync(string homeTenantId, string objectId)
        => Task.FromResult(Owners.TryGetValue((homeTenantId.ToLowerInvariant(), objectId.ToLowerInvariant()), out var o) ? o : null);

    public Task StampOwnerSignInAsync(string homeTenantId, string objectId, string upn, DateTime signInUtc)
    {
        Owners[(homeTenantId.ToLowerInvariant(), objectId.ToLowerInvariant())] = new PushOwner
        {
            HomeTenantId = homeTenantId.ToLowerInvariant(), ObjectId = objectId.ToLowerInvariant(), Upn = upn, LastSignInUtc = signInUtc,
        };
        return Task.CompletedTask;
    }

    public Task UpsertWatchAsync(PushSessionWatch watch)
    {
        Watches[(watch.TenantId, watch.SessionId, watch.OwnerObjectId)] = watch;
        return Task.CompletedTask;
    }

    public Task DeleteWatchAsync(string tenantId, string sessionId, string objectId)
    {
        Watches.Remove((tenantId, sessionId, objectId));
        return Task.CompletedTask;
    }

    public Task<PushSessionWatch?> GetWatchAsync(string tenantId, string sessionId, string objectId)
        => Task.FromResult(Watches.TryGetValue((tenantId, sessionId, objectId), out var w) ? w : null);

    public Task<List<PushSessionWatch>> GetWatchesForSessionAsync(string tenantId, string sessionId)
        => Task.FromResult(Watches.Values.Where(w => w.TenantId == tenantId && w.SessionId == sessionId).ToList());

    public Task<List<PushDevice>> GetAllDevicesAsync()
        => Task.FromResult(Devices.Values.Select(Clone).ToList());

    public Task<List<PushSessionWatch>> GetExpiredWatchesAsync(DateTime cutoffUtc)
        => Task.FromResult(Watches.Values.Where(w => w.ExpiresUtc < cutoffUtc).ToList());

    private static void Apply(PushDevice d, string key, object? value)
    {
        // Mirrors Table Storage Merge semantics: a null never clears a column; the production
        // repository writes "" for a cleared StatusReason, which maps back to null.
        if (value == null)
        {
            if (key == "StatusReason")
                d.StatusReason = null;
            return;
        }

        switch (key)
        {
            case "Status": d.Status = (string)value!; break;
            case "StatusReason": d.StatusReason = string.IsNullOrEmpty((string?)value) ? null : (string?)value; break;
            case "StatusChangedUtc": d.StatusChangedUtc = (DateTime?)value; break;
            case "ConfirmedUtc": d.ConfirmedUtc = (DateTime?)value; break;
            case "LastDeliveredUtc": d.LastDeliveredUtc = (DateTime?)value; break;
            case "LastOpenedUtc": d.LastOpenedUtc = (DateTime?)value; break;
            case "LastStatusCode": d.LastStatusCode = (int?)value; break;
            case "ConsecutiveFailures": d.ConsecutiveFailures = (int)value!; break;
            case "WindowStartUtc": d.WindowStartUtc = (DateTime?)value; break;
            case "WindowCount": d.WindowCount = (int)value!; break;
            case "SuppressedCount": d.SuppressedCount = (int)value!; break;
            case "Endpoint": d.Endpoint = (string)value!; break;
            case "P256dh": d.P256dh = (string)value!; break;
            case "Auth": d.Auth = (string)value!; break;
            case "VapidKid": d.VapidKid = (string)value!; break;
            default: throw new InvalidOperationException($"unexpected patch key {key}");
        }
    }

    private static PushDevice Clone(PushDevice d) => (PushDevice)d.MemberwiseCloneShim();
    private static PushPairingGrant Clone(PushPairingGrant g) => (PushPairingGrant)g.MemberwiseCloneShim();
}

internal static class ShallowClone
{
    public static object MemberwiseCloneShim(this object source)
    {
        var type = source.GetType();
        var copy = Activator.CreateInstance(type)!;
        foreach (var p in type.GetProperties().Where(p => p.CanRead && p.CanWrite))
            p.SetValue(copy, p.GetValue(source));
        return copy;
    }
}
