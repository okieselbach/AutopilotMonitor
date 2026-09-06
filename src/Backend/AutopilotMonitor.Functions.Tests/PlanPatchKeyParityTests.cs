using System.Linq;
using System.Reflection;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The tenant-plan PATCH is the one body read as a document (absent key ≠ explicit null). Its
/// handler walks the keys by name; this pins that key set to <see cref="PatchTenantPlanRequest"/>,
/// the type the web and MCP compile against — a property added to the DTO without a handler
/// branch (or the reverse) fails here instead of silently never binding.
/// </summary>
public class PlanPatchKeyParityTests
{
    [Fact]
    public void HandlerKeysMatchTheWireDto()
    {
        var dtoKeys = typeof(PatchTenantPlanRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .OrderBy(k => k, System.StringComparer.Ordinal)
            .ToArray();

        var handlerKeys = PlanManagementFunction.PlanPatchKeys.All
            .OrderBy(k => k, System.StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(dtoKeys, handlerKeys);
        Assert.Contains("trialExpiresUtc", handlerKeys); // the presence-sensitive key that motivated the document read
    }
}
