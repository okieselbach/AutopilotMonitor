using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The agent security gate stamps two dimensions onto the request row through
/// <c>FunctionContext.Items</c>: the tenant it proved (<c>ValidatedTenantId</c>) and the outcome of
/// the device-validation chain (<c>DeviceValidation</c>). These tests pin
///   - that a request which passes the gate carries both, and one rejected before the chain
///     carries neither (a rejected certificate must not attribute the row to the claimed tenant),
///   - that the middleware takes the proven tenant before the <c>X-Tenant-Id</c> header and never
///     takes the header on a route that does not validate it — register and config take the tenant
///     from body/query, so a client-sent header there is attacker-controlled.
/// </summary>
public class RequestRowMarkersTests
{
    private const string RegisterPath = "/api/agent/register-session";
    private const string ConfigPath = "/api/agent/config";
    private const string TelemetryPath = "/api/agent/telemetry";
    private const string OtherTenant = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    // ---------------------------------------------------------------- validator stamps

    [Fact]
    public async Task ValidateRequest_PassingTheGate_StampsProvenTenantAndDeviceValidation()
    {
        var items = new Dictionary<object, object>();
        var validator = CertTenantBindingTests.BuildValidator(Mock.Of<ILogger>());
        var tenantId = CertTenantBindingTests.SampleCertTenantId.ToString();

        var result = await validator.ValidateRequestAsync(
            CertTenantBindingTests.BuildRequestWithCert(CertTenantBindingTests.SampleCertBase64(), items),
            tenantId);

        Assert.True(result.IsValid);
        Assert.Equal(tenantId, Assert.Contains(RequestRowMarkers.ValidatedTenantKey, items));
        // The harness runs the tenant without a device validator (AllowInsecureAgentRequests).
        Assert.Equal(RequestRowMarkers.DeviceValidation.None, Assert.Contains(RequestRowMarkers.DeviceValidationKey, items));
    }

    [Fact]
    public async Task ValidateRequest_CertificateOfAnotherTenant_StampsNeither()
    {
        var items = new Dictionary<object, object>();
        var validator = CertTenantBindingTests.BuildValidator(Mock.Of<ILogger>());

        var result = await validator.ValidateRequestAsync(
            CertTenantBindingTests.BuildRequestWithCert(CertTenantBindingTests.SampleCertBase64(), items),
            tenantId: OtherTenant);

        Assert.False(result.IsValid);
        // The row must not be attributed to the tenant the caller merely claimed …
        Assert.DoesNotContain(RequestRowMarkers.ValidatedTenantKey, items);
        // … and the device chain never ran, so there is no outcome to report.
        Assert.DoesNotContain(RequestRowMarkers.DeviceValidationKey, items);
        // The binding outcome itself still rides along (denominator for the cert-tenant telemetry).
        Assert.Equal(CertTenantBinding.Outcome.Mismatch, Assert.Contains(CertTenantBinding.RequestItemKey, items));
    }

    [Fact]
    public void DeviceValidationValue_IsTheValidatorName_AndNoneForUnknown()
    {
        Assert.Equal("None", RequestRowMarkers.DeviceValidationValue(ValidatorType.Unknown));
        foreach (var validator in Enum.GetValues<ValidatorType>().Where(v => v != ValidatorType.Unknown))
            Assert.Equal(validator.ToString(), RequestRowMarkers.DeviceValidationValue(validator));

        // The three non-validator values must never collide with a validator name.
        var validatorNames = Enum.GetNames<ValidatorType>();
        Assert.DoesNotContain(RequestRowMarkers.DeviceValidation.None, validatorNames);
        Assert.DoesNotContain(RequestRowMarkers.DeviceValidation.Transient, validatorNames);
        Assert.DoesNotContain(RequestRowMarkers.DeviceValidation.Rejected, validatorNames);
    }

    // ---------------------------------------------------------------- middleware attribution

    [Fact]
    public void ResolveTenantId_PolicyTenantWins()
    {
        var items = new Dictionary<object, object> { [RequestRowMarkers.ValidatedTenantKey] = OtherTenant };
        Assert.Equal("policy-tenant", RequestTelemetryMiddleware.ResolveTenantId("policy-tenant", items, RegisterPath, OtherTenant));
    }

    [Theory]
    [InlineData(RegisterPath)]
    [InlineData(ConfigPath)]
    [InlineData(TelemetryPath)]
    public void ResolveTenantId_ProvenTenantBeatsTheHeaderOnEveryRoute(string path)
    {
        var proven = CertTenantBindingTests.SampleCertTenantId.ToString();
        var items = new Dictionary<object, object> { [RequestRowMarkers.ValidatedTenantKey] = proven };
        Assert.Equal(proven, RequestTelemetryMiddleware.ResolveTenantId(null, items, path, OtherTenant));
    }

    [Theory]
    [InlineData(RegisterPath)]
    [InlineData(ConfigPath)]
    [InlineData("/api/agent/upload-url")]
    [InlineData("/api/bootstrap/register-session")]
    public void ResolveTenantId_HeaderIsIgnoredOnRoutesThatDoNotValidateIt(string path)
    {
        // Without a stamp from the validator (the request never passed the gate) a client-sent
        // header must not attribute the row to any tenant.
        Assert.Null(RequestTelemetryMiddleware.ResolveTenantId(null, new Dictionary<object, object>(), path, OtherTenant));
    }

    [Fact]
    public void ResolveTenantId_HeaderIsHonoredOnlyOnTrustedRoutes_AndOnlyAsGuid()
    {
        var none = new Dictionary<object, object>();
        Assert.Equal(OtherTenant, RequestTelemetryMiddleware.ResolveTenantId(null, none, TelemetryPath, OtherTenant));
        Assert.Equal(OtherTenant, RequestTelemetryMiddleware.ResolveTenantId(null, none, "/api/agent/distress", OtherTenant));
        Assert.Null(RequestTelemetryMiddleware.ResolveTenantId(null, none, TelemetryPath, "not-a-guid"));
        Assert.Null(RequestTelemetryMiddleware.ResolveTenantId(null, none, TelemetryPath, null));
    }
}
