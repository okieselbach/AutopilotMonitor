using System.Threading;
using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing Azure Table Storage operations.
    /// Split into partial class files by domain:
    ///   - TableStorageService.cs          (this file: core, initialization, helpers)
    ///   - TableStorageService.Sessions.cs (sessions, events, mapping)
    ///   - TableStorageService.Rules.cs    (gather/analyze rules, rule states, IME patterns)
    ///   - TableStorageService.Metrics.cs  (usage metrics, platform stats, user activity, app installs)
    ///   - TableStorageService.Maintenance.cs (audit logs, data retention, deletion helpers)
    /// </summary>
    public partial class TableStorageService
    {
        private readonly TableServiceClient _tableServiceClient;
        private readonly ILogger<TableStorageService> _logger;
        private bool _tablesInitialized = false;
        private readonly object _initLock = new object();

        public TableStorageService(IConfiguration configuration, ILogger<TableStorageService> logger)
        {
            _logger = logger;

            var connectionString = configuration["AzureTableStorageConnectionString"];
            var storageAccountName = configuration["AzureStorageAccountName"];

            if (!string.IsNullOrEmpty(storageAccountName))
            {
                // Managed Identity: use DefaultAzureCredential with storage account name
                var tableUri = new Uri($"https://{storageAccountName}.table.core.windows.net");
                _tableServiceClient = new TableServiceClient(tableUri, new DefaultAzureCredential());
                _logger.LogInformation("Table Storage initialized with Managed Identity (account: {Account})", storageAccountName);
            }
            else if (!string.IsNullOrEmpty(connectionString))
            {
                // Fallback: connection string (local dev, legacy)
                _tableServiceClient = new TableServiceClient(connectionString);
                _logger.LogInformation("Table Storage initialized with connection string");
            }
            else
            {
                throw new InvalidOperationException(
                    "Table Storage not configured. Set either 'AzureStorageAccountName' (for Managed Identity) or 'AzureTableStorageConnectionString'.");
            }
        }

        /// <summary>
        /// Test seam: construct directly from a (possibly Moq'd) <see cref="TableServiceClient"/>.
        /// Used by xUnit so the storage-touching helpers in the partial classes (Deletion,
        /// Inventory, …) can be exercised against the SDK's virtual surface without hitting Azure.
        /// Public (not internal) because Moq's dynamic proxy assembly cannot see internal ctors
        /// even via InternalsVisibleTo.
        /// </summary>
        public TableStorageService(TableServiceClient tableServiceClient, ILogger<TableStorageService> logger)
        {
            _tableServiceClient = tableServiceClient;
            _logger = logger;
        }

        /// <summary>
        /// Returns a TableClient for the specified table name.
        /// Used by services that need direct table access (e.g. VulnerabilityCorrelationService).
        /// </summary>
        public TableClient GetTableClient(string tableName)
        {
            return _tableServiceClient.GetTableClient(tableName);
        }

        /// <summary>
        /// Initializes all Azure Table Storage tables.
        /// This method is idempotent and safe to call multiple times.
        /// Should be called at application startup.
        /// </summary>
        public async Task InitializeTablesAsync()
        {
            if (_tablesInitialized)
            {
                _logger.LogDebug("Tables already initialized, skipping");
                return;
            }

            lock (_initLock)
            {
                if (_tablesInitialized) return;
            }

            _logger.LogInformation("Initializing Azure Table Storage tables...");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var successCount = 0;
            var failCount = 0;

            await Parallel.ForEachAsync(
                Constants.TableNames.All,
                new ParallelOptions { MaxDegreeOfParallelism = 8 },
                async (tableName, ct) =>
                {
                    try
                    {
                        await _tableServiceClient.CreateTableIfNotExistsAsync(tableName, ct);
                        _logger.LogDebug("Table '{TableName}' initialized", tableName);
                        Interlocked.Increment(ref successCount);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to initialize table '{TableName}'", tableName);
                        Interlocked.Increment(ref failCount);
                    }
                });

            stopwatch.Stop();
            _logger.LogInformation("Table initialization completed in {ElapsedMs}ms: {Success} succeeded, {Failed} failed",
                stopwatch.ElapsedMilliseconds, successCount, failCount);

            // CPE mapping seed is imported via Admin UI "Re-Seed Mappings" button
            // (pulls from GitHub, not embedded resource). No auto-import at startup.

            lock (_initLock)
            {
                _tablesInitialized = failCount == 0;
            }
        }

        /// <summary>
        /// Gets the TableServiceClient for direct access (used by other services)
        /// </summary>
        public TableServiceClient GetTableServiceClient() => _tableServiceClient;

        // ===== HELPER METHODS =====

        /// <summary>
        /// Safely reads an Int32 property from a TableEntity.
        /// Returns null instead of throwing when the property has a different type (legacy data).
        /// </summary>
        private int? SafeGetInt32(TableEntity entity, string key)
        {
            try
            {
                return entity.GetInt32(key);
            }
            catch (InvalidOperationException)
            {
                _logger.LogWarning("Property '{Key}' on entity {PK}/{RK} is not Int32, attempting string parse", key, entity.PartitionKey, entity.RowKey);
                var str = entity.GetString(key);
                if (str != null && int.TryParse(str, out var parsed))
                    return parsed;
                return null;
            }
        }

        /// <summary>
        /// Safely reads a DateTime property from a TableEntity.
        /// Returns null instead of throwing when the property has a different type (legacy data).
        /// </summary>
        private DateTime? SafeGetDateTime(TableEntity entity, string key)
        {
            try
            {
                return entity.GetDateTime(key);
            }
            catch (InvalidOperationException)
            {
                _logger.LogWarning("Property '{Key}' on entity {PK}/{RK} is not DateTime, attempting string parse", key, entity.PartitionKey, entity.RowKey);
                var str = entity.GetString(key);
                if (str != null && DateTime.TryParse(str, out var parsed))
                    return parsed;
                return null;
            }
        }

        private T DeserializeJson<T>(string? json) where T : new()
        {
            if (string.IsNullOrEmpty(json))
                return new T();

            try
            {
                return JsonConvert.DeserializeObject<T>(json) ?? new T();
            }
            catch
            {
                return new T();
            }
        }

        /// <summary>
        /// Deserializes MatchedConditions JSON and normalizes nested JObject/JArray values
        /// to plain Dictionary/List so System.Text.Json can serialize them correctly.
        /// </summary>
        private Dictionary<string, object> DeserializeMatchedConditions(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return new Dictionary<string, object>();

            try
            {
                var raw = JsonConvert.DeserializeObject<Dictionary<string, object>>(json)
                          ?? new Dictionary<string, object>();

                var result = new Dictionary<string, object>();
                foreach (var kv in raw)
                    result[kv.Key] = NormalizeJToken(kv.Value);
                return result;
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        private static object NormalizeJToken(object? value)
        {
            if (value is Newtonsoft.Json.Linq.JObject jObj)
            {
                var dict = new Dictionary<string, object>();
                foreach (var prop in jObj.Properties())
                    dict[prop.Name] = NormalizeJToken(prop.Value);
                return dict;
            }
            if (value is Newtonsoft.Json.Linq.JArray jArr)
            {
                var list = new List<object>();
                foreach (var item in jArr)
                    list.Add(NormalizeJToken(item));
                return list;
            }
            if (value is Newtonsoft.Json.Linq.JValue jVal)
                return jVal.Value ?? string.Empty;
            return value ?? string.Empty;
        }

        private string[] DeserializeJsonArray(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return Array.Empty<string>();

            try
            {
                return JsonConvert.DeserializeObject<string[]>(json) ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Deserializes event data JSON and converts JToken objects to native .NET types
        /// </summary>
        private Dictionary<string, object> DeserializeEventData(string? dataJson)
        {
            if (string.IsNullOrEmpty(dataJson))
                return new Dictionary<string, object>();

            try
            {
                var deserialized = JsonConvert.DeserializeObject<Dictionary<string, object>>(dataJson);
                // Convert all JToken values to native types (shared with the ingest paths).
                return Functions.Ingest.EventDataNormalizer.NormalizeMap(deserialized);
            }
            catch
            {
                // JSON may be truncated (64KB Table Storage limit) — preserve the raw
                // string so the UI can still display it for debugging.
                return new Dictionary<string, object>
                {
                    ["_rawDataJson"] = dataJson
                };
            }
        }

    }

}
