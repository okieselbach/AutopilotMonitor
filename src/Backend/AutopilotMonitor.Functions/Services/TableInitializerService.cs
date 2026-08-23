using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Hosted service that initializes all Azure Table Storage tables at application startup.
/// This ensures all tables exist before any requests are processed.
/// Also performs a one-time backfill of the SessionsIndex table on fresh storage (see StartAsync).
/// </summary>
public class TableInitializerService : IHostedService
{
    private readonly TableStorageService _tableStorageService;
    private readonly ILogger<TableInitializerService> _logger;

    public TableInitializerService(
        TableStorageService tableStorageService,
        ILogger<TableInitializerService> logger)
    {
        _tableStorageService = tableStorageService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TableInitializerService starting");

        var fullPassRan = false;
        try
        {
            fullPassRan = await _tableStorageService.InitializeTablesAsync();
            _logger.LogInformation("TableInitializerService completed - all tables ready (fullPass={FullPass})", fullPassRan);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TableInitializerService failed to initialize tables");
            // Don't throw - allow the application to start even if table creation fails
            // Individual operations will fail gracefully with appropriate error messages
        }

        // One-time backfill: SessionsIndex can only be empty on fresh storage, which is exactly
        // when the schema sentinel was missing and the full pass ran. Skipped on the fast path,
        // and guarded by a conditional-insert claim so only one scaled-out instance scans.
        if (!fullPassRan) return;

        try
        {
            if (await _tableStorageService.IsSessionIndexEmptyAsync()
                && await _tableStorageService.TryClaimSessionIndexBackfillAsync())
            {
                _logger.LogInformation("SessionsIndex table is empty — starting startup backfill");
                var count = await _tableStorageService.BackfillSessionIndexAsync();
                _logger.LogInformation("Startup backfill completed: {Count} sessions indexed", count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup session index backfill failed — maintenance backfill will catch up");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
