using System.Collections.Generic;
using System.Text.Json;

namespace AutopilotMonitor.Shared.Models.Config
{
    /// <summary>
    /// Parses <see cref="AdminConfiguration.PlanTierDefinitionsJson"/> into typed definitions.
    /// Shared between the plan management surface and MCP quota enforcement so both read the
    /// exact same shape. Malformed/empty JSON → empty list (callers fall back to code defaults).
    /// </summary>
    public static class PlanTierDefinitionParser
    {
        private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

        public static List<PlanTierDefinition> Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<PlanTierDefinition>();

            try
            {
                return JsonSerializer.Deserialize<List<PlanTierDefinition>>(json!, Options)
                       ?? new List<PlanTierDefinition>();
            }
            catch
            {
                return new List<PlanTierDefinition>();
            }
        }
    }
}
