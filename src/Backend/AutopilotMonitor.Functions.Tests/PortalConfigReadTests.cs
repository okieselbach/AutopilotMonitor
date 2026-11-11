using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <c>GET config/{tenantId}</c> is the baseline of the settings editor and of every read-modify-write
/// in the portal, <c>GET config/{tenantId}/feature-flags</c> decides its banners and gates. Both
/// answer with the tenant's row or with an error — never with the synthesized default the plain
/// cached read hands out when storage fails.
/// </summary>
public sealed class PortalConfigReadTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    private static (TenantConfigurationService Service, StoredTenantConfig Store) Service(TenantConfiguration? row)
    {
        var repo = new Mock<IConfigRepository>();
        var store = repo.BackWithRow(row);
        var service = new TenantConfigurationService(
            repo.Object, NullLogger<TenantConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        return (service, store);
    }

    private static TenantConfiguration Row(string manufacturers)
    {
        var row = TenantConfiguration.CreateDefault(TenantId);
        row.ManufacturerWhitelist = manufacturers;
        return row;
    }

    private static string? Manufacturers(Microsoft.Azure.Functions.Worker.Http.HttpResponseData response)
    {
        response.Body.Position = 0;
        using var doc = JsonDocument.Parse(response.Body);
        return doc.RootElement.GetProperty("manufacturerWhitelist").GetString();
    }

    [Fact]
    public async Task Config_answers_with_the_tenants_row()
    {
        var (service, _) = Service(Row("Dell*"));
        var (req, _) = EndpointHarness.Request(TenantId);

        var response = await new GetTenantConfigurationFunction(NullLogger<GetTenantConfigurationFunction>.Instance, service).Run(req, TenantId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Dell*", Manufacturers(response));
    }

    [Fact]
    public async Task Config_answers_a_storage_failure_with_an_error_not_with_defaults()
    {
        var (service, store) = Service(Row("Dell*"));
        store.ThrowOnRead = new InvalidOperationException("storage unavailable");
        var (req, _) = EndpointHarness.Request(TenantId);

        var response = await new GetTenantConfigurationFunction(NullLogger<GetTenantConfigurationFunction>.Instance, service).Run(req, TenantId);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Config_still_creates_the_default_row_for_a_tenant_without_one()
    {
        var (service, store) = Service(null);
        var (req, _) = EndpointHarness.Request(TenantId);

        var response = await new GetTenantConfigurationFunction(NullLogger<GetTenantConfigurationFunction>.Instance, service).Run(req, TenantId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(store.Row);
        Assert.Equal(TenantConfiguration.CreateDefault(TenantId).ManufacturerWhitelist, Manufacturers(response));
    }

    [Fact]
    public async Task Flags_answer_a_storage_failure_with_an_error_not_with_defaults()
    {
        var (service, store) = Service(Row("Dell*"));
        store.ThrowOnRead = new InvalidOperationException("storage unavailable");
        var (req, _) = EndpointHarness.Request(TenantId);
        // The read fails before the homing service or the enrollment counter is asked.
        var function = new GetTenantFeatureFlagsFunction(
            NullLogger<GetTenantFeatureFlagsFunction>.Instance, service, appHomingService: null!, Mock.Of<IMetricsRepository>());

        var response = await function.Run(req, TenantId);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
