using System.Threading;
using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Caching;
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

        // ===== TENANT ID SNAPSHOT (cross-tenant fan-outs) =====
        //
        // Every cross-tenant read (session list/stats, app aggregates, geo window, audit logs)
        // fans out per tenant partition. The tenant list comes from TenantConfiguration (one row
        // per tenant) and used to be re-scanned for every 1000-row PAGE of the session-list
        // drain. It changes only on onboarding/offboarding, so one snapshot per instance for
        // TenantIdCacheTtl is shared by all fan-outs; a tenant onboarded meanwhile appears in
        // cross-tenant views after at most one TTL (its own tenant-scoped views are unaffected,
        // they never consult this list). Storage failures are never cached — the factory throws.
        internal static readonly TimeSpan TenantIdCacheTtl = TimeSpan.FromSeconds(60);
        private const string TenantIdCacheKey = "all";
        private readonly SingleFlightCache<IReadOnlyList<string>> _tenantIdCache = new();

        /// <summary>
        /// Cached TenantConfiguration partition keys (see the TENANT ID SNAPSHOT note). Throws on
        /// storage failure so callers keep their own fallback semantics (empty page vs. legacy scan).
        /// </summary>
        internal Task<IReadOnlyList<string>> GetTenantIdsCachedAsync()
            => _tenantIdCache.GetOrAddAsync(TenantIdCacheKey, TenantIdCacheTtl, () => QueryTenantIdsAsync(CancellationToken.None));

        private async Task<IReadOnlyList<string>> QueryTenantIdsAsync(CancellationToken cancellationToken)
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.TenantConfiguration);
            var query = tableClient.QueryAsync<TableEntity>(
                filter: "RowKey eq 'config'",
                select: new[] { "PartitionKey" },
                cancellationToken: cancellationToken);

            var tenantIds = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var entity in query)
            {
                tenantIds.Add(entity.PartitionKey);
            }

            return tenantIds.ToList();
        }

        /// <summary>
        /// Applies the delegated ("MSP") bound to a tenant-id set. Null bound = unchanged. Comparison
        /// is case-insensitive because AllowedTenantIds is lowercased while the config PartitionKey
        /// casing is not guaranteed.
        /// </summary>
        private static List<string> ApplyTenantBound(IReadOnlyList<string> tenantIds, IReadOnlyCollection<string>? allowedTenantIds)
        {
            if (allowedTenantIds == null) return tenantIds.ToList();
            var allowed = new HashSet<string>(allowedTenantIds, StringComparer.OrdinalIgnoreCase);
            return tenantIds.Where(allowed.Contains).ToList();
        }

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

        // ===== TABLE SCHEMA SENTINEL =====
        //
        // A cold start used to issue one CreateTableIfNotExists per table (each a Create POST
        // whose 409 is swallowed). The sentinel row in AdminConfiguration stores a hash derived
        // from Constants.TableNames.All; when it matches, startup is a single point-read.
        // Adding a table to TableNames.All changes the hash, so the next start runs the full
        // pass once and rewrites the sentinel — no manually maintained version to forget.
        // Gap: a table deleted by hand is not noticed until the daily maintenance full pass
        // (EnsureAllTablesAsync) recreates it.

        internal const string SchemaSentinelPartitionKey = "SchemaSentinel";
        internal const string SchemaSentinelRowKey = "tables";
        internal const string SchemaSentinelHashProperty = "TableSchemaHash";
        internal const string SessionIndexBackfillClaimRowKey = "sessionIndexBackfill";

        /// <summary>Outcome of the last <see cref="InitializeTablesAsync"/> call, read by StartupTelemetryService.</summary>
        public readonly record struct InitializationSnapshot(double DurationMs, bool FullPassRan);
        public InitializationSnapshot LastInitialization { get; private set; }

        /// <summary>
        /// Deterministic hash over the table registry (ordinal-sorted, so declaration order is irrelevant).
        /// </summary>
        internal static string ComputeTableSchemaHash(IEnumerable<string> tableNames)
        {
            var joined = string.Join("\n", tableNames.OrderBy(n => n, StringComparer.Ordinal));
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joined));
            return Convert.ToHexString(bytes);
        }

        /// <summary>
        /// Initializes all Azure Table Storage tables.
        /// Fast path: the schema sentinel matches the current table registry → no table calls.
        /// Slow path (sentinel missing, stale, or unreadable): CreateTableIfNotExists for every
        /// table, then the sentinel is (re)written. Idempotent and safe under scale-out — two
        /// instances that both see a stale sentinel simply both run the idempotent full pass.
        /// Returns true when the full pass ran (callers use this to gate one-time work such as
        /// the SessionsIndex backfill, which can only be needed on fresh storage).
        /// </summary>
        public async Task<bool> InitializeTablesAsync()
        {
            if (_tablesInitialized)
            {
                _logger.LogDebug("Tables already initialized, skipping");
                return false;
            }

            lock (_initLock)
            {
                if (_tablesInitialized) return false;
            }

            var initStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var expectedHash = ComputeTableSchemaHash(Constants.TableNames.All);

            if (await SchemaSentinelMatchesAsync(expectedHash))
            {
                _logger.LogInformation("Table schema sentinel matches ({HashPrefix}) — skipping table creation pass",
                    expectedHash.Substring(0, 12));
                lock (_initLock) { _tablesInitialized = true; }
                LastInitialization = new InitializationSnapshot(initStopwatch.Elapsed.TotalMilliseconds, FullPassRan: false);
                return false;
            }

            _logger.LogInformation("Table schema sentinel missing or stale — initializing Azure Table Storage tables...");
            var failCount = await EnsureAllTablesAsync();

            if (failCount == 0)
            {
                await WriteSchemaSentinelAsync(expectedHash);
            }

            lock (_initLock)
            {
                _tablesInitialized = failCount == 0;
            }
            LastInitialization = new InitializationSnapshot(initStopwatch.Elapsed.TotalMilliseconds, FullPassRan: true);
            return true;
        }

        /// <summary>
        /// Unconditional full pass: CreateTableIfNotExists for every table in the registry.
        /// Used by the startup slow path and by daily maintenance (recreates manually deleted tables).
        /// Returns the number of tables that failed to initialize.
        /// </summary>
        public async Task<int> EnsureAllTablesAsync()
        {
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

            return failCount;
        }

        private async Task<bool> SchemaSentinelMatchesAsync(string expectedHash)
        {
            try
            {
                var table = _tableServiceClient.GetTableClient(Constants.TableNames.AdminConfiguration);
                var response = await table.GetEntityIfExistsAsync<TableEntity>(
                    SchemaSentinelPartitionKey, SchemaSentinelRowKey, select: new[] { SchemaSentinelHashProperty });
                if (!response.HasValue) return false;
                return string.Equals(response.Value!.GetString(SchemaSentinelHashProperty), expectedHash, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                // Table missing (fresh storage) or transient error — fall back to the full pass.
                _logger.LogInformation(ex, "Table schema sentinel not readable — running full table initialization");
                return false;
            }
        }

        private async Task WriteSchemaSentinelAsync(string hash)
        {
            try
            {
                var table = _tableServiceClient.GetTableClient(Constants.TableNames.AdminConfiguration);
                var entity = new TableEntity(SchemaSentinelPartitionKey, SchemaSentinelRowKey)
                {
                    [SchemaSentinelHashProperty] = hash,
                    ["TableCount"] = Constants.TableNames.All.Length,
                    ["UpdatedUtc"] = DateTime.UtcNow
                };
                await table.UpsertEntityAsync(entity, TableUpdateMode.Replace);
            }
            catch (Exception ex)
            {
                // Non-fatal: the next start simply runs the full pass again.
                _logger.LogWarning(ex, "Failed to write table schema sentinel");
            }
        }

        /// <summary>
        /// Scale-out guard for the one-time SessionsIndex backfill: a conditional insert that
        /// only one instance can win. Returns true for the winner. A lost race (409) or any
        /// error yields false — the manual maintenance backfill remains the safety net.
        /// </summary>
        public async Task<bool> TryClaimSessionIndexBackfillAsync()
        {
            try
            {
                var table = _tableServiceClient.GetTableClient(Constants.TableNames.AdminConfiguration);
                var entity = new TableEntity(SchemaSentinelPartitionKey, SessionIndexBackfillClaimRowKey)
                {
                    ["ClaimedUtc"] = DateTime.UtcNow,
                    ["Instance"] = Environment.MachineName
                };
                await table.AddEntityAsync(entity);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                _logger.LogInformation("SessionsIndex backfill already claimed by another instance");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to claim SessionsIndex backfill");
                return false;
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
        /// Safely reads a Double property from a TableEntity.
        /// Returns null instead of throwing when the property has a different type (legacy data).
        /// Also accepts Int32/Int64-typed cells — Table Storage stores a whole-number double
        /// as Int when written via JSON paths that drop the decimal point.
        /// </summary>
        private double? SafeGetDouble(TableEntity entity, string key)
        {
            try
            {
                return entity.GetDouble(key);
            }
            catch (InvalidOperationException)
            {
                if (entity.TryGetValue(key, out var raw))
                {
                    switch (raw)
                    {
                        case int i: return i;
                        case long l: return l;
                        case string s when double.TryParse(s,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var parsed):
                            return parsed;
                    }
                }
                _logger.LogWarning("Property '{Key}' on entity {PK}/{RK} is not Double and could not be coerced", key, entity.PartitionKey, entity.RowKey);
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
