using System;
using System.Linq;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services.Maintenance
{
    /// <summary>
    /// The OData filter that selects every row of ONE session in each table the orphan sweep
    /// clears — the key shape of the table's writer, not of the cascade manifest (a session
    /// without a Sessions row has no manifest). Pure, so the shapes are unit-tested against the
    /// writers once instead of trusted per call.
    /// </summary>
    public static class OrphanSessionRowFilters
    {
        /// <summary>
        /// Tables cleared through a filter, in the order the sweep runs them. The handle
        /// (EventSessionIndex) and the inventory pair (SessionInventoryContributions + the
        /// SoftwareInventory decrements) are handled by the sweeper itself and are not listed here.
        /// </summary>
        public static readonly string[] Tables =
        {
            Constants.TableNames.Events,
            Constants.TableNames.RuleResults,
            Constants.TableNames.AppInstallSummaries,
            Constants.TableNames.VulnerabilityReports,
            Constants.TableNames.DeviceSnapshot,
            Constants.TableNames.Signals,
            Constants.TableNames.DecisionTransitions,
            Constants.TableNames.EventTypeIndex,
            Constants.TableNames.CveIndex,
            Constants.TableNames.SessionTimeBreakdowns,
            Constants.TableNames.SessionAnnotations,
            Constants.TableNames.SessionTenantLookup,
        };

        /// <summary>
        /// Filter for <paramref name="table"/>; throws for a table that has no declared shape so a
        /// new session table can never be swept with a guessed filter.
        /// </summary>
        public static string For(string table, string tenantId, string sessionId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));
            var t = ODataSanitizer.EscapeValue(tenantId);
            var s = ODataSanitizer.EscapeValue(sessionId);

            switch (table)
            {
                // Partition per session: {tenantId}_{sessionId}.
                case Constants.TableNames.Events:
                case Constants.TableNames.RuleResults:
                case Constants.TableNames.Signals:
                case Constants.TableNames.DecisionTransitions:
                case Constants.TableNames.VulnerabilityReports:
                    return $"PartitionKey eq '{t}_{s}'";

                // One row per session under the tenant partition.
                case Constants.TableNames.DeviceSnapshot:
                case Constants.TableNames.SessionTimeBreakdowns:
                    return $"PartitionKey eq '{t}' and RowKey eq '{s}'";

                // Tenant partition, session in a column.
                case Constants.TableNames.AppInstallSummaries:
                    return $"PartitionKey eq '{t}' and SessionId eq '{s}'";

                // PK {tenantId}_{eventType}, RK {invertedTicks}_{sessionId}: the RowKey of a session
                // without a Sessions row is not reconstructible (one row per batch), so the session
                // is found by its column across the tenant's partitions. Tenant ids are GUIDs, so the
                // '_'..'_~' range cannot reach another tenant.
                case Constants.TableNames.EventTypeIndex:
                    return $"PartitionKey ge '{t}_' and PartitionKey lt '{t}_~' and SessionId eq '{s}'";

                // PK {tenantId}_{cveId}, RK sessionId.
                case Constants.TableNames.CveIndex:
                    return $"PartitionKey ge '{t}_' and PartitionKey lt '{t}_~' and RowKey eq '{s}'";

                // PK tenantId, RK {sessionId}_{lane}; the lanes are a closed set.
                case Constants.TableNames.SessionAnnotations:
                    var lanes = string.Join(" or ", AnnotationLanes.All.Select(lane => $"RowKey eq '{s}_{lane}'"));
                    return $"PartitionKey eq '{t}' and ({lanes})";

                // PK sessionId, RK 'tenant'; the TenantId column pins the row to this tenant's session.
                case Constants.TableNames.SessionTenantLookup:
                    return $"PartitionKey eq '{s}' and RowKey eq 'tenant' and TenantId eq '{t}'";

                default:
                    throw new ArgumentException($"No orphan-sweep filter is declared for table '{table}'.", nameof(table));
            }
        }
    }
}
