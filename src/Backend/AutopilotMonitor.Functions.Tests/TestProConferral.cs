using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Conferred-Pro test doubles: an inert <see cref="ProConferralService"/> for subjects that merely
/// depend on it, and a storage-free <see cref="ManagedTenantProIndex"/> that answers from a lambda.
/// </summary>
internal static class TestProConferral
{
    /// <summary>A service over empty storage: every record call is a no-op that returns "nothing stamped".</summary>
    public static ProConferralService Inert()
    {
        var adminRepo = new Mock<IAdminRepository>();
        adminRepo.Setup(r => r.GetGroupTenantsAsync(It.IsAny<string>())).ReturnsAsync(new List<string>());
        var configs = new TenantConfigurationService(
            Mock.Of<IConfigRepository>(), NullLogger<TenantConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        return new ProConferralService(adminRepo.Object, configs, ManagedTenantProIndex.None, NullLogger<ProConferralService>.Instance);
    }
}

/// <summary>Index stub: the conferring owner per tenant id comes from the lambda; no storage, no cache.</summary>
internal sealed class StubManagedTenantProIndex : ManagedTenantProIndex
{
    private readonly Func<string?, string?> _owner;
    public int Invalidations { get; private set; }

    public StubManagedTenantProIndex(Func<string?, string?> owner) => _owner = owner;

    public override Task<string?> GetConferringOwnerAsync(string? tenantId) => Task.FromResult(_owner(tenantId));
    public override void Invalidate() => Invalidations++;
}
