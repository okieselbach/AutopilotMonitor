using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Server-side "exclude deletions" audit view. Per-session deletion bookkeeping
/// (deletion_started/deletion_completed) is written with the triggering
/// operator's UPN — not System.Maintenance — so a cleanup sweep floods the
/// audit table with rows the performer exclusion does not catch. The "All
/// (excl. deletions)" view must drop these in the OData query so a page is
/// back-filled with real entries instead of coming back empty.
/// </summary>
public class AuditLogDeletionExclusionTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Tenant_filter_includes_deletion_exclusion_only_when_requested()
    {
        var without = TableStorageService.BuildAuditLogFilter(Tenant.ToString(), null, null, excludeDeletions: false);
        var with = TableStorageService.BuildAuditLogFilter(Tenant.ToString(), null, null, excludeDeletions: true);

        Assert.DoesNotContain("deletion_started", without);
        Assert.DoesNotContain("deletion_completed", without);

        Assert.Contains("Action ne 'deletion_started'", with);
        Assert.Contains("Action ne 'deletion_completed'", with);
    }

    [Fact]
    public void Global_fanout_filter_includes_deletion_exclusion_only_when_requested()
    {
        var without = TableStorageService.BuildAuditLogFilterWithRowKeyBound(
            Tenant.ToString(), null, null, rowKeyBound: null, excludeDeletions: false);
        var with = TableStorageService.BuildAuditLogFilterWithRowKeyBound(
            Tenant.ToString(), null, null, rowKeyBound: null, excludeDeletions: true);

        Assert.DoesNotContain("deletion_started", without);
        Assert.Contains("Action ne 'deletion_started'", with);
        Assert.Contains("Action ne 'deletion_completed'", with);
    }

    [Fact]
    public void Exclusion_clause_is_identical_across_tenant_and_global_paths()
    {
        // Both code paths must drop the same actions; a drift would let deletion
        // noise leak back into one of the two views.
        var tenant = TableStorageService.BuildAuditLogFilter(Tenant.ToString(), null, null, excludeDeletions: true);
        var global = TableStorageService.BuildAuditLogFilterWithRowKeyBound(
            Tenant.ToString(), null, null, rowKeyBound: null, excludeDeletions: true);

        var clause = TableStorageService.DeletionExclusionClause();
        Assert.Contains(clause, tenant);
        Assert.Contains(clause, global);
    }

    [Fact]
    public void Exclusion_does_not_disturb_the_performer_suppression()
    {
        // The pre-existing System.Maintenance suppression must survive alongside
        // the new clause — both noise sources stay filtered.
        var with = TableStorageService.BuildAuditLogFilter(Tenant.ToString(), null, null, excludeDeletions: true);
        Assert.Contains("PerformedBy ne 'System.Maintenance'", with);
    }
}
