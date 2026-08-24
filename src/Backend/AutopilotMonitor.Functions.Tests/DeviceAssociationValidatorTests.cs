using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="DeviceAssociationValidator"/> — focused on pure-function pieces:
/// JSON-to-DTO mapping (incl. exact-match guard) and cache key shape. The HTTP/cache/retry
/// resilience is mirrored 1:1 from <c>AutopilotDeviceValidator</c>; behavioural drift
/// would surface here.
/// </summary>
public class DeviceAssociationValidatorTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string Serial = "1730-2406-2605-6305-0260-8436-93";

    // -- BuildCacheKey --

    [Fact]
    public void BuildCacheKey_StableShape()
    {
        var key = DeviceAssociationValidator.BuildCacheKey(TenantId, Serial);
        Assert.Equal($"device-association:{TenantId}:{Serial}", key);
    }

    [Fact]
    public void BuildCacheKey_DistinctFromAutopilotValidatorKey()
    {
        // The two validators must NOT share cache keys; otherwise a positive Autopilot
        // hit would incorrectly satisfy a DevPrep lookup.
        var devPrepKey = DeviceAssociationValidator.BuildCacheKey(TenantId, Serial);
        Assert.StartsWith("device-association:", devPrepKey);
        Assert.DoesNotContain("autopilot", devPrepKey);
    }

    // -- ParseTenantAssociatedDevicesResponse: empty / missing / malformed --

    [Fact]
    public void Parse_EmptyValueArray_NotFound()
    {
        var body = "{\"value\":[]}";
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, Serial);
        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Equal(Serial, result.SerialNumber);
        Assert.Contains("not associated", result.ErrorMessage);
    }

    [Fact]
    public void Parse_MissingValueProperty_NotFound()
    {
        var body = "{}";
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, Serial);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Parse_MalformedJson_NotFound_NotTransient()
    {
        // Defensive: bad payload is treated as "not associated", not as a transient
        // — transient handling is only triggered by HTTP-level failures.
        var body = "this is not json";
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, Serial);
        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
    }

    // -- ParseTenantAssociatedDevicesResponse: exact-match guard --

    [Fact]
    public void Parse_OnlySimilarSerial_NotFound()
    {
        // Guards against widened filter semantics: even if Graph returns devices whose serial
        // merely contains the query, we must require an exact match before declaring success.
        var body = $@"{{""value"":[{{""serialNumber"":""{Serial}-OTHER"",""associationState"":""preassociated""}}]}}";
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, Serial);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Parse_ExactSerialMatch_PopulatesAllFields()
    {
        var body = $@"{{
            ""value"":[{{
                ""id"":""f8226ccb-e464-1842-a341-325b2a4fd906"",
                ""serialNumber"":""{Serial}"",
                ""associationState"":""preassociated"",
                ""devicePreparationPolicyId"":""00000000-0000-0000-0000-000000000000"",
                ""preassociationDateTime"":""2026-04-14T12:00:21.5029229Z"",
                ""associationDateTime"":""0001-01-01T00:00:00Z"",
                ""preassociatedByUserPrincipalName"":""admin@contoso.com"",
                ""assignedToUserPrincipalName"":null,
                ""managedDeviceId"":""00000000-0000-0000-0000-000000000000""
            }}]
        }}";

        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, Serial);

        Assert.True(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Equal(Serial, result.SerialNumber);
        Assert.Equal("preassociated", result.AssociationState);
        Assert.Equal("00000000-0000-0000-0000-000000000000", result.DevicePreparationPolicyId);
        Assert.Equal("admin@contoso.com", result.PreAssociatedByUserPrincipalName);
        // Newtonsoft.Json yields "" for JSON null when calling .ToString() on the JToken;
        // downstream telemetry uses IsNullOrEmpty so both null and "" surface as "absent".
        Assert.True(string.IsNullOrEmpty(result.AssignedToUserPrincipalName));
        Assert.NotNull(result.PreAssociationDateTime);
        Assert.Null(result.AssociationDateTime); // Graph "0001-01-01" → null (unset DateTimeOffset sentinel)
    }

    [Fact]
    public void Parse_TrimsWhitespace_OnGraphSerialBeforeMatching()
    {
        // Defensive: protect against Graph stragglers with stray whitespace.
        var body = $@"{{""value"":[{{""serialNumber"":""  {Serial}  "",""associationState"":""preassociated""}}]}}";
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, Serial);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_CaseInsensitiveSerialMatch()
    {
        var lower = Serial.ToLowerInvariant();
        var upper = Serial.ToUpperInvariant();
        var body = $@"{{""value"":[{{""serialNumber"":""{upper}"",""associationState"":""preassociated""}}]}}";
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, lower);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void AsCacheHit_FlagsTheCopyAndLeavesTheCachedInstanceAlone()
    {
        // The cached instance is shared across every request for that serial. Marking it in place
        // would make the first (real) lookup look cached too, and the shadow log gate would then
        // drop the one line per lookup that carries the finding.
        var cached = new DeviceAssociationResult
        {
            IsValid = true,
            SerialNumber = Serial,
            AssociationState = "preassociated",
            DevicePreparationPolicyId = "11111111-2222-3333-4444-555555555555",
            AssignedToUserPrincipalName = "someone@example.invalid",
            ManagedDeviceId = "66666666-7777-8888-9999-000000000000",
        };

        var hit = cached.AsCacheHit();

        Assert.True(hit.ServedFromCache);
        Assert.False(cached.ServedFromCache);
        Assert.NotSame(cached, hit);

        // Every field the shadow log reports must survive the copy.
        Assert.Equal(cached.IsValid, hit.IsValid);
        Assert.Equal(cached.IsTransient, hit.IsTransient);
        Assert.Equal(cached.SerialNumber, hit.SerialNumber);
        Assert.Equal(cached.AssociationState, hit.AssociationState);
        Assert.Equal(cached.DevicePreparationPolicyId, hit.DevicePreparationPolicyId);
        Assert.Equal(cached.AssignedToUserPrincipalName, hit.AssignedToUserPrincipalName);
        Assert.Equal(cached.ManagedDeviceId, hit.ManagedDeviceId);
    }

    [Fact]
    public void FreshlyParsedResult_IsNotFlaggedAsCached()
    {
        // A result straight from a Graph response must be loggable — that is the one line per lookup.
        var body = $@"{{""value"":[{{""serialNumber"":""{Serial}"",""associationState"":""preassociated""}}]}}";

        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, Serial);

        Assert.False(result.ServedFromCache);
    }

}
