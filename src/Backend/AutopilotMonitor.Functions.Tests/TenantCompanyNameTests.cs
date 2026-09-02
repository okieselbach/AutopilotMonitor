using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// CompanyName is the second half of the tenant contact profile (next to ContactEmail):
/// optional on Community, required at the Pro entry point. These tests pin the storage
/// roundtrip and the validator's contract — empty is valid, the value is trimmed, and
/// only control characters / over-length are rejected.
/// </summary>
public class TenantCompanyNameTests
{
    private const string TenantId = "77777777-7777-7777-7777-777777777777";

    [Fact]
    public void CompanyName_defaults_to_null_so_absence_is_distinguishable_from_empty()
    {
        Assert.Null(new TenantConfiguration().CompanyName);
    }

    [Fact]
    public void CompanyName_survives_the_table_roundtrip_and_legacy_rows_map_to_null()
    {
        var config = new TenantConfiguration
        {
            TenantId = TenantId,
            DomainName = "contoso.com",
            UpdatedBy = "admin@contoso.com",
            CompanyName = "Contoso Ltd.",
        };

        var entity = TableConfigRepository.ConvertToTenantTableEntity(config);
        Assert.Equal("Contoso Ltd.", TableConfigRepository.ConvertFromTenantTableEntity(entity).CompanyName);

        entity.Remove("CompanyName"); // row written before the column existed
        Assert.Null(TableConfigRepository.ConvertFromTenantTableEntity(entity).CompanyName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Contoso Ltd.")]
    [InlineData("Müller & Söhne GmbH / Co. KG")]
    [InlineData("株式会社コントソ")]
    public void ValidateCompanyName_accepts_empty_and_anything_a_human_types(string? value)
    {
        Assert.Null(TenantConfigValidation.ValidateCompanyName(value));
    }

    [Fact]
    public void ValidateCompanyName_accepts_exactly_the_maximum_length()
    {
        Assert.Null(TenantConfigValidation.ValidateCompanyName(new string('a', TenantConfigValidation.MaxCompanyNameLength)));
    }

    [Fact]
    public void ValidateCompanyName_rejects_over_length()
    {
        var error = TenantConfigValidation.ValidateCompanyName(new string('a', TenantConfigValidation.MaxCompanyNameLength + 1));
        Assert.Contains("at most", error);
    }

    [Theory]
    [InlineData("Contoso\nLtd.")]
    [InlineData("Contoso\tLtd.")]
    [InlineData("Contoso\u0000")]
    public void ValidateCompanyName_rejects_control_characters(string value)
    {
        // The value is rendered in ops events, mails and the admin UI — a line break must
        // never let it masquerade as a second line.
        Assert.Contains("control characters", TenantConfigValidation.ValidateCompanyName(value));
    }

    [Fact]
    public void ValidateModel_surfaces_the_company_name_error()
    {
        var config = TenantConfiguration.CreateDefault(TenantId);
        config.CompanyName = "bad\u0001name";

        var error = TenantConfigValidation.ValidateModel(config, TenantConfiguration.CreateDefault(TenantId), isGlobalAdmin: true);

        Assert.StartsWith("Invalid company name:", error);
    }
}
