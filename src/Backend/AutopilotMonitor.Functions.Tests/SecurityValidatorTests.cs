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
}
