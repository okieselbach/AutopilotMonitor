using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// A radar pass reads one tenant at a time so its memory follows the largest tenant, and it never
/// evaluates an empty list: re-arm would read that as "version drained" and delete open episodes.
/// </summary>
public class AppVersionRadarPassTests
{
    private static List<AppInstallSummary> Rows(string tenantId, int count)
    {
        var rows = new List<AppInstallSummary>();
        for (var i = 0; i < count; i++)
            rows.Add(new AppInstallSummary { TenantId = tenantId });
        return rows;
    }

    [Fact]
    public async Task Each_tenant_is_finished_before_the_next_one_is_loaded()
    {
        var calls = new List<string>();

        await MaintenanceService.RunAppVersionRadarPassAsync(
            new[] { "a", "b" },
            tenantId =>
            {
                calls.Add($"load {tenantId}");
                return Task.FromResult(Rows(tenantId, 2));
            },
            (tenantId, summaries) =>
            {
                calls.Add($"evaluate {tenantId}");
                Assert.All(summaries, s => Assert.Equal(tenantId, s.TenantId));
                return Task.FromResult((0, 0, 0));
            },
            (_, ex) => throw ex);

        Assert.Equal(new[] { "load a", "evaluate a", "load b", "evaluate b" }, calls);
    }

    [Fact]
    public async Task A_tenant_without_rows_is_not_evaluated()
    {
        var evaluated = new List<string>();

        var pass = await MaintenanceService.RunAppVersionRadarPassAsync(
            new[] { "empty", "full" },
            tenantId => Task.FromResult(tenantId == "empty" ? new List<AppInstallSummary>() : Rows(tenantId, 3)),
            (tenantId, _) =>
            {
                evaluated.Add(tenantId);
                return Task.FromResult((0, 0, 0));
            },
            (_, ex) => throw ex);

        Assert.Equal(new[] { "full" }, evaluated);
        Assert.Equal(1, pass.Tenants);
        Assert.Equal(3, pass.Rows);
    }

    [Fact]
    public async Task A_failing_tenant_does_not_cost_the_others_their_pass()
    {
        var failed = new List<string>();

        var pass = await MaintenanceService.RunAppVersionRadarPassAsync(
            new[] { "load-fails", "evaluate-fails", "ok" },
            tenantId => tenantId == "load-fails"
                ? throw new InvalidOperationException("load")
                : Task.FromResult(Rows(tenantId, 1)),
            (tenantId, _) => tenantId == "evaluate-fails"
                ? throw new InvalidOperationException("evaluate")
                : Task.FromResult((1, 2, 3)),
            (tenantId, _) => failed.Add(tenantId));

        Assert.Equal(new[] { "load-fails", "evaluate-fails" }, failed);
        Assert.Equal((1, 2, 3), (pass.Fired, pass.Refreshed, pass.Rearmed));
    }

    [Fact]
    public async Task The_totals_add_up_over_the_tenants()
    {
        var pass = await MaintenanceService.RunAppVersionRadarPassAsync(
            new[] { "a", "b" },
            tenantId => Task.FromResult(Rows(tenantId, 2)),
            (_, _) => Task.FromResult((1, 2, 3)),
            (_, ex) => throw ex);

        Assert.Equal((2, 4L, 2, 4, 6), pass);
    }
}
