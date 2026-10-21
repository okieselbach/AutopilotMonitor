using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for input validation, sanitization, and security properties of the distress channel.
///
/// SECURITY GUARD: The distress endpoint is unauthenticated. These validations prevent:
///   - IP spoofing/parsing exploits (<see cref="ClientIpExtractor"/>, rightmost-hop policy)
///   - Control character injection and field overflow (Sanitize)
///   - Tenant ID injection attacks (GuidPattern)
///   - Replay attacks with stale timestamps (IsDistressTimestampValid)
///   - Invalid enum values bypassing typed deserialization (DistressErrorType)
///
/// All validation failures return 200 OK — zero information leakage to attackers.
/// </summary>
public class DistressValidationTests
{
    // Fixed reference time for deterministic timestamp tests
    private static readonly DateTime FixedUtcNow = new(2026, 3, 30, 12, 0, 0, DateTimeKind.Utc);

    // =========================================================================
    // ClientIpExtractor.ExtractTrustedHop — X-Forwarded-For RIGHTMOST-hop parsing.
    //
    // App Service appends "<client-ip>:<port>" to X-Forwarded-For when it forwards
    // to the Functions worker, so the trustworthy entry is the rightmost one. Any
    // entry to the left of it was supplied by the (untrusted) client and would let
    // an attacker rotate the per-IP rate-limit key on every request.
    // =========================================================================

    [Fact]
    public void ExtractTrustedHop_SimpleIpv4_ReturnsIp()
    {
        Assert.Equal("10.0.0.1", ClientIpExtractor.ExtractTrustedHop("10.0.0.1"));
    }

    [Fact]
    public void ExtractTrustedHop_Ipv4WithPort_StripsPort()
    {
        Assert.Equal("10.0.0.1", ClientIpExtractor.ExtractTrustedHop("10.0.0.1:12345"));
    }

    [Fact]
    public void ExtractTrustedHop_MultipleProxies_TakesRightmost()
    {
        // The rightmost entry is the one App Service appended — anything to the left
        // is client/upstream-proxy controlled.
        Assert.Equal("172.16.0.1", ClientIpExtractor.ExtractTrustedHop("10.0.0.1, 192.168.1.1, 172.16.0.1"));
    }

    [Fact]
    public void ExtractTrustedHop_SpoofedLeftmostIgnored_TakesAppendedHop()
    {
        // Threat model: attacker sends "X-Forwarded-For: 1.1.1.1" and rotates the value
        // on every request. App Service appends the real client IP (with port) to the
        // right of it; we MUST trust that rightmost value, not the spoofed prefix.
        Assert.Equal(
            "203.0.113.5",
            ClientIpExtractor.ExtractTrustedHop("1.1.1.1, 203.0.113.5:54321"));
    }

    [Fact]
    public void ExtractTrustedHop_BracketedIpv6WithPort_ExtractsAddress()
    {
        Assert.Equal("::1", ClientIpExtractor.ExtractTrustedHop("[::1]:12345"));
    }

    [Fact]
    public void ExtractTrustedHop_BareIpv6_ReturnsAsIs()
    {
        Assert.Equal("2001:db8::1", ClientIpExtractor.ExtractTrustedHop("2001:db8::1"));
    }

    [Fact]
    public void ExtractTrustedHop_FullIpv6_ReturnsAsIs()
    {
        var full = "2001:0db8:85a3:0000:0000:8a2e:0370:7334";
        Assert.Equal(full, ClientIpExtractor.ExtractTrustedHop(full));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ExtractTrustedHop_NullOrEmpty_ReturnsUnknown(string? value)
    {
        Assert.Equal("unknown", ClientIpExtractor.ExtractTrustedHop(value));
    }

    [Fact]
    public void ExtractTrustedHop_WhitespaceOnly_ReturnsUnknown()
    {
        Assert.Equal("unknown", ClientIpExtractor.ExtractTrustedHop("   "));
    }

    [Fact]
    public void ExtractTrustedHop_MultipleProxiesWithIpv6Last_TakesRightmostBracketedIpv6()
    {
        Assert.Equal("::1", ClientIpExtractor.ExtractTrustedHop("10.0.0.1, [::1]:443"));
    }

    [Fact]
    public void ExtractTrustedHop_Ipv4MappedIpv6_ReturnsAsIs()
    {
        // Multiple colons → treated as bare IPv6 (no port suffix to strip)
        Assert.Equal("::ffff:10.0.0.1", ClientIpExtractor.ExtractTrustedHop("::ffff:10.0.0.1"));
    }

    [Fact]
    public void ExtractTrustedHop_Ipv4WithLeadingWhitespace_Trimmed()
    {
        Assert.Equal("10.0.0.1", ClientIpExtractor.ExtractTrustedHop("  10.0.0.1  "));
    }

    [Fact]
    public void ExtractTrustedHop_TrailingEmptyEntry_SkipsToLastNonEmpty()
    {
        // Tolerant of stray trailing commas — defensive, not expected from App Service.
        Assert.Equal("203.0.113.5", ClientIpExtractor.ExtractTrustedHop("10.0.0.1, 203.0.113.5, "));
    }

    // =========================================================================
    // ClientIpExtractor.GetHeaderFromBindingDataJson — Flex Consumption strips the
    // X-Forwarded-* family from the proxied HTTP request; the host passes the
    // ORIGINAL header set as trigger metadata (BindingData["Headers"], JSON object).
    // Malformed metadata must fail soft (null), never throw into request handling.
    // =========================================================================

    [Fact]
    public void GetHeaderFromBindingDataJson_XffPresent_ReturnsValue()
    {
        var json = """{"Host":"example.com","X-Forwarded-For":"1.1.1.1, 203.0.113.5:443"}""";
        Assert.Equal(
            "1.1.1.1, 203.0.113.5:443",
            ClientIpExtractor.GetHeaderFromBindingDataJson(json, "X-Forwarded-For"));
    }

    [Fact]
    public void GetHeaderFromBindingDataJson_HeaderNameCaseInsensitive()
    {
        var json = """{"x-forwarded-for":"203.0.113.5"}""";
        Assert.Equal(
            "203.0.113.5",
            ClientIpExtractor.GetHeaderFromBindingDataJson(json, "X-Forwarded-For"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]                                // JSON but not an object
    [InlineData("""{"Host":"example.com"}""")]        // header absent
    [InlineData("""{"X-Forwarded-For":42}""")]        // non-string value
    public void GetHeaderFromBindingDataJson_MissingOrMalformed_ReturnsNull(string? json)
    {
        Assert.Null(ClientIpExtractor.GetHeaderFromBindingDataJson(json, "X-Forwarded-For"));
    }

    // =========================================================================
    // ClientIpExtractor.RemoteIpToString — transport-level fallback. On Flex the
    // worker's TCP peer is the host's LOCAL proxy: loopback means "not a client"
    // and must map to "unknown", never be stored as a forensic SourceIp
    // (production 2026-07-29: distress reports carried SourceIp 127.0.0.1).
    // =========================================================================

    [Fact]
    public void RemoteIpToString_Null_ReturnsUnknown()
    {
        Assert.Equal("unknown", ClientIpExtractor.RemoteIpToString(null));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]  // IPv4-mapped loopback
    public void RemoteIpToString_Loopback_ReturnsUnknown(string ip)
    {
        Assert.Equal("unknown", ClientIpExtractor.RemoteIpToString(System.Net.IPAddress.Parse(ip)));
    }

    [Fact]
    public void RemoteIpToString_PublicIpv4_ReturnsIp()
    {
        Assert.Equal("203.0.113.5", ClientIpExtractor.RemoteIpToString(System.Net.IPAddress.Parse("203.0.113.5")));
    }

    [Fact]
    public void RemoteIpToString_Ipv4MappedIpv6_FoldsToIpv4()
    {
        Assert.Equal("203.0.113.5", ClientIpExtractor.RemoteIpToString(System.Net.IPAddress.Parse("::ffff:203.0.113.5")));
    }

    // =========================================================================
    // Sanitize — Control character stripping and truncation
    // =========================================================================

    [Fact]
    public void Sanitize_Null_ReturnsNull()
    {
        Assert.Null(ReportDistressFunction.Sanitize(null, 64));
    }

    [Fact]
    public void Sanitize_Empty_ReturnsNull()
    {
        Assert.Null(ReportDistressFunction.Sanitize("", 64));
    }

    [Fact]
    public void Sanitize_NormalString_Unchanged()
    {
        Assert.Equal("Dell Inc.", ReportDistressFunction.Sanitize("Dell Inc.", 64));
    }

    [Fact]
    public void Sanitize_ExactlyAtMaxLength_NotTruncated()
    {
        var input = new string('A', ReportDistressFunction.MaxStringField64);
        var result = ReportDistressFunction.Sanitize(input, ReportDistressFunction.MaxStringField64);
        Assert.Equal(64, result!.Length);
    }

    [Fact]
    public void Sanitize_OneOverMaxLength_Truncated()
    {
        var input = new string('A', ReportDistressFunction.MaxStringField64 + 1);
        var result = ReportDistressFunction.Sanitize(input, ReportDistressFunction.MaxStringField64);
        Assert.Equal(ReportDistressFunction.MaxStringField64, result!.Length);
    }

    [Fact]
    public void Sanitize_ControlChars_Stripped()
    {
        // \x00 (null byte) and \x01 (SOH) should be stripped
        Assert.Equal("DellInc", ReportDistressFunction.Sanitize("Dell\x00Inc\x01", 64));
    }

    [Fact]
    public void Sanitize_NullByte_Stripped()
    {
        Assert.Equal("malicious", ReportDistressFunction.Sanitize("\x00malicious", 64));
    }

    [Fact]
    public void Sanitize_TabPreserved()
    {
        // Tab (0x09) is NOT in the control char regex [\x00-\x08\x0B\x0C\x0E-\x1F]
        var result = ReportDistressFunction.Sanitize("before\tafter", 64);
        Assert.Contains("\t", result);
    }

    [Fact]
    public void Sanitize_NewlinePreserved()
    {
        // Newline (0x0A) is NOT in the control char regex
        var result = ReportDistressFunction.Sanitize("line1\nline2", 64);
        Assert.Contains("\n", result);
    }

    [Fact]
    public void Sanitize_CarriageReturnPreserved()
    {
        // CR (0x0D) is NOT in the control char regex
        var result = ReportDistressFunction.Sanitize("line1\rline2", 64);
        Assert.Contains("\r", result);
    }

    [Fact]
    public void Sanitize_OnlyControlChars_ReturnsEmptyString()
    {
        // After stripping control chars and trimming → empty string (not null)
        var result = ReportDistressFunction.Sanitize("\x00\x01\x02", 64);
        Assert.Equal("", result);
    }

    [Fact]
    public void Sanitize_LeadingTrailingWhitespace_Trimmed()
    {
        Assert.Equal("Dell", ReportDistressFunction.Sanitize("  Dell  ", 64));
    }

    [Fact]
    public void Sanitize_TruncationAfterStripping()
    {
        // 70 total chars, 10 are control chars → after stripping = 60 chars → under 64 limit
        var input = new string('A', 30) + new string('\x01', 10) + new string('B', 30);
        var result = ReportDistressFunction.Sanitize(input, 64);
        Assert.Equal(60, result!.Length);
    }

    [Fact]
    public void Sanitize_MessageMaxLength_Applied()
    {
        var input = new string('X', 300);
        var result = ReportDistressFunction.Sanitize(input, ReportDistressFunction.MaxMessageLength);
        Assert.Equal(ReportDistressFunction.MaxMessageLength, result!.Length);
    }

    [Fact]
    public void Sanitize_AgentVersionMaxLength_Applied()
    {
        var input = new string('V', 50);
        var result = ReportDistressFunction.Sanitize(input, ReportDistressFunction.MaxStringField32);
        Assert.Equal(ReportDistressFunction.MaxStringField32, result!.Length);
    }

    // =========================================================================
    // GuidPattern — GUID format validation (anti-injection)
    // =========================================================================

    [Fact]
    public void GuidPattern_ValidLowercaseGuid_Matches()
    {
        Assert.Matches(ReportDistressFunction.GuidPattern, "a1b2c3d4-e5f6-7890-abcd-ef1234567890");
    }

    [Fact]
    public void GuidPattern_ValidUppercaseGuid_Matches()
    {
        Assert.Matches(ReportDistressFunction.GuidPattern, "A1B2C3D4-E5F6-7890-ABCD-EF1234567890");
    }

    [Fact]
    public void GuidPattern_NewGuid_Matches()
    {
        Assert.Matches(ReportDistressFunction.GuidPattern, Guid.NewGuid().ToString());
    }

    [Fact]
    public void GuidPattern_NoDashes_NoMatch()
    {
        Assert.DoesNotMatch(ReportDistressFunction.GuidPattern, "a1b2c3d4e5f67890abcdef1234567890");
    }

    [Fact]
    public void GuidPattern_BracedGuid_NoMatch()
    {
        Assert.DoesNotMatch(ReportDistressFunction.GuidPattern, "{a1b2c3d4-e5f6-7890-abcd-ef1234567890}");
    }

    [Fact]
    public void GuidPattern_SqlInjection_NoMatch()
    {
        Assert.DoesNotMatch(ReportDistressFunction.GuidPattern, "'; DROP TABLE--");
    }

    [Fact]
    public void GuidPattern_PathTraversal_NoMatch()
    {
        Assert.DoesNotMatch(ReportDistressFunction.GuidPattern, "../../../etc/passwd");
    }

    [Fact]
    public void GuidPattern_PartialGuid_NoMatch()
    {
        Assert.DoesNotMatch(ReportDistressFunction.GuidPattern, "a1b2c3d4-e5f6");
    }

    [Fact]
    public void GuidPattern_TrailingSpace_NoMatch()
    {
        // Anchored regex: ^ ... $ — no trailing chars allowed
        Assert.DoesNotMatch(ReportDistressFunction.GuidPattern, "a1b2c3d4-e5f6-7890-abcd-ef1234567890 ");
    }

    [Fact]
    public void GuidPattern_EmptyString_NoMatch()
    {
        Assert.DoesNotMatch(ReportDistressFunction.GuidPattern, "");
    }

    [Fact]
    public void GuidPattern_GuidWithNonHexChars_NoMatch()
    {
        // 'g' is not a hex character
        Assert.DoesNotMatch(ReportDistressFunction.GuidPattern, "g1b2c3d4-e5f6-7890-abcd-ef1234567890");
    }

    // =========================================================================
    // IsDistressTimestampValid — Timestamp boundary validation
    // =========================================================================

    [Fact]
    public void IsDistressTimestampValid_JustNow_Valid()
    {
        Assert.True(ReportDistressFunction.IsDistressTimestampValid(
            FixedUtcNow.AddSeconds(-1), FixedUtcNow));
    }

    [Fact]
    public void IsDistressTimestampValid_ExactlyAtFutureLimit_Valid()
    {
        // 5 minutes in the future is the boundary (age = -5min, condition is >= -5min)
        Assert.True(ReportDistressFunction.IsDistressTimestampValid(
            FixedUtcNow.Add(ReportDistressFunction.MaxTimestampFuture), FixedUtcNow));
    }

    [Fact]
    public void IsDistressTimestampValid_BeyondFutureLimit_Invalid()
    {
        // 6 minutes in the future exceeds tolerance
        Assert.False(ReportDistressFunction.IsDistressTimestampValid(
            FixedUtcNow.AddMinutes(6), FixedUtcNow));
    }

    [Fact]
    public void IsDistressTimestampValid_23HoursAgo_Valid()
    {
        Assert.True(ReportDistressFunction.IsDistressTimestampValid(
            FixedUtcNow.AddHours(-23), FixedUtcNow));
    }

    [Fact]
    public void IsDistressTimestampValid_Exactly24HoursAgo_Valid()
    {
        // Boundary: age == MaxTimestampAge, condition is <= MaxTimestampAge
        Assert.True(ReportDistressFunction.IsDistressTimestampValid(
            FixedUtcNow.Add(-ReportDistressFunction.MaxTimestampAge), FixedUtcNow));
    }

    [Fact]
    public void IsDistressTimestampValid_25HoursAgo_Invalid()
    {
        Assert.False(ReportDistressFunction.IsDistressTimestampValid(
            FixedUtcNow.AddHours(-25), FixedUtcNow));
    }

    [Fact]
    public void IsDistressTimestampValid_DateTimeMinValue_Invalid()
    {
        Assert.False(ReportDistressFunction.IsDistressTimestampValid(
            DateTime.MinValue, FixedUtcNow));
    }

    [Fact]
    public void IsDistressTimestampValid_DateTimeMaxValue_Invalid()
    {
        Assert.False(ReportDistressFunction.IsDistressTimestampValid(
            DateTime.MaxValue, FixedUtcNow));
    }

    // =========================================================================
    // DistressErrorType enum — IsDefined validation
    // =========================================================================

    [Theory]
    [InlineData(DistressErrorType.AuthCertificateMissing)]
    [InlineData(DistressErrorType.AuthCertificateInvalid)]
    [InlineData(DistressErrorType.AuthCertificateRejected)]
    [InlineData(DistressErrorType.HardwareNotAllowed)]
    [InlineData(DistressErrorType.DeviceNotRegistered)]
    [InlineData(DistressErrorType.TenantRejected)]
    [InlineData(DistressErrorType.ConfigFetchDenied)]
    [InlineData(DistressErrorType.SessionRegistrationDenied)]
    [InlineData(DistressErrorType.TpmPssUnsupported)]
    [InlineData(DistressErrorType.ClientCertificateNotReceived)]
    public void EnumIsDefined_AllValidErrorTypes_ReturnsTrue(DistressErrorType errorType)
    {
        Assert.True(Enum.IsDefined(typeof(DistressErrorType), errorType));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    [InlineData(10)]
    [InlineData(999)]
    public void EnumIsDefined_InvalidValues_ReturnsFalse(int rawValue)
    {
        Assert.False(Enum.IsDefined(typeof(DistressErrorType), (DistressErrorType)rawValue));
    }

    [Fact]
    public void DistressErrorType_HasExactly10Values()
    {
        // If someone adds a new enum value, this test forces them to also add test coverage
        var values = Enum.GetValues(typeof(DistressErrorType));
        Assert.Equal(10, values.Length);
    }

    // =========================================================================
    // ControlChars regex — character class verification
    // =========================================================================

    [Theory]
    [InlineData('\x00')]  // NUL
    [InlineData('\x01')]  // SOH
    [InlineData('\x08')]  // BS
    [InlineData('\x0B')]  // VT
    [InlineData('\x0C')]  // FF
    [InlineData('\x0E')]  // SO
    [InlineData('\x1F')]  // US
    public void ControlChars_DangerousChars_AreStripped(char c)
    {
        Assert.Matches(ReportDistressFunction.ControlChars, c.ToString());
    }

    [Theory]
    [InlineData('\x09')]  // Tab — preserved
    [InlineData('\x0A')]  // LF — preserved
    [InlineData('\x0D')]  // CR — preserved
    public void ControlChars_SafeWhitespace_NotStripped(char c)
    {
        Assert.DoesNotMatch(ReportDistressFunction.ControlChars, c.ToString());
    }

    // =========================================================================
    // Constants verification — catch accidental changes to security limits
    // =========================================================================

    [Fact]
    public void Constants_MaxContentLength_IsTheSharedCapOf2048()
    {
        // 1024 → 1536 for the V2 cert-context fields, → 2048 (shared with the agent, which trims
        // its message to fit) for the device-context fields. Rate-limit + sanitize + truncate +
        // enum-validation remain the primary DoS defenses; the size cap is a payload-shape gate.
        Assert.Equal(AutopilotMonitor.Shared.Constants.DistressReportMaxBytes, ReportDistressFunction.MaxContentLength);
        Assert.Equal(2048, ReportDistressFunction.MaxContentLength);
    }

    // =========================================================================
    // Device-context tokens — dropped when off format, never truncated
    // =========================================================================

    [Theory]
    [InlineData("26200.6899", "26200.6899")]
    [InlineData("26100", "26100")]
    [InlineData(" 26300.1234 ", "26300.1234")]
    [InlineData("26200.6899\n", "26200.6899")]
    [InlineData("26200.", null)]
    [InlineData(".6899", null)]
    [InlineData("26200..6899", null)]
    [InlineData("26200.6899 (beta)", null)]
    [InlineData("26200\n.6899", null)]
    [InlineData("1234567890.1234567890.12345678901", null)] // 33 chars, one over the cap
    [InlineData("", null)]
    [InlineData(null, null)]
    public void SanitizeToken_OsBuild(string? input, string? expected)
    {
        Assert.Equal(expected, ReportDistressFunction.SanitizeToken(
            input, ReportDistressFunction.MaxOsBuildLength, ReportDistressFunction.OsBuildPattern));
    }

    [Theory]
    [InlineData("in_progress", "in_progress")]
    [InlineData("completed", "completed")]
    [InlineData("unknown_7", "unknown_7")]
    [InlineData("unavailable", "unavailable")]
    [InlineData("Completed", null)]
    [InlineData("in progress", null)]
    [InlineData("in-progress", null)]
    [InlineData("a_very_long_state_x", null)] // 19 chars > 16
    [InlineData("", null)]
    [InlineData(null, null)]
    public void SanitizeToken_OobeState(string? input, string? expected)
    {
        Assert.Equal(expected, ReportDistressFunction.SanitizeToken(
            input, ReportDistressFunction.MaxOobeStateLength, ReportDistressFunction.OobeStatePattern));
    }

    [Theory]
    [InlineData("device_setup", "device_setup")]
    [InlineData("account_setup_failed", "account_setup_failed")]
    [InlineData("esp_done", "esp_done")]
    [InlineData("device_preparation_failed", "device_preparation_failed")]
    [InlineData("Device_Setup", null)]
    [InlineData("device-setup", null)]
    [InlineData("device_setup2", null)]
    [InlineData("an_esp_phase_token_far_beyond_the_cap", null)]
    [InlineData(null, null)]
    public void SanitizeToken_EspPhase(string? input, string? expected)
    {
        Assert.Equal(expected, ReportDistressFunction.SanitizeToken(
            input, ReportDistressFunction.MaxEspPhaseLength, ReportDistressFunction.EspPhasePattern));
    }

    [Fact]
    public void SanitizeDeviceBoot_KeepsBootsWithinThirtyDaysBeforeTheReport()
    {
        var report = FixedUtcNow;
        Assert.Equal(report.AddMinutes(-20), ReportDistressFunction.SanitizeDeviceBoot(report.AddMinutes(-20), report));
        Assert.Equal(report.AddDays(-30), ReportDistressFunction.SanitizeDeviceBoot(report.AddDays(-30), report));
        Assert.Equal(report.AddMinutes(4), ReportDistressFunction.SanitizeDeviceBoot(report.AddMinutes(4), report));
        Assert.Null(ReportDistressFunction.SanitizeDeviceBoot(report.AddDays(-31), report));
        Assert.Null(ReportDistressFunction.SanitizeDeviceBoot(report.AddMinutes(6), report));
        Assert.Null(ReportDistressFunction.SanitizeDeviceBoot(DateTime.MinValue, report));
        Assert.Null(ReportDistressFunction.SanitizeDeviceBoot(null, report));
    }

    [Fact]
    public void SanitizeToken_OsBuildExactlyAtCap_Kept()
    {
        var atCap = new string('9', ReportDistressFunction.MaxOsBuildLength);
        Assert.Equal(atCap, ReportDistressFunction.SanitizeToken(
            atCap, ReportDistressFunction.MaxOsBuildLength, ReportDistressFunction.OsBuildPattern));
    }

    [Fact]
    public void Sanitize_TpmVersion_StrippedAndCapped()
    {
        var value = "IFX\u0001 7.63.3353.0" + new string('x', 60);
        var clean = ReportDistressFunction.Sanitize(value, ReportDistressFunction.MaxTpmVersionLength);
        Assert.NotNull(clean);
        Assert.StartsWith("IFX 7.63.3353.0", clean);
        Assert.Equal(ReportDistressFunction.MaxTpmVersionLength, clean!.Length);
    }

    // =========================================================================
    // TPM-PSS bell text
    // =========================================================================

    [Fact]
    public void TpmPssBellMessage_NamesDeviceAndTheWayOut()
    {
        var message = ReportDistressFunction.BuildTpmPssBellMessage("SN-TEST-1", "HP", "EliteBook 840 G5");

        Assert.Equal(
            "Device SN-TEST-1 (HP EliteBook 840 G5) could not connect because its TPM cannot sign with RSA-PSS. " +
            "It may connect after setup and a restart; Hardware settings show whether it did. " +
            "Otherwise update the TPM firmware or replace the device.",
            message);
    }

    [Fact]
    public void TpmPssBellMessage_WithoutManufacturerAndModel_NamesSerialOnly()
    {
        var message = ReportDistressFunction.BuildTpmPssBellMessage("SN-TEST-2", null, "");

        Assert.StartsWith("Device SN-TEST-2 could not connect because", message);
    }

    [Fact]
    public void Constants_MaxTimestampAge_Is24Hours()
    {
        Assert.Equal(TimeSpan.FromHours(24), ReportDistressFunction.MaxTimestampAge);
    }

    [Fact]
    public void Constants_MaxTimestampFuture_Is5Minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), ReportDistressFunction.MaxTimestampFuture);
    }
}
