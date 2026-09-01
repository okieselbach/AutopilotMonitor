using System.Linq;
using System.Reflection;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.DataAccess;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The cross-category PAGED read fans out over a fixed category list. A category that is written
/// but not listed is invisible to every paged reader (MCP get_ops_events, the ops dashboard) while
/// the unpaged full-table path still returns it — a silent, one-sided blind spot. That happened to
/// Platform (Azure Monitor alerts via the ops alert webhook), which was recorded from day one and
/// never fanned out. These tests fail the build when a new OpsEventCategory constant is added
/// without extending the fan-out list.
/// </summary>
public class OpsEventCategoryCoverageTests
{
    private static string[] DeclaredCategories() =>
        typeof(OpsEventCategory)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

    [Fact]
    public void All_CoversEveryDeclaredCategoryConstant()
    {
        var declared = DeclaredCategories();

        Assert.NotEmpty(declared);
        Assert.Equal(declared.OrderBy(c => c), OpsEventCategory.All.OrderBy(c => c));
    }

    [Fact]
    public void PagedFanOut_UsesTheFullCategoryVocabulary()
    {
        // Same array instance on purpose: the fan-out must not keep a private copy that can drift.
        Assert.Same(OpsEventCategory.All, TableOpsEventRepository.AllCategories);
        Assert.Contains(OpsEventCategory.Platform, TableOpsEventRepository.AllCategories);
    }
}
