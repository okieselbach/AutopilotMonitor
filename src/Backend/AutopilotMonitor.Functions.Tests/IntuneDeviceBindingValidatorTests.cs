using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="IntuneDeviceBindingValidator"/> — the pure pieces: the managedDevice
/// JSON-to-DTO mapping (incl. the exact-match guard on the returned id), the enrolledDateTime
/// parsing that the enrollment-race measurement depends on, and the cache key shape.
/// The HTTP/cache/retry resilience mirrors <see cref="CloudPcDeviceValidator"/> 1:1.
/// </summary>
public class IntuneDeviceBindingValidatorTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string DeviceId = "07bc0167-9061-43b7-b243-0bb6aeda736e";

    // -- BuildCacheKey --

    [Fact]
    public void BuildCacheKey_StableShape()
    {
        var key = IntuneDeviceBindingValidator.BuildCacheKey(TenantId, DeviceId);
        Assert.Equal($"intune-device-binding:{TenantId}:{DeviceId}", key);
    }

    [Fact]
    public void BuildCacheKey_DistinctFromOtherValidatorKeys()
    {
        // A shared prefix would let a positive hit in one validator satisfy a lookup in another.
        var key = IntuneDeviceBindingValidator.BuildCacheKey(TenantId, DeviceId);
        Assert.StartsWith("intune-device-binding:", key);
        Assert.DoesNotContain("cloudpc", key);
        Assert.DoesNotContain("autopilot", key);
    }

    [Fact]
    public void BuildCacheKey_IsTenantScoped()
    {
        // The whole point of the check is that the same device id must not resolve across
        // tenants — a tenant-blind cache key would defeat it.
        var a = IntuneDeviceBindingValidator.BuildCacheKey(TenantId, DeviceId);
        var b = IntuneDeviceBindingValidator.BuildCacheKey("22222222-2222-2222-2222-222222222222", DeviceId);
        Assert.NotEqual(a, b);
    }

    // -- ParseManagedDeviceResponse --

    [Fact]
    public void Parse_MatchingDevice_ReturnsMatchWithDiagnostics()
    {
        var body = $$"""
        {
          "id": "{{DeviceId}}",
          "deviceName": "TEST-DEVICE-01",
          "enrolledDateTime": "2026-08-24T15:20:00Z",
          "azureADDeviceId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
          "managementState": "managed"
        }
        """;

        var result = IntuneDeviceBindingValidator.ParseManagedDeviceResponse(body, DeviceId);

        Assert.Equal(IntuneDeviceBindingOutcome.Match, result.Outcome);
        Assert.True(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Equal("TEST-DEVICE-01", result.DeviceName);
        Assert.Equal("managed", result.ManagementState);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 24, 15, 20, 0, TimeSpan.Zero),
            result.EnrolledDateTime);
    }

    [Fact]
    public void Parse_DifferentIdInBody_IsNotAMatch()
    {
        // Guards against a redirected or widened lookup being taken as confirmation of the id
        // we actually asked about.
        var body = """
        { "id": "99999999-9999-9999-9999-999999999999", "deviceName": "OTHER" }
        """;

        var result = IntuneDeviceBindingValidator.ParseManagedDeviceResponse(body, DeviceId);

        Assert.Equal(IntuneDeviceBindingOutcome.NotFound, result.Outcome);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Parse_IdCasingDiffers_StillMatches()
    {
        // Graph echoes GUIDs lowercase, but casing must never decide a security outcome.
        var body = $$"""
        { "id": "{{DeviceId.ToUpperInvariant()}}", "deviceName": "TEST-DEVICE-01" }
        """;

        var result = IntuneDeviceBindingValidator.ParseManagedDeviceResponse(body, DeviceId);

        Assert.Equal(IntuneDeviceBindingOutcome.Match, result.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{ "id": "" }""")]
    [InlineData("""{ "deviceName": "no id field" }""")]
    public void Parse_MalformedOrEmpty_IsNotFoundNotAnException(string body)
    {
        var result = IntuneDeviceBindingValidator.ParseManagedDeviceResponse(body, DeviceId);

        Assert.Equal(IntuneDeviceBindingOutcome.NotFound, result.Outcome);
        Assert.Equal(DeviceId, result.IntuneDeviceId);
    }

    [Fact]
    public void Parse_MissingEnrolledDateTime_LeavesItNull()
    {
        var body = $$"""
        { "id": "{{DeviceId}}", "deviceName": "TEST-DEVICE-01" }
        """;

        var result = IntuneDeviceBindingValidator.ParseManagedDeviceResponse(body, DeviceId);

        Assert.Equal(IntuneDeviceBindingOutcome.Match, result.Outcome);
        Assert.Null(result.EnrolledDateTime);
    }

    // -- ParseEnrolledDateTime --

    [Fact]
    public void ParseEnrolledDateTime_IntuneZeroDate_IsAbsenceNotADate()
    {
        // Intune returns 0001-01-01T00:00:00Z when it has no enrollment timestamp. Treating that
        // as a real date would report an object age of ~2000 years and poison the race analysis.
        Assert.Null(IntuneDeviceBindingValidator.ParseEnrolledDateTime("0001-01-01T00:00:00Z"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-date")]
    public void ParseEnrolledDateTime_UnusableInput_IsNull(string? raw)
    {
        Assert.Null(IntuneDeviceBindingValidator.ParseEnrolledDateTime(raw));
    }

    [Fact]
    public void ParseEnrolledDateTime_NormalizesToUtc()
    {
        // A device object created minutes ago is the signature of an enrollment race, so the
        // instant has to be comparable regardless of the offset Graph reports it in.
        var parsed = IntuneDeviceBindingValidator.ParseEnrolledDateTime("2026-08-24T17:20:00+02:00");

        Assert.NotNull(parsed);
        Assert.Equal(TimeSpan.Zero, parsed!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 15, 20, 0, TimeSpan.Zero), parsed.Value);
    }

    // -- result semantics --

    [Theory]
    [InlineData(IntuneDeviceBindingOutcome.NotFound)]
    [InlineData(IntuneDeviceBindingOutcome.NoDeviceIdInCert)]
    [InlineData(IntuneDeviceBindingOutcome.PermissionMissing)]
    public void DefinitiveNegatives_AreNotTransient(IntuneDeviceBindingOutcome outcome)
    {
        // Only Transient may be retried / left uncached. A missing grant or a foreign device is a
        // state, not an outage — retrying cannot change either.
        var result = new IntuneDeviceBindingResult { Outcome = outcome };

        Assert.False(result.IsTransient);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void AsCacheHit_FlagsTheCopyAndLeavesTheCachedInstanceAlone()
    {
        // The cached instance is shared across every request for that device. Marking it in place
        // would make the first (real) lookup look cached too, and the log gate would then drop the
        // one line per lookup we actually want to keep.
        var cached = new IntuneDeviceBindingResult
        {
            Outcome = IntuneDeviceBindingOutcome.Match,
            IntuneDeviceId = DeviceId,
            DeviceName = "TEST-DEVICE-01",
            EnrolledDateTime = new DateTimeOffset(2026, 8, 24, 16, 51, 0, TimeSpan.Zero),
        };

        var hit = cached.AsCacheHit();

        Assert.True(hit.ServedFromCache);
        Assert.False(cached.ServedFromCache);
        Assert.NotSame(cached, hit);

        // Everything the log line reports must survive the copy.
        Assert.Equal(cached.Outcome, hit.Outcome);
        Assert.Equal(cached.IntuneDeviceId, hit.IntuneDeviceId);
        Assert.Equal(cached.DeviceName, hit.DeviceName);
        Assert.Equal(cached.EnrolledDateTime, hit.EnrolledDateTime);
    }

    [Fact]
    public void FreshResult_IsNotFlaggedAsCached()
    {
        // A freshly parsed Graph response must be loggable — that is the one line per lookup.
        var body = $$"""
        { "id": "{{DeviceId}}", "deviceName": "TEST-DEVICE-01" }
        """;

        var result = IntuneDeviceBindingValidator.ParseManagedDeviceResponse(body, DeviceId);

        Assert.False(result.ServedFromCache);
    }

    [Fact]
    public void TransientOutcome_IsNeitherValidNorDefinitive()
    {
        var result = new IntuneDeviceBindingResult { Outcome = IntuneDeviceBindingOutcome.Transient };

        Assert.True(result.IsTransient);
        Assert.False(result.IsValid);
    }
}
