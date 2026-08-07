using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="CloudPcDeviceValidator"/> — focused on pure-function pieces:
/// JSON-to-DTO mapping (incl. exact-match guard on managedDeviceId) and cache key shape.
/// The HTTP/cache/retry resilience is mirrored 1:1 from <c>AutopilotDeviceValidator</c>;
/// behavioural drift would surface here. Response shapes match a real
/// virtualEndpoint/cloudPCs payload (field-captured 2026-08-06 on a W365 tenant).
/// </summary>
public class CloudPcDeviceValidatorTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string DeviceId = "07623d56-1e77-4948-bff5-5bdac8167560";

    // -- BuildCacheKey --

    [Fact]
    public void BuildCacheKey_StableShape()
    {
        var key = CloudPcDeviceValidator.BuildCacheKey(TenantId, DeviceId);
        Assert.Equal($"cloudpc-device-validation:{TenantId}:{DeviceId}", key);
    }

    [Fact]
    public void BuildCacheKey_DistinctFromOtherValidatorKeys()
    {
        // The validators must NOT share cache keys; otherwise a positive hit in one
        // would incorrectly satisfy a lookup in another.
        var key = CloudPcDeviceValidator.BuildCacheKey(TenantId, DeviceId);
        Assert.StartsWith("cloudpc-device-validation:", key);
        Assert.DoesNotContain("autopilot", key);
        Assert.DoesNotContain("device-association", key);
    }

    // -- ParseCloudPcResponse: empty / missing / malformed --

    [Fact]
    public void Parse_EmptyValueArray_NotFound()
    {
        var result = CloudPcDeviceValidator.ParseCloudPcResponse("{\"value\":[]}", DeviceId);
        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Equal(DeviceId, result.IntuneDeviceId);
        Assert.Contains("not a Windows 365 Cloud PC", result.ErrorMessage);
    }

    [Fact]
    public void Parse_MissingValueProperty_NotFound()
    {
        var result = CloudPcDeviceValidator.ParseCloudPcResponse("{}", DeviceId);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Parse_MalformedJson_NotFound_NotTransient()
    {
        // Defensive: bad payload is treated as "not a Cloud PC", not as a transient
        // — transient handling is only triggered by HTTP-level failures.
        var result = CloudPcDeviceValidator.ParseCloudPcResponse("this is not json", DeviceId);
        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
    }

    // -- ParseCloudPcResponse: exact-match guard --

    [Fact]
    public void Parse_DifferentManagedDeviceId_NotFound()
    {
        // Guards against widened filter semantics: even if Graph returns Cloud PCs whose
        // managedDeviceId does not exactly match the queried id, we must reject.
        var body = "{\"value\":[{\"id\":\"423a1fb4-6033-4b69-a31f-e7f3f54194dc\",\"managedDeviceId\":\"99999999-9999-9999-9999-999999999999\"}]}";
        var result = CloudPcDeviceValidator.ParseCloudPcResponse(body, DeviceId);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Parse_ExactMatch_PopulatesAllFields()
    {
        var body = $@"{{
            ""value"":[{{
                ""id"":""423a1fb4-6033-4b69-a31f-e7f3f54194dc"",
                ""displayName"":""GKT-CloudPC - Obi-Wan Kenobi"",
                ""managedDeviceId"":""{DeviceId}"",
                ""managedDeviceName"":""CPC-cloud-AY5HT"",
                ""userPrincipalName"":""cloudadmin@gktatooine.net"",
                ""servicePlanName"":""Cloud PC Enterprise 2vCPU/4GB/128GB""
            }}]
        }}";

        var result = CloudPcDeviceValidator.ParseCloudPcResponse(body, DeviceId);

        Assert.True(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Equal(DeviceId, result.IntuneDeviceId);
        Assert.Equal("423a1fb4-6033-4b69-a31f-e7f3f54194dc", result.CloudPcId);
        Assert.Equal("CPC-cloud-AY5HT", result.ManagedDeviceName);
        Assert.Equal("Cloud PC Enterprise 2vCPU/4GB/128GB", result.ServicePlanName);
    }

    [Fact]
    public void Parse_CaseInsensitiveIdMatch()
    {
        var body = $@"{{""value"":[{{""id"":""423a1fb4-6033-4b69-a31f-e7f3f54194dc"",""managedDeviceId"":""{DeviceId.ToUpperInvariant()}""}}]}}";
        var result = CloudPcDeviceValidator.ParseCloudPcResponse(body, DeviceId);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_TrimsWhitespace_OnGraphIdBeforeMatching()
    {
        var body = $@"{{""value"":[{{""id"":""423a1fb4-6033-4b69-a31f-e7f3f54194dc"",""managedDeviceId"":""  {DeviceId}  ""}}]}}";
        var result = CloudPcDeviceValidator.ParseCloudPcResponse(body, DeviceId);
        Assert.True(result.IsValid);
    }
}
