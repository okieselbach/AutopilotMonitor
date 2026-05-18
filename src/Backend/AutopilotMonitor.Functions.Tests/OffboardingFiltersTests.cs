using System;
using AutopilotMonitor.Functions.Services.Offboarding;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Plan §5.5 + §10.1 (F6-Fix). The filter helper sits in front of every SafeWipe table
/// query, so injection or range-bleed bugs here turn into cross-tenant data loss. Tests
/// hammer GUID validation, single-quote escaping, and the underscore-tilde upper bound.
/// </summary>
public class OffboardingFiltersTests
{
    private const string ValidTenant = "11111111-1111-1111-1111-111111111111";

    // ── EnsureValidGuid behaviour propagates through every entry point ─────────

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("abc")]
    [InlineData("'; DROP TABLE Sessions; --")]
    [InlineData("")]
    [InlineData("11111111-1111-1111-1111-1111111111111")] // too long
    public void ExactPartition_ThrowsForNonGuid(string input)
    {
        Assert.Throws<ArgumentException>(() => OffboardingFilters.ExactPartition(input));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("'")]
    public void CompositePartitionRange_ThrowsForNonGuid(string input)
    {
        Assert.Throws<ArgumentException>(() => OffboardingFilters.CompositePartitionRange(input));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void DiscriminatorWithTenantProp_ThrowsForNonGuid(string input)
    {
        Assert.Throws<ArgumentException>(() => OffboardingFilters.DiscriminatorWithTenantProp("CodeLookup", input));
    }

    [Fact]
    public void DiscriminatorWithTenantProp_ThrowsForEmptyDiscriminator()
    {
        Assert.Throws<ArgumentException>(() => OffboardingFilters.DiscriminatorWithTenantProp("", ValidTenant));
    }

    [Theory]
    [InlineData("not-a-guid")]
    public void TenantIdProperty_ThrowsForNonGuid(string input)
    {
        Assert.Throws<ArgumentException>(() => OffboardingFilters.TenantIdProperty(input));
    }

    // ── Filter shapes ───────────────────────────────────────────────────────────

    [Fact]
    public void ExactPartition_EmitsCanonicalEqualsClause()
    {
        var filter = OffboardingFilters.ExactPartition(ValidTenant);
        Assert.Equal($"PartitionKey eq '{ValidTenant}'", filter);
    }

    [Fact]
    public void CompositePartitionRange_UsesUnderscoreTildeUpperBound()
    {
        var filter = OffboardingFilters.CompositePartitionRange(ValidTenant);

        // Lower bound must include the underscore anchor — otherwise the range matches
        // "{tenantId}abc" and other tenants whose ids happen to start with the same prefix.
        Assert.Contains($"PartitionKey ge '{ValidTenant}_'", filter);

        // Upper bound MUST be '{tenantId}_~' (underscore-tilde). Switching to '{tenantId}~'
        // would bleed across the next ASCII bucket and pull in foreign rows.
        Assert.Contains($"PartitionKey lt '{ValidTenant}_~'", filter);
        Assert.DoesNotContain($"PartitionKey lt '{ValidTenant}~'", filter);
    }

    [Fact]
    public void DiscriminatorWithTenantProp_RequiresBothAnchorsServerSide()
    {
        var filter = OffboardingFilters.DiscriminatorWithTenantProp("CodeLookup", ValidTenant);

        Assert.Contains("PartitionKey eq 'CodeLookup'", filter);
        Assert.Contains($"TenantId eq '{ValidTenant}'", filter);
        Assert.Contains("and", filter);
    }

    [Fact]
    public void TenantIdProperty_EmitsPropertyOnlyFilter()
    {
        var filter = OffboardingFilters.TenantIdProperty(ValidTenant);
        Assert.Equal($"TenantId eq '{ValidTenant}'", filter);
        Assert.DoesNotContain("PartitionKey", filter);
    }

    // ── Injection neutralization (defense in depth even after EnsureValidGuid) ──

    [Fact]
    public void DiscriminatorWithTenantProp_EscapesDiscriminatorQuotes()
    {
        // Hostile discriminator wouldn't pass through real callers (compile-time constants), but
        // defense in depth: the helper still escapes.
        var filter = OffboardingFilters.DiscriminatorWithTenantProp("a'b", ValidTenant);
        Assert.Contains("PartitionKey eq 'a''b'", filter);
    }
}
