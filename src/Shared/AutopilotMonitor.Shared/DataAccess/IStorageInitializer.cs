using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Initializes the storage backend (creates tables, containers, collections, etc.).
    /// Each storage provider implements this for its specific setup needs.
    /// </summary>
    public interface IStorageInitializer
    {
        /// <summary>Startup path: cheap when the storage schema is already known to be current.</summary>
        Task InitializeAsync();

        /// <summary>
        /// Unconditional full pass that (re)creates every registered table. Used by daily
        /// maintenance to repair tables that were deleted out-of-band after the startup
        /// fast path stopped checking them.
        /// </summary>
        Task EnsureAllAsync();
    }
}
