using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tenant Hello wait (<see cref="TenantConfiguration.HelloWaitTimeoutSeconds"/>): a changed value
/// must lie in 30..3600 s. Like the retention cap, only a CHANGED value is checked, so a value
/// stored before the rule existed never blocks an unrelated save or a backup revert (both
/// validate the whole model).
/// </summary>
public class HelloWaitTimeoutConfigTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    [Theory]
    [InlineData(30)]
    [InlineData(300)]
    [InlineData(1800)]
    [InlineData(3600)]
    public void ValidateModel_accepts_the_supported_range(int seconds)
    {
        var candidate = TenantConfiguration.CreateDefault(TenantId);
        candidate.HelloWaitTimeoutSeconds = seconds;

        Assert.Null(TenantConfigValidation.ValidateModel(candidate, TenantConfiguration.CreateDefault(TenantId), isGlobalAdmin: false));
    }

    [Theory]
    [InlineData(29)]
    [InlineData(3601)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateModel_rejects_a_changed_value_outside_the_range(int seconds)
    {
        var candidate = TenantConfiguration.CreateDefault(TenantId);
        candidate.HelloWaitTimeoutSeconds = seconds;

        var error = TenantConfigValidation.ValidateModel(candidate, TenantConfiguration.CreateDefault(TenantId), isGlobalAdmin: true);

        Assert.Equal("Hello wait timeout must be between 30 and 3600 seconds.", error);
    }

    [Fact]
    public void ValidateModel_lets_an_unchanged_out_of_range_value_through()
    {
        // Written through the API before the range was validated — an unrelated save (or a
        // revert to a snapshot holding the same value) must not fail on it.
        var existing = TenantConfiguration.CreateDefault(TenantId);
        existing.HelloWaitTimeoutSeconds = 86400;
        var candidate = TenantConfiguration.CreateDefault(TenantId);
        candidate.HelloWaitTimeoutSeconds = 86400;
        candidate.ContactEmail = "it@example.com";

        Assert.Null(TenantConfigValidation.ValidateModel(candidate, existing, isGlobalAdmin: false));
    }

    [Fact]
    public void Default_is_30_seconds_which_keeps_the_built_in_window()
    {
        Assert.Equal(30, TenantConfiguration.CreateDefault(TenantId).HelloWaitTimeoutSeconds);
        Assert.Equal(30, new CollectorConfiguration().HelloWaitTimeoutSeconds);
        Assert.Equal(300, HelloWaitTimeout.EffectiveSeconds(HelloWaitTimeout.DefaultSeconds));
    }

    [Theory]
    [InlineData(30, 300)]
    [InlineData(300, 300)]
    [InlineData(301, 301)]
    [InlineData(3600, 3600)]
    [InlineData(86400, 3600)]
    [InlineData(0, 300)]
    public void EffectiveSeconds_keeps_the_built_in_window_as_floor_and_caps_at_one_hour(int configured, int expected)
    {
        Assert.Equal(expected, HelloWaitTimeout.EffectiveSeconds(configured));
    }

    [Fact]
    public void Row_without_the_column_reads_as_the_default()
    {
        var entity = TableConfigRepository.ConvertToTenantTableEntity(TenantConfiguration.CreateDefault(TenantId));
        entity.Remove("HelloWaitTimeoutSeconds");

        Assert.Equal(30, TableConfigRepository.ConvertFromTenantTableEntity(entity).HelloWaitTimeoutSeconds);
    }

    [Fact]
    public void Configured_value_roundtrips_store_and_map()
    {
        var config = TenantConfiguration.CreateDefault(TenantId);
        config.HelloWaitTimeoutSeconds = 2400;

        var mapped = TableConfigRepository.ConvertFromTenantTableEntity(
            TableConfigRepository.ConvertToTenantTableEntity(config));

        Assert.Equal(2400, mapped.HelloWaitTimeoutSeconds);
    }
}
