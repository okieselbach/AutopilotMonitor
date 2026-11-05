using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// The quota wire nodes the MCP usage views share (the tenant's reporting page, the GA overview), built from the
/// quota counters and resolved plan limits — one place, so both views show the same windows the same way.
/// </summary>
internal static class McpQuotaNodes
{
    /// <summary>
    /// Pure: the tenant's organization windows from its counter rows — today's and this month's total over every
    /// account charged to the tenant, against the tenant plan's limits (0 = unlimited).
    /// </summary>
    internal static McpOrganizationQuotaNode BuildOrganizationQuota(
        IEnumerable<TenantUsageRecord> records, McpQuotaService.TenantPlanLimits limits, string today, string monthStart)
    {
        long dailyUsed = 0, monthlyUsed = 0;
        foreach (var record in records)
        {
            if (record.Date == today) dailyUsed += record.RequestCount;
            if (string.CompareOrdinal(record.Date, monthStart) >= 0 && string.CompareOrdinal(record.Date, today) <= 0)
                monthlyUsed += record.RequestCount;
        }

        return new McpOrganizationQuotaNode
        {
            TenantPlan = limits.TenantPlan,
            DailyLimit = limits.TenantDailyLimit,
            MonthlyLimit = limits.TenantMonthlyLimit,
            DailyUsed = dailyUsed,
            MonthlyUsed = monthlyUsed,
            PurchasedDelegatedSlots = WithSlots(limits.PurchasedDelegatedSlots, limits.PurchasedDelegatedSlots),
            SlotDailyLimit = WithSlots(limits.PurchasedDelegatedSlots, limits.SlotTenantDailyLimit),
            SlotMonthlyLimit = WithSlots(limits.PurchasedDelegatedSlots, limits.SlotTenantMonthlyLimit),
        };
    }

    /// <summary>The slot breakdown is only worth sending when a slot was actually purchased.</summary>
    internal static int? WithSlots(int purchasedSlots, int value) => purchasedSlots > 0 ? value : null;
}
