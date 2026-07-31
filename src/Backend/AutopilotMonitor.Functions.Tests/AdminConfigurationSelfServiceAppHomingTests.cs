using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Default-value + fail-closed tests for <see cref="AdminConfiguration.SelfServiceAppHomingEnabled"/>.
/// The flag is the kill switch of the dual app-reg self-service homing flip — defaulting to false
/// keeps the consent funnel and auto-flip off until the operator deliberately opts in, and a
/// storage error must read as "disabled" (kill switch wins over a blip).
/// </summary>
public class AdminConfigurationSelfServiceAppHomingTests
{
    [Fact]
    public void SelfServiceAppHoming_defaults_to_false_on_new_config()
    {
        var cfg = new AdminConfiguration();
        Assert.False(cfg.SelfServiceAppHomingEnabled);
    }

    [Fact]
    public void SelfServiceAppHoming_defaults_to_false_on_CreateDefault()
    {
        var cfg = AdminConfiguration.CreateDefault();
        Assert.False(cfg.SelfServiceAppHomingEnabled);
    }

    [Fact]
    public void SelfServiceAppHoming_true_persists_on_the_config()
    {
        var cfg = new AdminConfiguration { SelfServiceAppHomingEnabled = true };
        Assert.True(cfg.SelfServiceAppHomingEnabled);
    }

    [Fact]
    public async Task Reader_returns_true_when_flag_enabled()
    {
        var repo = new Mock<IConfigRepository>();
        repo.Setup(r => r.GetAdminConfigurationAsync())
            .ReturnsAsync(new AdminConfiguration { SelfServiceAppHomingEnabled = true });
        var svc = new AdminConfigurationService(
            repo.Object, NullLogger<AdminConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions()));

        Assert.True(await svc.IsSelfServiceAppHomingEnabledAsync());
    }

    [Fact]
    public async Task Reader_fails_closed_on_repository_error()
    {
        var repo = new Mock<IConfigRepository>();
        repo.Setup(r => r.GetAdminConfigurationAsync())
            .ThrowsAsync(new InvalidOperationException("storage down"));
        var svc = new AdminConfigurationService(
            repo.Object, NullLogger<AdminConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions()));

        Assert.False(await svc.IsSelfServiceAppHomingEnabledAsync());
    }
}
