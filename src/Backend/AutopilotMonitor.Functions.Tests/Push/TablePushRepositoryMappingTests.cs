using System.Reflection;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure;
using Azure.Data.Tables;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>
/// Store → Map round trip of <see cref="TablePushRepository"/> for the three models it projects
/// (<see cref="PushDevice"/>, <see cref="PushPairingGrant"/>, <see cref="PushSessionWatch"/>;
/// <see cref="PushOwner"/> is written inline, not through a Store/Map pair). Every row is a
/// credential or binds one, so a column dropped on either side is a silently unpaired phone or a
/// reusable code. The comparisons run by reflection over the model's public properties: a property
/// added later is covered without touching this file, and the fixture guard fails until the fixture
/// carries a distinct non-default value for it.
/// </summary>
public class TablePushRepositoryMappingTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string OwnerHomeTenantId = "22222222-2222-2222-2222-222222222222";
    private const string SessionId = "33333333-3333-3333-3333-333333333333";
    private const string DeviceId = "dddddddd-0000-0000-0000-000000000001";
    private const string OwnerObjectId = "aaaaaaaa-0000-0000-0000-000000000001";
    private static readonly DateTime Anchor = new(2026, 10, 7, 8, 30, 0, DateTimeKind.Utc);

    // Documented key mappings: model property → entity key, where the value does not live in a column of its own name.
    private static readonly Dictionary<string, string> DeviceKeys = new() { ["Scope"] = "PartitionKey", ["DeviceId"] = "RowKey" };
    private static readonly Dictionary<string, string> GrantKeys = new() { ["CodeHash"] = "RowKey" };
    private static readonly Dictionary<string, string> WatchKeys = new() { ["TenantId"] = "PartitionKey" };

    // ── Devices ─────────────────────────────────────────────────────────────

    [Fact]
    public void Device_round_trip_preserves_every_property()
    {
        var device = FullDevice();

        var mapped = TablePushRepository.MapDevice(TablePushRepository.StoreDevice(device));

        AssertSameProperties(device, mapped);
    }

    [Fact]
    public void Device_fixture_sets_every_property_to_a_distinct_non_default_value()
        => AssertFixtureIsDistinctAndNonDefault(FullDevice());

    [Fact]
    public void Device_store_writes_every_property_as_a_column_or_key()
    {
        var device = FullDevice();

        var entity = TablePushRepository.StoreDevice(device);

        Assert.Equal(device.Scope, entity.PartitionKey);
        Assert.Equal(device.DeviceId, entity.RowKey);
        AssertEveryPropertyIsStored<PushDevice>(entity, DeviceKeys);
    }

    [Fact]
    public void Device_store_labels_every_date_utc_without_moving_it()
    {
        var input = WithUnspecifiedKinds(FullDevice());

        var entity = TablePushRepository.StoreDevice(input);
        var mapped = TablePushRepository.MapDevice(entity);

        AssertDatesStoredAsUtc(input, entity, mapped);
    }

    [Fact]
    public void Device_status_reason_cleared_to_empty_string_maps_to_null()
    {
        // A Merge cannot clear a column, so MutateDeviceAsync writes "" for a cleared reason.
        var entity = TablePushRepository.StoreDevice(FullDevice());
        entity["StatusReason"] = string.Empty;

        Assert.Null(TablePushRepository.MapDevice(entity).StatusReason);
    }

    [Fact]
    public void Device_without_status_reason_column_maps_to_null()
    {
        var entity = new TableEntity(TenantId, DeviceId);

        Assert.Null(TablePushRepository.MapDevice(entity).StatusReason);
    }

    [Theory]
    [InlineData("gone")]
    [InlineData("vapid_key_mismatch")]
    [InlineData("invalid_subscription")]
    [InlineData("owner_inactive")]
    [InlineData("delivery_failures")]
    public void Device_set_status_reason_maps_verbatim(string reason)
    {
        var entity = new TableEntity(TenantId, DeviceId) { ["StatusReason"] = reason };

        Assert.Equal(reason, TablePushRepository.MapDevice(entity).StatusReason);
    }

    [Fact]
    public void Device_absent_nullable_columns_map_to_null()
    {
        var mapped = TablePushRepository.MapDevice(new TableEntity(TenantId, DeviceId));

        var nullable = PublicProperties<PushDevice>().Where(p => Nullable.GetUnderlyingType(p.PropertyType) != null).ToList();
        Assert.NotEmpty(nullable);
        var populated = nullable.Where(p => p.GetValue(mapped) != null).Select(p => p.Name).ToList();
        Assert.Empty(populated);
    }

    [Fact]
    public void Device_without_optional_values_round_trips_the_nulls()
    {
        // Edm.DateTime cannot hold default(DateTime): a null stays null through Store (the Utc helper) and Map.
        var device = FullDevice();
        foreach (var p in PublicProperties<PushDevice>().Where(p => Nullable.GetUnderlyingType(p.PropertyType) != null))
            p.SetValue(device, null);
        device.StatusReason = null;

        var mapped = TablePushRepository.MapDevice(TablePushRepository.StoreDevice(device));

        AssertSameProperties(device, mapped);
    }

    [Fact]
    public void Device_row_without_columns_maps_to_a_pending_device_with_model_defaults()
    {
        var mapped = TablePushRepository.MapDevice(new TableEntity(TenantId, DeviceId));

        Assert.Equal(TenantId, mapped.Scope);
        Assert.Equal(DeviceId, mapped.DeviceId);
        // A row without a Status column is Pending — it must never become Active by omission.
        Assert.Equal(Constants.Push.DeviceStatus.Pending, mapped.Status);
        Assert.Equal(new PushDevice().Kind, mapped.Kind);
        Assert.Equal(new PushDevice().Platform, mapped.Platform);
        Assert.Equal(string.Empty, mapped.OwnerUpn);
        Assert.Equal(DateTime.MinValue, mapped.PairedUtc);
        Assert.Equal(0, mapped.ConsecutiveFailures);
        Assert.Equal(0, mapped.WindowCount);
        Assert.Equal(0, mapped.SuppressedCount);
    }

    // ── Grants ──────────────────────────────────────────────────────────────

    [Fact]
    public void Grant_round_trip_preserves_every_property_except_the_etag()
    {
        var grant = FullGrant();

        var entity = TablePushRepository.StoreGrant(grant);
        var mapped = TablePushRepository.MapGrant(entity);

        // ETag is the storage concurrency token: Map takes it from the entity, which a freshly built
        // TableEntity does not carry, so it cannot equal the input (see Grant_etag_comes_from_the_entity…).
        AssertSameProperties(grant, mapped, nameof(PushPairingGrant.ETag));
        Assert.Equal(entity.ETag.ToString(), mapped.ETag);
    }

    [Fact]
    public void Grant_fixture_sets_every_property_to_a_distinct_non_default_value()
        => AssertFixtureIsDistinctAndNonDefault(FullGrant());

    [Fact]
    public void Grant_store_writes_every_property_as_a_column_or_key()
    {
        var grant = FullGrant();

        var entity = TablePushRepository.StoreGrant(grant);

        Assert.Equal("grant", entity.PartitionKey);
        Assert.Equal(grant.CodeHash, entity.RowKey);
        // ETag is never a column: TryUpdateGrantAsync sends it as the If-Match condition.
        AssertEveryPropertyIsStored<PushPairingGrant>(entity, GrantKeys, nameof(PushPairingGrant.ETag));
        Assert.False(entity.ContainsKey(nameof(PushPairingGrant.ETag)));
    }

    [Fact]
    public void Grant_etag_comes_from_the_entity_not_from_the_stored_grant()
    {
        var grant = FullGrant();   // carries the ETag of an earlier read
        var entity = TablePushRepository.StoreGrant(grant);
        entity.ETag = new ETag("W/\"datetime'2026-10-07T08%3A31%3A00.0000000Z'\"");

        var mapped = TablePushRepository.MapGrant(entity);

        Assert.Equal(entity.ETag.ToString(), mapped.ETag);
        Assert.NotEqual(grant.ETag, mapped.ETag);
    }

    [Fact]
    public void Grant_without_redeem_round_trips_null_redeemed_and_device_id()
    {
        var grant = FullGrant();
        grant.Status = Constants.Push.PairingStatus.Pending;
        grant.RedeemedUtc = null;
        grant.DeviceId = null;

        var mapped = TablePushRepository.MapGrant(TablePushRepository.StoreGrant(grant));

        Assert.Null(mapped.RedeemedUtc);
        Assert.Null(mapped.DeviceId);
        AssertSameProperties(grant, mapped, nameof(PushPairingGrant.ETag));
    }

    [Fact]
    public void Grant_store_labels_every_date_utc_without_moving_it()
    {
        var input = WithUnspecifiedKinds(FullGrant());

        var entity = TablePushRepository.StoreGrant(input);
        var mapped = TablePushRepository.MapGrant(entity);

        AssertDatesStoredAsUtc(input, entity, mapped);
    }

    // ── Watches ─────────────────────────────────────────────────────────────

    [Fact]
    public void Watch_round_trip_preserves_every_property()
    {
        // Lower-case inputs: TenantId, OwnerObjectId and OwnerUpn are lower-cased on store (dedicated test below).
        var watch = FullWatch();

        var mapped = TablePushRepository.MapWatch(TablePushRepository.StoreWatch(watch));

        AssertSameProperties(watch, mapped);
    }

    [Fact]
    public void Watch_fixture_sets_every_property_to_a_distinct_non_default_value()
        => AssertFixtureIsDistinctAndNonDefault(FullWatch());

    [Fact]
    public void Watch_store_writes_every_property_as_a_column_or_key()
    {
        var watch = FullWatch();

        var entity = TablePushRepository.StoreWatch(watch);

        Assert.Equal(watch.TenantId, entity.PartitionKey);
        // SessionId and OwnerObjectId compose the RowKey (point read and per-session range scan) and
        // are stored as columns as well, so Map never has to split the key.
        Assert.Equal($"{watch.SessionId}_{watch.OwnerObjectId}", entity.RowKey);
        AssertEveryPropertyIsStored<PushSessionWatch>(entity, WatchKeys);
    }

    [Fact]
    public void Watch_store_lower_cases_tenant_owner_object_id_and_upn()
    {
        var watch = FullWatch();
        watch.TenantId = "11111111-AAAA-1111-1111-111111111111";
        watch.OwnerObjectId = "AAAAAAAA-0000-0000-0000-000000000001";
        watch.OwnerUpn = "Watcher@Contoso.invalid";

        var entity = TablePushRepository.StoreWatch(watch);
        var mapped = TablePushRepository.MapWatch(entity);

        Assert.Equal("11111111-aaaa-1111-1111-111111111111", entity.PartitionKey);
        Assert.Equal($"{SessionId}_aaaaaaaa-0000-0000-0000-000000000001", entity.RowKey);
        Assert.Equal("11111111-aaaa-1111-1111-111111111111", mapped.TenantId);
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001", mapped.OwnerObjectId);
        Assert.Equal("watcher@contoso.invalid", mapped.OwnerUpn);
    }

    [Fact]
    public void Watch_owner_scopes_list_survives_the_round_trip()
    {
        var watch = FullWatch();
        watch.OwnerScopes = $"{Constants.Push.PlatformScope};{TenantId}";

        var mapped = TablePushRepository.MapWatch(TablePushRepository.StoreWatch(watch));

        Assert.Equal("platform;11111111-1111-1111-1111-111111111111", mapped.OwnerScopes);
    }

    [Fact]
    public void Watch_store_labels_every_date_utc_without_moving_it()
    {
        var input = WithUnspecifiedKinds(FullWatch());

        var entity = TablePushRepository.StoreWatch(input);
        var mapped = TablePushRepository.MapWatch(entity);

        AssertDatesStoredAsUtc(input, entity, mapped);
    }

    [Fact]
    public void Watch_row_without_columns_maps_to_empty_scopes_and_min_value_dates()
    {
        var mapped = TablePushRepository.MapWatch(new TableEntity(TenantId, $"{SessionId}_{OwnerObjectId}"));

        Assert.Equal(TenantId, mapped.TenantId);
        // Legacy rows without OwnerScopes mean "the session tenant"; Map reads the columns, not the RowKey.
        Assert.Equal(string.Empty, mapped.OwnerScopes);
        Assert.Equal(string.Empty, mapped.SessionId);
        Assert.Equal(string.Empty, mapped.OwnerObjectId);
        Assert.Equal(string.Empty, mapped.OwnerUpn);
        Assert.Equal(DateTime.MinValue, mapped.CreatedUtc);
        Assert.Equal(DateTime.MinValue, mapped.ExpiresUtc);
    }

    // ── Fixtures: a distinct non-default value for every public property ────

    private static PushDevice FullDevice() => new()
    {
        Scope = TenantId,
        DeviceId = DeviceId,
        OwnerUpn = "owner@contoso.invalid",
        OwnerObjectId = OwnerObjectId,
        OwnerHomeTenantId = OwnerHomeTenantId,
        Kind = "apns",
        Endpoint = "https://push.contoso.invalid/send/endpoint-0001",
        P256dh = "p256dh-key-material-0001",
        Auth = "auth-secret-0001",
        DeviceSecretHash = "device-secret-hash-0001",
        VapidKid = "KID00001",
        Label = "My iPhone",
        Platform = "ios-homescreen",
        AppVersion = "1.2.3",
        Status = Constants.Push.DeviceStatus.Paused,
        StatusReason = "delivery_failures",
        PairedUtc = Anchor,
        ConfirmedUtc = Anchor.AddSeconds(1),
        LastDeliveredUtc = Anchor.AddSeconds(2),
        LastOpenedUtc = Anchor.AddSeconds(3),
        StatusChangedUtc = Anchor.AddSeconds(4),
        LastStatusCode = 410,
        ConsecutiveFailures = 3,
        WindowStartUtc = Anchor.AddSeconds(5),
        WindowCount = 7,
        SuppressedCount = 2,
    };

    private static PushPairingGrant FullGrant() => new()
    {
        CodeHash = "code-hash-base64url-0001",
        TenantId = TenantId,
        OwnerUpn = "admin@contoso.invalid",
        OwnerObjectId = OwnerObjectId,
        OwnerHomeTenantId = OwnerHomeTenantId,
        Status = Constants.Push.PairingStatus.Redeemed,
        CreatedUtc = Anchor,
        ExpiresUtc = Anchor.AddMinutes(Constants.Push.PairingGrantMinutes),
        RedeemedUtc = Anchor.AddSeconds(42),
        DeviceId = DeviceId,
        FailedAttempts = 2,
        ETag = "W/\"datetime'2026-10-07T08%3A30%3A00.0000000Z'\"",
    };

    private static PushSessionWatch FullWatch() => new()
    {
        TenantId = TenantId,
        SessionId = SessionId,
        OwnerObjectId = OwnerObjectId,
        OwnerUpn = "watcher@contoso.invalid",
        OwnerScopes = $"{Constants.Push.PlatformScope};{OwnerHomeTenantId}",
        CreatedUtc = Anchor,
        ExpiresUtc = Anchor.AddHours(6),
    };

    // ── Reflection helpers ──────────────────────────────────────────────────

    private static PropertyInfo[] PublicProperties<T>()
        => typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    private static IEnumerable<PropertyInfo> DateProperties<T>()
        => PublicProperties<T>().Where(p => p.PropertyType == typeof(DateTime) || p.PropertyType == typeof(DateTime?));

    /// <summary>Every public property of <paramref name="expected"/> equals the one on <paramref name="actual"/>, except the named ones.</summary>
    private static void AssertSameProperties<T>(T expected, T actual, params string[] except) where T : class
    {
        var mismatches = PublicProperties<T>()
            .Where(p => !except.Contains(p.Name))
            .Where(p => !Equals(p.GetValue(expected), p.GetValue(actual)))
            .Select(p => $"{typeof(T).Name}.{p.Name}: expected '{p.GetValue(expected)}', got '{p.GetValue(actual)}'")
            .ToList();
        Assert.Empty(mismatches);
    }

    /// <summary>
    /// The round trip is only as strong as its input: a property left at its default would survive a
    /// dropped column unnoticed, and two properties sharing a value would hide swapped columns.
    /// </summary>
    private static void AssertFixtureIsDistinctAndNonDefault<T>(T fixture) where T : class, new()
    {
        var blank = new T();
        var atDefault = PublicProperties<T>()
            .Where(p => Equals(p.GetValue(fixture), p.GetValue(blank)))
            .Select(p => p.Name)
            .ToList();
        Assert.Empty(atDefault);

        var duplicates = PublicProperties<T>()
            .Select(p => p.GetValue(fixture))
            .OfType<object>()
            .GroupBy(v => v)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}")
            .ToList();
        Assert.Empty(duplicates);
    }

    /// <summary>
    /// Every public property of <typeparamref name="T"/> lands in the entity: as the column of the same
    /// name, or under its documented key mapping. A property added to the model without a Store line fails here.
    /// </summary>
    private static void AssertEveryPropertyIsStored<T>(TableEntity entity, IReadOnlyDictionary<string, string> keyMappings, params string[] notStored)
    {
        var missing = PublicProperties<T>()
            .Where(p => !notStored.Contains(p.Name))
            .Select(p => keyMappings.GetValueOrDefault(p.Name) ?? p.Name)
            .Where(column => !entity.ContainsKey(column))
            .ToList();
        Assert.Empty(missing);
    }

    /// <summary>Relabels every date of the model as Unspecified without moving it — a value parsed from JSON without a zone designator.</summary>
    private static T WithUnspecifiedKinds<T>(T model) where T : class
    {
        foreach (var p in DateProperties<T>())
        {
            if (p.GetValue(model) is DateTime dt)
                p.SetValue(model, DateTime.SpecifyKind(dt, DateTimeKind.Unspecified));
        }
        return model;
    }

    /// <summary>
    /// Store labels every date Utc (Edm.DateTime is UTC-only; an Unspecified or Local value would be
    /// serialised with a shift or without a zone): every DateTime column of the entity and every date
    /// of the mapped model carries Kind Utc with the input's ticks untouched.
    /// </summary>
    private static void AssertDatesStoredAsUtc<T>(T input, TableEntity entity, T mapped) where T : class
    {
        var nonUtcColumns = entity.Keys
            .Where(key => entity[key] is DateTime dt && dt.Kind != DateTimeKind.Utc)
            .ToList();
        Assert.Empty(nonUtcColumns);

        var mismatches = new List<string>();
        foreach (var p in DateProperties<T>())
        {
            if (p.GetValue(input) is not DateTime expected)
                continue;
            if (p.GetValue(mapped) is not DateTime actual || actual.Kind != DateTimeKind.Utc || actual.Ticks != expected.Ticks)
                mismatches.Add($"{p.Name}: expected ticks {expected.Ticks} as Utc, got '{p.GetValue(mapped)}'");
        }
        Assert.Empty(mismatches);
    }
}
