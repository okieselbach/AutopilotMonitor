using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Config;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Concurrent cache misses on the two configuration services share one repository read
/// (audit 2026-09-05 F12). The MemoryCache stays: the hit path and the in-place projection
/// semantics are untouched, only the miss path is single-flighted.
/// </summary>
public class ConfigurationServiceSingleFlightTests
{
    [Fact]
    public async Task Admin_config_concurrent_misses_issue_one_repository_read()
    {
        var gate = new TaskCompletionSource<AdminConfiguration?>();
        var repo = new Mock<IConfigRepository>();
        repo.Setup(r => r.GetAdminConfigurationAsync()).Returns(gate.Task);
        var sut = new AdminConfigurationService(repo.Object, NullLogger<AdminConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));

        var calls = Enumerable.Range(0, 10).Select(_ => sut.GetConfigurationAsync()).ToArray();
        gate.SetResult(AdminConfiguration.CreateDefault());
        var results = await Task.WhenAll(calls);

        repo.Verify(r => r.GetAdminConfigurationAsync(), Times.Once);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task Admin_config_after_completion_the_next_miss_reads_again()
    {
        var repo = new Mock<IConfigRepository>();
        repo.Setup(r => r.GetAdminConfigurationAsync()).ReturnsAsync(AdminConfiguration.CreateDefault());
        var sut = new AdminConfigurationService(repo.Object, NullLogger<AdminConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));

        await sut.GetConfigurationAsync();
        sut.InvalidateCache();
        await sut.GetConfigurationAsync();

        repo.Verify(r => r.GetAdminConfigurationAsync(), Times.Exactly(2));
    }

    [Fact]
    public async Task Tenant_config_concurrent_misses_for_one_tenant_issue_one_repository_read()
    {
        const string tenantId = "11111111-1111-1111-1111-111111111111";
        var gate = new TaskCompletionSource<TenantConfiguration?>();
        var repo = new Mock<IConfigRepository>();
        repo.Setup(r => r.GetTenantConfigurationAsync(tenantId)).Returns(gate.Task);
        var sut = new TenantConfigurationService(repo.Object, NullLogger<TenantConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));

        var calls = Enumerable.Range(0, 10).Select(_ => sut.GetConfigurationAsync(tenantId)).ToArray();
        gate.SetResult(TenantConfiguration.CreateDefault(tenantId));
        var results = await Task.WhenAll(calls);

        repo.Verify(r => r.GetTenantConfigurationAsync(tenantId), Times.Once);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task Tenant_config_different_tenants_do_not_share_a_flight()
    {
        const string a = "11111111-1111-1111-1111-111111111111";
        const string b = "22222222-2222-2222-2222-222222222222";
        var repo = new Mock<IConfigRepository>();
        repo.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>()))
            .Returns<string>(id => Task.FromResult<TenantConfiguration?>(TenantConfiguration.CreateDefault(id)));
        var sut = new TenantConfigurationService(repo.Object, NullLogger<TenantConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));

        await Task.WhenAll(sut.GetConfigurationAsync(a), sut.GetConfigurationAsync(b));

        repo.Verify(r => r.GetTenantConfigurationAsync(a), Times.Once);
        repo.Verify(r => r.GetTenantConfigurationAsync(b), Times.Once);
    }
}
