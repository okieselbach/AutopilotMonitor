using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of IStorageInitializer.
    /// Delegates to existing TableStorageService for backwards compatibility.
    /// </summary>
    public class TableStorageInitializer : IStorageInitializer
    {
        private readonly TableStorageService _storage;

        public TableStorageInitializer(TableStorageService storage)
        {
            _storage = storage;
        }

        public Task InitializeAsync()
            => _storage.InitializeTablesAsync();

        public Task EnsureAllAsync()
            => _storage.EnsureAllTablesAsync();
    }
}
