using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using System.Collections.Specialized;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Exact-match field filters (action / performedBy / entityType / entityId) for the
/// audit-log views. They are folded into the server-side OData query so a filtered
/// query never falls back to in-memory scanning, and into the pagination fingerprint
/// (via the request extras) so a token minted for one filter set can't be replayed
/// against another.
/// </summary>
public class AuditLogFieldFilterTests
{
    private static readonly string Tenant = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void Tenant_filter_omits_field_clauses_when_no_filters()
    {
        var f = TableStorageService.BuildAuditLogFilter(Tenant, null, null, excludeDeletions: false, filters: null);
        Assert.DoesNotContain("Action eq", f);
        Assert.DoesNotContain("PerformedBy eq", f);
        Assert.DoesNotContain("EntityType eq", f);
        Assert.DoesNotContain("EntityId eq", f);
    }

    [Fact]
    public void Tenant_filter_adds_eq_clause_per_supplied_field()
    {
        var filters = new AuditLogQueryFilters
        {
            Action = "config_updated",
            PerformedBy = "alice@contoso.com",
            EntityType = "TenantConfiguration",
            EntityId = "tenant-42",
        };

        var f = TableStorageService.BuildAuditLogFilter(Tenant, null, null, excludeDeletions: false, filters);

        Assert.Contains("Action eq 'config_updated'", f);
        Assert.Contains("PerformedBy eq 'alice@contoso.com'", f);
        Assert.Contains("EntityType eq 'TenantConfiguration'", f);
        Assert.Contains("EntityId eq 'tenant-42'", f);
    }

    [Fact]
    public void Global_fanout_filter_adds_eq_clause_per_supplied_field()
    {
        var filters = new AuditLogQueryFilters { Action = "device_blocked", EntityType = "Device" };

        var f = TableStorageService.BuildAuditLogFilterWithRowKeyBound(
            Tenant, null, null, lastRowKey: null, excludeDeletions: false, filters);

        Assert.Contains("Action eq 'device_blocked'", f);
        Assert.Contains("EntityType eq 'Device'", f);
        Assert.DoesNotContain("PerformedBy eq", f); // not supplied
    }

    [Fact]
    public void Field_filter_coexists_with_performer_suppression_and_deletion_exclusion()
    {
        var filters = new AuditLogQueryFilters { Action = "deletion_started" };
        var f = TableStorageService.BuildAuditLogFilter(Tenant, null, null, excludeDeletions: true, filters);

        // The new positive filter and the pre-existing negative noise filters all AND together.
        Assert.Contains("Action eq 'deletion_started'", f);
        Assert.Contains("PerformedBy ne 'System.Maintenance'", f);
        Assert.Contains("Action ne 'deletion_started'", f);
    }

    [Fact]
    public void Field_values_are_odata_escaped()
    {
        // A stray single quote must be doubled, not break out of the literal.
        var filters = new AuditLogQueryFilters { PerformedBy = "o'brien@contoso.com" };
        var f = TableStorageService.BuildAuditLogFilter(Tenant, null, null, excludeDeletions: false, filters);
        Assert.Contains("PerformedBy eq 'o''brien@contoso.com'", f);
    }

    [Fact]
    public void Request_parse_maps_query_params_and_empty_is_empty()
    {
        var query = new NameValueCollection
        {
            { "action", "config_updated" },
            { "performedBy", "alice@contoso.com" },
            { "entityType", "TenantConfiguration" },
            { "entityId", "tenant-42" },
        };

        var parsed = AuditLogFilterRequest.Parse(query);
        Assert.Equal("config_updated", parsed.Action);
        Assert.Equal("alice@contoso.com", parsed.PerformedBy);
        Assert.Equal("TenantConfiguration", parsed.EntityType);
        Assert.Equal("tenant-42", parsed.EntityId);
        Assert.False(parsed.IsEmpty);

        var empty = AuditLogFilterRequest.Parse(new NameValueCollection());
        Assert.True(empty.IsEmpty);
    }

    [Fact]
    public void Request_extras_are_ordered_and_skip_empty_fields()
    {
        // Order must be stable: the fingerprint computed at mint time has to match
        // the one recomputed from the echoed nextLink params on the follow-up call.
        var filters = new AuditLogQueryFilters { EntityType = "Device", Action = "device_blocked" };
        var extras = AuditLogFilterRequest.ToExtras(filters);

        Assert.Equal(2, extras.Count);
        Assert.Equal("action", extras[0].Key);
        Assert.Equal("device_blocked", extras[0].Value);
        Assert.Equal("entityType", extras[1].Key);
        Assert.Equal("Device", extras[1].Value);
    }
}
