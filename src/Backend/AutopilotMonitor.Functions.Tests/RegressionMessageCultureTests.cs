using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The regression and calibration messages are English text for admins and operators: every number in
/// them is formatted in invariant culture, never "2,5x" or "12,5%" on a host whose culture uses a
/// decimal comma. Each test switches to de-DE so the proof holds on any machine, not only on one that
/// happens to run in English.
/// </summary>
public class RegressionMessageCultureTests
{
    private static readonly CultureInfo German = new("de-DE");

    [Fact]
    public void Maintenance_regression_messages_are_culture_invariant()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = German;
        try
        {
            var appMessage = MaintenanceService.BuildAppVersionRegressionMessage(new AppVersionDurationRegressionFinding
            {
                CurrentVersion = "2.4.0", PreviousVersion = "2.3.9",
                CurrentMedianSeconds = 1770, PreviousMedianSeconds = 690,
                CurrentMeasuredCount = 12, PreviousMeasuredCount = 40, Lift = 2.6,
            });
            Assert.Contains("rose from 11.5 to 29.5 min", appMessage);
            Assert.Contains("lift 2.6x", appMessage);

            var dimension = MaintenanceService.DescribeDimension(new RuleRegressionDimension
            {
                Dimension = "model", Value = "Latitude 5520", HitSharePct = 62.5, AllSharePct = 12.5, Lift = 5.0,
            });
            Assert.Equal(
                "62.5% of affected sessions are on model Latitude 5520 vs 12.5% of all sessions (lift 5x) — correlated, not necessarily causal",
                dimension);

            var ruleMessage = MaintenanceService.BuildRegressionMessage(new RuleRegressionFinding
            {
                WindowFireCount = 15, WindowSessionCount = 120, WindowRatePct = 12.5,
                BaselineFireCount = 10, BaselineSessionCount = 400, BaselineRatePct = 2.5,
                Lift = 5.0, WindowStartDate = "2026-07-20", WindowEndDate = "2026-07-26",
            }, dimension);
            Assert.Contains("(12.5%)", ruleMessage);
            Assert.Contains("baseline 2.5% (10/400 over the prior 28 days) — lift 5x", ruleMessage);
            Assert.Contains("62.5% of affected sessions", ruleMessage);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Ops_event_regression_messages_are_culture_invariant()
    {
        var saved = new List<OpsEventEntry>();
        var repository = new Mock<IOpsEventRepository>();
        repository.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
            .Callback<OpsEventEntry>(saved.Add)
            .Returns(Task.CompletedTask);
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions())) { CallBase = false };
        var ops = new OpsEventService(repository.Object, NullLogger<OpsEventService>.Instance,
            TestNotifications.InertOpsAlertDispatch(adminConfig.Object));

        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = German;
        try
        {
            await ops.RecordRuleFrequencyRegressionAsync("tenant", "RULE-1", "Network check",
                15, 120, 12.5, 10, 400, 2.5, lift: 2.5, dimensionSummary: null);
            await ops.RecordAppVersionDurationRegressionAsync("tenant", "App", "win32", "2.4.0", "2.3.9",
                1770, 690, 12, 40, lift: 2.6);
            await ops.RecordVerdictCalibrationDriftAsync("tenant", "kind", "path", "status",
                15, 120, 12.5, 10, 400, 2.5, lift: 2.5, dimensionSummary: null);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Equal(3, saved.Count);
        Assert.Contains("(12.5%) over 7d vs 2.5% baseline", saved[0].Message);
        Assert.EndsWith(" — lift 2.5x", saved[0].Message);
        Assert.Contains("rose from 11.5 to 29.5 min", saved[1].Message);
        Assert.EndsWith(" — lift 2.6x", saved[1].Message);
        Assert.Contains("(12.5%) over 7d vs 2.5% baseline", saved[2].Message);
        Assert.EndsWith(" — lift 2.5x", saved[2].Message);
    }
}
