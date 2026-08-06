using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for SecurityValidator GUID validation.
/// StoreEventsBatchAsync relies on EnsureValidGuid to reject invalid TenantId/SessionId values —
/// these tests guard that contract.
/// </summary>
public class SecurityValidatorTests
{
    // --- IsValidGuid ---

    [Theory]
    // Valid: hyphenated 8-4-4-4-12, any casing.
    [InlineData("a1b2c3d4-e5f6-7890-abcd-ef1234567890", true)]   // lowercase
    [InlineData("A1B2C3D4-E5F6-7890-ABCD-EF1234567890", true)]   // uppercase
    [InlineData("a1B2c3D4-e5F6-7890-AbCd-Ef1234567890", true)]   // mixed case
    // Invalid: wrong/absent format.
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]                                   // whitespace only
    [InlineData("not-a-guid", false)]
    [InlineData(@"DESKTOP-DIU8038\defaultuser0", false)]         // regression: agent-as-defaultuser0 sent this as TenantId
    [InlineData("a1b2c3d4e5f67890abcdef1234567890", false)]      // no dashes → not standard format
    [InlineData("{a1b2c3d4-e5f6-7890-abcd-ef1234567890}", false)] // braced {…} not accepted
    public void IsValidGuid_reflects_guid_format(string? input, bool expected)
    {
        Assert.Equal(expected, SecurityValidator.IsValidGuid(input));
    }

    [Fact]
    public void IsValidGuid_WithNewGuid_ReturnsTrue()
    {
        Assert.True(SecurityValidator.IsValidGuid(Guid.NewGuid().ToString()));
    }

    // --- EnsureValidGuid ---

    [Fact]
    public void EnsureValidGuid_WithValidGuid_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            SecurityValidator.EnsureValidGuid(Guid.NewGuid().ToString(), "TenantId"));
        Assert.Null(ex);
    }

    [Fact]
    public void EnsureValidGuid_WithNull_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SecurityValidator.EnsureValidGuid(null, "TenantId"));
        Assert.Contains("TenantId", ex.Message);
    }

    [Fact]
    public void EnsureValidGuid_WithEmptyString_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SecurityValidator.EnsureValidGuid("", "SessionId"));
        Assert.Contains("SessionId", ex.Message);
    }

    [Fact]
    public void EnsureValidGuid_WithInvalidString_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SecurityValidator.EnsureValidGuid("invalid-tenant", "TenantId"));
        Assert.Equal("TenantId", ex.ParamName);
    }

    [Fact]
    public void EnsureValidGuid_ErrorMessage_MentionsParameterName()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SecurityValidator.EnsureValidGuid("bad", "MyParam"));
        Assert.Contains("MyParam", ex.Message);
    }

    // --- OData injection prevention (bootstrap token attack vectors) ---

    [Theory]
    [InlineData("' or '1'='1")]
    [InlineData("' or Token ne '")]
    [InlineData("' or PartitionKey ne 'CodeLookup")]
    [InlineData("a1b2c3d4-e5f6-7890-abcd-ef1234567890' or '1'='1")]
    [InlineData("'; --")]
    [InlineData("' or 1 eq 1 or '")]
    public void IsValidGuid_WithODataInjectionPayload_ReturnsFalse(string payload)
    {
        Assert.False(SecurityValidator.IsValidGuid(payload));
    }

    // --- Intune device id extraction from the MDM client certificate subject ---
    // Intune MDM Device CA certs carry the Intune managedDevice id as the Subject CN
    // (field-verified 2026-08-06 on a W365 Cloud PC). This is the identity anchor for the
    // CloudPc validator, so the parse must be strict: CN present AND a canonical GUID.

    [Fact]
    public void TryGetIntuneDeviceId_PlainCnGuid_Extracts()
    {
        Assert.True(SecurityValidator.TryGetIntuneDeviceIdFromCertSubject(
            "CN=07623d56-1e77-4948-bff5-5bdac8167560", out var id));
        Assert.Equal("07623d56-1e77-4948-bff5-5bdac8167560", id);
    }

    [Fact]
    public void TryGetIntuneDeviceId_UppercaseGuid_NormalizedToLower()
    {
        Assert.True(SecurityValidator.TryGetIntuneDeviceIdFromCertSubject(
            "CN=07623D56-1E77-4948-BFF5-5BDAC8167560", out var id));
        Assert.Equal("07623d56-1e77-4948-bff5-5bdac8167560", id);
    }

    [Fact]
    public void TryGetIntuneDeviceId_MultiRdnSubject_FindsCn()
    {
        Assert.True(SecurityValidator.TryGetIntuneDeviceIdFromCertSubject(
            "O=Contoso, CN=07623d56-1e77-4948-bff5-5bdac8167560, C=DE", out var id));
        Assert.Equal("07623d56-1e77-4948-bff5-5bdac8167560", id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("O=Contoso, C=DE")]                              // no CN at all
    [InlineData("CN=not-a-guid")]                                // CN present but not a GUID
    [InlineData("CN=hostname.contoso.com")]                      // typical non-Intune device cert
    [InlineData("CN=' or '1'='1")]                               // injection payload never survives
    public void TryGetIntuneDeviceId_InvalidSubjects_ReturnFalse(string? subject)
    {
        Assert.False(SecurityValidator.TryGetIntuneDeviceIdFromCertSubject(subject, out var id));
        Assert.Null(id);
    }

    [Fact]
    public void TryGetIntuneDeviceId_NonGuidFirstCn_IsDefinitive()
    {
        // A first CN that is not a GUID means "not an Intune MDM device cert shape" — a
        // later GUID-valued CN must NOT resurrect the parse (no CN-shopping).
        Assert.False(SecurityValidator.TryGetIntuneDeviceIdFromCertSubject(
            "CN=hostname, CN=07623d56-1e77-4948-bff5-5bdac8167560", out _));
    }
}
