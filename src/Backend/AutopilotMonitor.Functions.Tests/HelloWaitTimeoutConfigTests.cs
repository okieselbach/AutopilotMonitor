using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Azure.Data.Tables;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tenant Hello wait (<see cref="TenantConfiguration.HelloWaitTimeoutSeconds"/>): writes must lie
/// in 300..3600 s, and stored rows are read as their effective value — so rows still holding the
/// old 30 s default never block an unrelated save or a backup revert (both validate the whole
/// model) and the patch verify round-trip stays drift-free.
/// </summary>
public class HelloWaitTimeoutConfigTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    [Theory]
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
    [InlineData(299)]
    [InlineData(3601)]
    [InlineData(30)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateModel_rejects_values_outside_the_range(int seconds)
    {
        var candidate = TenantConfiguration.CreateDefault(TenantId);
        candidate.HelloWaitTimeoutSeconds = seconds;

        var error = TenantConfigValidation.ValidateModel(candidate, TenantConfiguration.CreateDefault(TenantId), isGlobalAdmin: true);

        Assert.Equal("Hello wait timeout must be between 300 and 3600 seconds.", error);
    }

    [Fact]
    public void Default_is_the_built_in_five_minutes()
    {
        Assert.Equal(300, TenantConfiguration.CreateDefault(TenantId).HelloWaitTimeoutSeconds);
        Assert.Equal(300, new CollectorConfiguration().HelloWaitTimeoutSeconds);
    }

    [Theory]
    [InlineData(30, 300)]     // old default still stored for many tenants
    [InlineData(1800, 1800)]
    [InlineData(86400, 3600)] // written before the server validated the range
    public void Stored_row_is_read_as_its_effective_value(int stored, int expected)
    {
        var entity = TableConfigRepository.ConvertToTenantTableEntity(TenantConfiguration.CreateDefault(TenantId));
        entity["HelloWaitTimeoutSeconds"] = stored;

        var mapped = TableConfigRepository.ConvertFromTenantTableEntity(entity);

        Assert.Equal(expected, mapped.HelloWaitTimeoutSeconds);
        Assert.Null(TenantConfigValidation.ValidateModel(mapped, mapped, isGlobalAdmin: false));
    }

    [Fact]
    public void Row_without_the_column_reads_as_the_default()
    {
        var entity = TableConfigRepository.ConvertToTenantTableEntity(TenantConfiguration.CreateDefault(TenantId));
        entity.Remove("HelloWaitTimeoutSeconds");

        Assert.Equal(300, TableConfigRepository.ConvertFromTenantTableEntity(entity).HelloWaitTimeoutSeconds);
    }

    [Fact]
    public void Supported_value_roundtrips_store_and_map()
    {
        var config = TenantConfiguration.CreateDefault(TenantId);
        config.HelloWaitTimeoutSeconds = 2400;

        var mapped = TableConfigRepository.ConvertFromTenantTableEntity(
            TableConfigRepository.ConvertToTenantTableEntity(config));

        Assert.Equal(2400, mapped.HelloWaitTimeoutSeconds);
    }
}
