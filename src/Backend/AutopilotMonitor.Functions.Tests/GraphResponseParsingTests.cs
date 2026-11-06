using System;
using System.Globalization;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.GraphResolution;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The contract of the Graph and Entra token-endpoint response parsers (D-420): what every realistic body,
/// and every malformed one, yields. Written against the Newtonsoft parsers before their move to
/// System.Text.Json and kept as their specification. Tests whose name ends in _D1 to _D7 pin a
/// deliberate difference of that move (listed in D-420).
/// </summary>
public class GraphResponseParsingTests
{
    private const string Serial = "PF3XKQ7";
    private const string IntuneId = "6f1c4d2a-0b3e-4c5d-8e9f-1a2b3c4d5e6f";

    // ── GraphTokenService.ReadTokenResponse ─────────────────────────────────────

    [Theory]
    [InlineData("""{"access_token":"t","expires_in":3599}""", 3599L)]
    [InlineData("""{"access_token":"t","expires_in":"3599"}""", 3599L)]
    [InlineData("""{"access_token":"t","expires_in":" 3599 "}""", 3599L)]
    [InlineData("""{"access_token":"t","expires_in":"+3599"}""", 3599L)]
    [InlineData("""{"access_token":"t",/*c*/"expires_in":3599,}""", 3599L)]
    [InlineData("""{"access_token":"t"}""", null)]
    [InlineData("""{"access_token":"t","expires_in":null}""", null)]
    public void Token_response_reads_the_token_and_its_lifetime(string body, long? expiresIn)
    {
        var (token, lifetime) = GraphTokenService.ReadTokenResponse(body);
        Assert.Equal("t", token);
        Assert.Equal(expiresIn, lifetime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"access_token":""}""")]
    [InlineData("""{"access_token":null,"expires_in":"abc"}""")]
    public void Token_response_without_a_token_yields_none_and_ignores_the_lifetime(string body)
    {
        var (token, lifetime) = GraphTokenService.ReadTokenResponse(body);
        Assert.True(string.IsNullOrEmpty(token));
        Assert.Null(lifetime);
    }

    [Theory]
    [InlineData("<html>busy</html>")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("""{"access_token":"t"} x""")]
    public void Token_response_that_is_not_one_object_throws(string body)
        => Assert.ThrowsAny<Exception>(() => GraphTokenService.ReadTokenResponse(body));

    // D5: an expires_in that is not a whole number counts as absent; the token stays usable with the
    // default lifetime. Newtonsoft threw on the first five and coerced the last two (3600, 1).
    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("\"\"")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("99999999999999999999")]
    [InlineData("3599.5")]
    [InlineData("true")]
    public void Token_response_with_an_unusable_lifetime_keeps_the_token_D5(string expiresIn)
    {
        var (token, lifetime) = GraphTokenService.ReadTokenResponse($$"""{"access_token":"t","expires_in":{{expiresIn}}}""");
        Assert.Equal("t", token);
        Assert.Null(lifetime);
    }

    // ── EntraTokenRejection.Parse ───────────────────────────────────────────────

    [Theory]
    [InlineData("""{"error_codes":[7000229],"error_description":"AADSTS7000229: missing"}""", "AADSTS7000229")]
    [InlineData("""{"error_codes":["7000229",53003]}""", "AADSTS53003")]
    [InlineData("""{"error_codes":[7000229.0],"error_description":"AADSTS50000: x"}""", "AADSTS50000")]
    [InlineData("""{"error_codes":7000229,"error_description":"AADSTS50000: x"}""", "AADSTS50000")]
    [InlineData("""{"error_codes":[],"error_description":"AADSTS50000: x"}""", "AADSTS50000")]
    [InlineData("""{"error_codes":[1],"error_codes":[7000229]}""", "AADSTS7000229")]
    [InlineData("""{"error_codes":[7000229,],/*c*/}""", "AADSTS7000229")]
    public void Entra_rejection_reads_the_first_integer_code_then_the_description(string body, string code)
        => Assert.Equal(code, EntraTokenRejection.Parse(400, body).ErrorCode);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("\"AADSTS7000229\"")]
    [InlineData("42")]
    [InlineData("[1,2]")]
    [InlineData("<html>busy</html>")]
    [InlineData("""{"error_description":null}""")]
    [InlineData("""{"error_description":42}""")]
    [InlineData("""{"error_codes":[7000229]} x""")]
    public void Entra_rejection_without_a_readable_object_keeps_only_the_status(string? body)
    {
        var rejection = EntraTokenRejection.Parse(400, body);
        Assert.Equal(400, rejection.StatusCode);
        Assert.Null(rejection.ErrorCode);
        Assert.Null(rejection.Description);
    }

    // D3: a description is kept as sent, even when it looks like a date (Newtonsoft dropped it).
    [Fact]
    public void Entra_rejection_keeps_a_date_shaped_description_D3()
        => Assert.Equal("2026-11-01T08:00:00Z", EntraTokenRejection.Parse(400, """{"error_description":"2026-11-01T08:00:00Z"}""").Description);

    // D6: an error code beyond Int64 is skipped like any other unusable entry (Newtonsoft threw).
    [Fact]
    public void Entra_rejection_skips_an_out_of_range_code_D6()
        => Assert.Equal("AADSTS53003", EntraTokenRejection.Parse(400, """{"error_codes":[99999999999999999999,53003]}""").ErrorCode);

    // ── AutopilotDeviceValidator.FindRegistration ───────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"value":"oops"}""")]
    [InlineData("""{"value":[]}""")]
    [InlineData("""{"value":[{"serialNumber":null}]}""")]
    [InlineData("""{"value":[{"serialNumber":"OTHER"}]}""")]
    public void Autopilot_list_without_the_serial_is_not_registered(string body)
        => Assert.Null(AutopilotDeviceValidator.FindRegistration(body, Serial));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"value":[]} x""")]
    public void Autopilot_list_that_is_not_one_object_throws(string body)
        => Assert.ThrowsAny<Exception>(() => AutopilotDeviceValidator.FindRegistration(body, Serial));

    [Fact]
    public void Autopilot_list_with_a_null_entry_before_the_match_throws()
        => Assert.Throws<InvalidOperationException>(() => AutopilotDeviceValidator.FindRegistration(
            """{"value":[null,{"id":"ap-1","serialNumber":"PF3XKQ7"}]}""", Serial));

    [Fact]
    public void Autopilot_list_matches_the_trimmed_serial_case_insensitively_and_stops_there()
    {
        var registration = AutopilotDeviceValidator.FindRegistration(
            """{"value":[{"id":"ap-1","serialNumber":" pf3xkq7 ","groupTag":" Sales ","purchaseOrderIdentifier":"PO-7","displayName":"KIOSK-1","userPrincipalName":"u@contoso.com"},null]}""",
            Serial);

        Assert.NotNull(registration);
        Assert.Equal("ap-1", registration!.DeviceId);
        Assert.Equal("Sales", registration.GroupTag);
        Assert.Equal("PO-7", registration.PurchaseOrderId);
        Assert.Equal("KIOSK-1", registration.DisplayName);
        Assert.True(registration.UserAssigned);
    }

    [Fact]
    public void Autopilot_registration_drops_blank_and_non_text_values_and_cuts_long_text()
    {
        var longText = new string('x', AutopilotDeviceValidator.MaxRegistrationFieldLength + 10);
        var registration = AutopilotDeviceValidator.FindRegistration(
            $$"""{"value":[{"id":"   ","serialNumber":"PF3XKQ7","groupTag":true,"purchaseOrderIdentifier":{},"displayName":"{{longText}}","userPrincipalName":null}]}""",
            Serial);

        Assert.NotNull(registration);
        Assert.Null(registration!.DeviceId);
        Assert.Null(registration.GroupTag);
        Assert.Null(registration.PurchaseOrderId);
        Assert.Equal(AutopilotDeviceValidator.MaxRegistrationFieldLength, registration.DisplayName!.Length);
        Assert.False(registration.UserAssigned);
    }

    // D4: a serial that is not text never matches (Newtonsoft matched through its text).
    [Fact]
    public void Autopilot_list_with_a_numeric_serial_does_not_match_D4()
        => Assert.Null(AutopilotDeviceValidator.FindRegistration("""{"value":[{"id":"ap-1","serialNumber":12345}]}""", "12345"));

    // D3: registration text is kept as sent, even when it looks like a date (Newtonsoft dropped it).
    [Fact]
    public void Autopilot_registration_keeps_date_shaped_text_D3()
    {
        var registration = AutopilotDeviceValidator.FindRegistration(
            """{"value":[{"id":"ap-1","serialNumber":"PF3XKQ7","groupTag":"2026-01-01T00:00:00Z","displayName":"/Date(1713096021000)/"}]}""",
            Serial);

        Assert.Equal("2026-01-01T00:00:00Z", registration!.GroupTag);
        Assert.Equal("/Date(1713096021000)/", registration.DisplayName);
    }

    // ── CorporateIdentifierValidator.ReadIdentityPage ───────────────────────────

    private static readonly string CorporateId =
        CorporateIdentifierValidator.BuildNormalizedIdentifier("Microsoft Corporation", "Virtual Machine", "1234-5678");

    [Theory]
    [InlineData("", "")]
    [InlineData("null", "")]
    [InlineData("{}", "")]
    [InlineData("""{"value":null}""", "")]
    [InlineData("""{"value":[],"@odata.nextLink":""}""", "")]
    [InlineData("""{"value":[],"@odata.nextLink":null}""", "")]
    [InlineData("""{"value":{},"@odata.nextLink":"https://graph.microsoft.com/beta/next"}""", "https://graph.microsoft.com/beta/next")]
    [InlineData("""{"value":[{"importedDeviceIdentityType":"serialNumber","importedDeviceIdentifier":"1234-5678"}]}""", "")]
    public void Corporate_page_without_the_identifier_yields_the_next_url(string body, string nextUrl)
        => Assert.Equal((false, nextUrl), CorporateIdentifierValidator.ReadIdentityPage(body, CorporateId));

    [Theory]
    [InlineData("""{"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"Microsoft Corporation,Virtual Machine,1234-5678"}],"@odata.nextLink":"https://graph.microsoft.com/beta/next"}""")]
    [InlineData("""{"value":[{"importedDeviceIdentityType":"MANUFACTURERMODELSERIAL","importedDeviceIdentifier":"MICROSOFTCORPORATION,VIRTUALMACHINE,12345678"}]}""")]
    [InlineData("""{"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":null},{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"Microsoft Corporation,Virtual Machine,1234-5678"}]}""")]
    public void Corporate_page_with_the_identifier_is_found(string body)
        => Assert.Equal((true, string.Empty), CorporateIdentifierValidator.ReadIdentityPage(body, CorporateId));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Corporate_page_that_is_not_one_object_throws(string body)
        => Assert.ThrowsAny<Exception>(() => CorporateIdentifierValidator.ReadIdentityPage(body, CorporateId));

    [Fact]
    public void Corporate_page_with_a_null_entry_before_the_match_throws()
        => Assert.Throws<InvalidOperationException>(() => CorporateIdentifierValidator.ReadIdentityPage(
            """{"value":[null,{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"Microsoft Corporation,Virtual Machine,1234-5678"}]}""",
            CorporateId));

    [Theory]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("{}")]
    public void Corporate_page_with_a_non_text_next_link_never_yields_a_page_to_follow(string nextLink)
    {
        // The scan must stop as a failure: either the read throws or the url cannot be requested.
        try
        {
            var (found, nextUrl) = CorporateIdentifierValidator.ReadIdentityPage($$"""{"value":[],"@odata.nextLink":{{nextLink}}}""", CorporateId);
            Assert.False(found);
            Assert.False(string.IsNullOrEmpty(nextUrl));
            Assert.False(Uri.IsWellFormedUriString(nextUrl, UriKind.Absolute));
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // Throwing ends the scan as transient, like an unusable url does.
        }
    }

    // D4: an identifier that is not text never matches (Newtonsoft matched an array through its text).
    [Fact]
    public void Corporate_page_with_an_array_identifier_does_not_match_D4()
        => Assert.False(CorporateIdentifierValidator.ReadIdentityPage(
            """{"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":["Microsoft Corporation,Virtual Machine,1234-5678"]}]}""",
            CorporateId).Found);

    // ── DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse ─────────

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("not json")]
    [InlineData("{} x")]
    [InlineData("""{"value":null}""")]
    [InlineData("""{"value":{}}""")]
    [InlineData("""{"value":[{"serialNumber":"OTHER"}]}""")]
    public void Association_list_without_the_serial_is_not_found(string body)
    {
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, Serial);
        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
    }

    [Fact]
    public void Association_list_with_a_null_entry_before_the_match_throws()
        => Assert.Throws<InvalidOperationException>(() => DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(
            """{"value":[null,{"serialNumber":"PF3XKQ7"}]}""", Serial));

    [Fact]
    public void Association_list_reads_the_matching_device_and_stops_there()
    {
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(
            """{"value":[{"serialNumber":" pf3xkq7 ","associationState":"associated","devicePreparationPolicyId":"pol-1","preassociatedByUserPrincipalName":"a@contoso.com","assignedToUserPrincipalName":"u@contoso.com","managedDeviceId":"md-1","preassociationDateTime":"2026-05-08T12:16:49Z"},null]}""",
            Serial);

        Assert.True(result.IsValid);
        Assert.Equal("associated", result.AssociationState);
        Assert.Equal("pol-1", result.DevicePreparationPolicyId);
        Assert.Equal("a@contoso.com", result.PreAssociatedByUserPrincipalName);
        Assert.Equal("u@contoso.com", result.AssignedToUserPrincipalName);
        Assert.Equal("md-1", result.ManagedDeviceId);
        Assert.Equal(new DateTime(2026, 5, 8, 12, 16, 49, DateTimeKind.Utc), result.PreAssociationDateTime);
        Assert.Equal(DateTimeKind.Utc, result.PreAssociationDateTime!.Value.Kind);
    }

    [Theory]
    [InlineData("""{"value":[{"serialNumber":"PF3XKQ7"}]}""")]
    [InlineData("""{"value":[{"serialNumber":"PF3XKQ7","associationDateTime":null}]}""")]
    [InlineData("""{"value":[{"serialNumber":"PF3XKQ7","associationDateTime":""}]}""")]
    [InlineData("""{"value":[{"serialNumber":"PF3XKQ7","associationDateTime":"0001-01-01T00:00:00Z"}]}""")]
    [InlineData("""{"value":[{"serialNumber":"PF3XKQ7","associationDateTime":"not-a-date"}]}""")]
    public void Association_date_that_is_unset_reads_as_null(string body)
        => Assert.Null(DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(body, Serial).AssociationDateTime);

    // D1: JSON null in a text field is absent (Newtonsoft read it as an empty string).
    [Fact]
    public void Association_fields_that_are_json_null_read_as_null_D1()
    {
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(
            """{"value":[{"serialNumber":"PF3XKQ7","associationState":null,"assignedToUserPrincipalName":null,"managedDeviceId":null}]}""",
            Serial);
        Assert.Null(result.AssociationState);
        Assert.Null(result.AssignedToUserPrincipalName);
        Assert.Null(result.ManagedDeviceId);
    }

    // D4: text whose escapes are no valid UTF-16 reads as absent, in the serial of another entry and in a field
    // of the match, instead of failing the whole lookup (Newtonsoft read it into a broken string).
    [Fact]
    public void Association_text_with_a_broken_escape_reads_as_null_D4()
    {
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(
            """{"value":[{"serialNumber":"\ud800"},{"serialNumber":"PF3XKQ7","assignedToUserPrincipalName":"u\ud800"}]}""", Serial);
        Assert.True(result.IsValid);
        Assert.Null(result.AssignedToUserPrincipalName);
    }

    // D2: the date keeps its fraction, in any culture (Newtonsoft cut it to whole seconds).
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("en-GB")]
    [InlineData("th-TH")]
    public void Association_date_keeps_its_fraction_in_any_culture_D2(string culture)
    {
        var date = InCulture(culture, () => DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(
            """{"value":[{"serialNumber":"PF3XKQ7","preassociationDateTime":"2026-05-08T12:16:49.1234567Z"}]}""",
            Serial).PreAssociationDateTime);

        Assert.Equal(new DateTime(2026, 5, 8, 12, 16, 49, DateTimeKind.Utc).AddTicks(1234567), date);
        Assert.Equal(DateTimeKind.Utc, date!.Value.Kind);
    }

    // ── CloudPcDeviceValidator.ParseCloudPcResponse ─────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""{"value":[]} x""")]
    [InlineData("""{"value":null}""")]
    [InlineData("""{"value":[{"managedDeviceId":"other"}]}""")]
    public void CloudPc_list_without_the_device_is_not_found(string body)
    {
        var result = CloudPcDeviceValidator.ParseCloudPcResponse(body, IntuneId);
        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
    }

    [Fact]
    public void CloudPc_list_with_a_null_entry_before_the_match_throws()
        => Assert.Throws<InvalidOperationException>(() => CloudPcDeviceValidator.ParseCloudPcResponse(
            $$"""{"value":[null,{"managedDeviceId":"{{IntuneId}}"}]}""", IntuneId));

    [Fact]
    public void CloudPc_list_reads_the_matching_cloud_pc()
    {
        var result = CloudPcDeviceValidator.ParseCloudPcResponse(
            $$"""{"value":[{"id":"cpc-1","managedDeviceId":" {{IntuneId.ToUpperInvariant()}} ","managedDeviceName":"CPC-ADM-1","servicePlanName":"Cloud PC Enterprise"},null]}""",
            IntuneId);

        Assert.True(result.IsValid);
        Assert.Equal("cpc-1", result.CloudPcId);
        Assert.Equal("CPC-ADM-1", result.ManagedDeviceName);
        Assert.Equal("Cloud PC Enterprise", result.ServicePlanName);
    }

    // D1: JSON null in a text field is absent (Newtonsoft read it as an empty string).
    [Fact]
    public void CloudPc_fields_that_are_json_null_read_as_null_D1()
    {
        var result = CloudPcDeviceValidator.ParseCloudPcResponse(
            $$"""{"value":[{"managedDeviceId":"{{IntuneId}}","id":null,"managedDeviceName":null,"servicePlanName":null}]}""", IntuneId);
        Assert.True(result.IsValid);
        Assert.Null(result.CloudPcId);
        Assert.Null(result.ManagedDeviceName);
        Assert.Null(result.ServicePlanName);
    }

    // ── IntuneDeviceBindingValidator.ParseManagedDeviceResponse ─────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("not json")]
    [InlineData("""[{"id":"6f1c4d2a-0b3e-4c5d-8e9f-1a2b3c4d5e6f"}]""")]
    [InlineData("""{"id":"6f1c4d2a-0b3e-4c5d-8e9f-1a2b3c4d5e6f"} x""")]
    [InlineData("""{"id":null}""")]
    [InlineData("""{"id":42}""")]
    [InlineData("""{"id":"other"}""")]
    public void Managed_device_that_is_not_the_requested_one_is_not_found(string body)
        => Assert.Equal(IntuneDeviceBindingOutcome.NotFound, IntuneDeviceBindingValidator.ParseManagedDeviceResponse(body, IntuneId).Outcome);

    [Theory]
    [InlineData("\"managed\"", IntuneDeviceBindingOutcome.Match)]
    [InlineData("null", IntuneDeviceBindingOutcome.Match)]
    [InlineData("\"retirePending\"", IntuneDeviceBindingOutcome.NotManaged)]
    [InlineData("\"WIPEISSUED\"", IntuneDeviceBindingOutcome.NotManaged)]
    [InlineData("\"deletePending\"", IntuneDeviceBindingOutcome.NotManaged)]
    public void Managed_device_on_its_way_out_is_not_managed(string managementState, IntuneDeviceBindingOutcome outcome)
        => Assert.Equal(outcome, IntuneDeviceBindingValidator.ParseManagedDeviceResponse(
            $$"""{"id":" {{IntuneId.ToUpperInvariant()}} ","managementState":{{managementState}}}""", IntuneId).Outcome);

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", null)]
    [InlineData("\"true\"", null)]
    [InlineData("1", null)]
    public void Managed_device_registration_flag_is_strictly_boolean(string value, bool? expected)
        => Assert.Equal(expected, IntuneDeviceBindingValidator.ParseManagedDeviceResponse(
            $$"""{"id":"{{IntuneId}}","azureADRegistered":{{value}}}""", IntuneId).AzureAdRegistered);

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void Managed_device_telemetry_fields_that_are_blank_read_as_null(string value)
    {
        var result = IntuneDeviceBindingValidator.ParseManagedDeviceResponse(
            $$"""{"id":"{{IntuneId}}","serialNumber":{{value}},"managedDeviceOwnerType":{{value}},"deviceEnrollmentType":{{value}},"enrolledDateTime":{{value}}}""",
            IntuneId);
        Assert.Null(result.SerialNumber);
        Assert.Null(result.OwnerType);
        Assert.Null(result.EnrollmentType);
        Assert.Null(result.EnrolledDateTime);
    }

    [Fact]
    public void Managed_device_reads_its_fields()
    {
        var result = IntuneDeviceBindingValidator.ParseManagedDeviceResponse(
            $$"""{"id":"{{IntuneId}}","deviceName":"DESKTOP-1","azureADDeviceId":"aad-1","managementState":"managed","serialNumber":"PF3XKQ7","managedDeviceOwnerType":"company","deviceEnrollmentType":"windowsAzureADJoin","enrolledDateTime":"2026-05-08T12:16:49Z"}""",
            IntuneId);

        Assert.Equal(IntuneDeviceBindingOutcome.Match, result.Outcome);
        Assert.Equal("DESKTOP-1", result.DeviceName);
        Assert.Equal("aad-1", result.AzureAdDeviceId);
        Assert.Equal("PF3XKQ7", result.SerialNumber);
        Assert.Equal("company", result.OwnerType);
        Assert.Equal("windowsAzureADJoin", result.EnrollmentType);
        Assert.Equal(new DateTimeOffset(2026, 5, 8, 12, 16, 49, TimeSpan.Zero), result.EnrolledDateTime);
    }

    [Fact]
    public void Managed_device_unset_enrollment_date_reads_as_null()
        => Assert.Null(IntuneDeviceBindingValidator.ParseManagedDeviceResponse(
            $$"""{"id":"{{IntuneId}}","enrolledDateTime":"0001-01-01T00:00:00Z"}""", IntuneId).EnrolledDateTime);

    // D1: JSON null in a text field is absent (Newtonsoft read it as an empty string).
    [Fact]
    public void Managed_device_fields_that_are_json_null_read_as_null_D1()
    {
        var result = IntuneDeviceBindingValidator.ParseManagedDeviceResponse(
            $$"""{"id":"{{IntuneId}}","deviceName":null,"azureADDeviceId":null,"managementState":null}""", IntuneId);
        Assert.Equal(IntuneDeviceBindingOutcome.Match, result.Outcome);
        Assert.Null(result.DeviceName);
        Assert.Null(result.AzureAdDeviceId);
        Assert.Null(result.ManagementState);
    }

    // D2: the enrollment date keeps its fraction and its day and month in any culture (Newtonsoft cut the
    // fraction and swapped day and month on day-first cultures).
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("en-GB")]
    [InlineData("th-TH")]
    public void Managed_device_enrollment_date_is_the_same_in_any_culture_D2(string culture)
    {
        var date = InCulture(culture, () => IntuneDeviceBindingValidator.ParseManagedDeviceResponse(
            $$"""{"id":"{{IntuneId}}","enrolledDateTime":"2026-05-08T12:16:49.1234567Z"}""", IntuneId).EnrolledDateTime);

        Assert.Equal(new DateTimeOffset(2026, 5, 8, 12, 16, 49, TimeSpan.Zero).AddTicks(1234567), date);
        Assert.Equal(TimeSpan.Zero, date!.Value.Offset);
    }

    // D4: an id that is not text never matches (Newtonsoft matched through its text).
    [Fact]
    public void Managed_device_with_a_numeric_id_is_not_found_D4()
        => Assert.Equal(IntuneDeviceBindingOutcome.NotFound,
            IntuneDeviceBindingValidator.ParseManagedDeviceResponse("""{"id":12345}""", "12345").Outcome);

    // ── Dates in any offset (new with System.Text.Json) ─────────────────────────

    // Newtonsoft turned an offset date into machine-local time and read that back as UTC, so the instant
    // depended on the host's time zone; it was only right on a UTC host.
    [Theory]
    [InlineData("2026-05-08T14:16:49+02:00")]
    [InlineData("2026-05-08T07:16:49-05:00")]
    [InlineData("2026-05-08T12:16:49Z")]
    [InlineData("2026-05-08T12:16:49")]
    public void Dates_in_any_offset_read_as_the_same_utc_instant(string value)
    {
        var expected = new DateTime(2026, 5, 8, 12, 16, 49, DateTimeKind.Utc);

        var association = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(
            $$"""{"value":[{"serialNumber":"PF3XKQ7","preassociationDateTime":"{{value}}"}]}""", Serial).PreAssociationDateTime;
        var enrolled = IntuneDeviceBindingValidator.ParseManagedDeviceResponse(
            $$"""{"id":"{{IntuneId}}","enrolledDateTime":"{{value}}"}""", IntuneId).EnrolledDateTime;

        Assert.Equal(expected, association);
        Assert.Equal(DateTimeKind.Utc, association!.Value.Kind);
        Assert.Equal(new DateTimeOffset(expected), enrolled);
        Assert.Equal(TimeSpan.Zero, enrolled!.Value.Offset);
    }

    // ── ScriptDisplayNameResolver.ReadScriptPage / ReadScriptDisplayName ────────

    [Fact]
    public void Script_page_keeps_object_entries_with_an_id_and_the_next_url()
    {
        var (scripts, next) = ScriptDisplayNameResolver.ReadScriptPage(
            """{"value":[1,"x",null,[],{"displayName":"no id"},{"id":null},{"id":""},{"id":"s1","displayName":"A","fileName":"a.ps1"},{"id":"s2","displayName":"B"}],"@odata.nextLink":"https://graph.microsoft.com/beta/p2"}""",
            withFileName: true);

        Assert.Equal(new[]
        {
            new ScriptDisplayNameResolver.ScriptListItem("s1", "A", "a.ps1"),
            new ScriptDisplayNameResolver.ScriptListItem("s2", "B", null),
        }, scripts);
        Assert.Equal("https://graph.microsoft.com/beta/p2", next);
    }

    [Theory]
    [InlineData("""{"value":[{"id":"s1"}]}""")]
    [InlineData("""{"value":[],"@odata.nextLink":null}""")]
    [InlineData("""{"value":[],"@odata.nextLink":""}""")]
    [InlineData("""{"value":"oops"}""")]
    [InlineData("{}")]
    public void Script_page_without_a_next_link_ends_the_pull(string body)
        => Assert.True(string.IsNullOrEmpty(ScriptDisplayNameResolver.ReadScriptPage(body, withFileName: false).NextUrl));

    [Fact]
    public void Script_page_of_health_scripts_reads_no_file_name()
        => Assert.Null(Assert.Single(ScriptDisplayNameResolver.ReadScriptPage(
            """{"value":[{"id":"s1","displayName":"A","fileName":"a.ps1"}]}""", withFileName: false).Scripts).FileName);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Script_page_or_name_that_is_not_one_object_throws(string body)
    {
        Assert.ThrowsAny<Exception>(() => ScriptDisplayNameResolver.ReadScriptPage(body, withFileName: false));
        Assert.ThrowsAny<Exception>(() => ScriptDisplayNameResolver.ReadScriptDisplayName(body));
    }

    [Theory]
    [InlineData("""{"displayName":"Install agent"}""", "Install agent")]
    [InlineData("""{"id":"s1"}""", null)]
    public void Script_name_reads_the_display_name(string body, string? name)
        => Assert.Equal(name, ScriptDisplayNameResolver.ReadScriptDisplayName(body));

    // D1: a display name that is JSON null is absent (Newtonsoft read it as an empty string).
    [Fact]
    public void Script_name_that_is_json_null_reads_as_null_D1()
    {
        Assert.Null(ScriptDisplayNameResolver.ReadScriptDisplayName("""{"displayName":null}"""));
        Assert.Null(Assert.Single(ScriptDisplayNameResolver.ReadScriptPage(
            """{"value":[{"id":"s1","displayName":null}]}""", withFileName: false).Scripts).DisplayName);
    }

    // D3: a display name is kept as sent in any culture, even when it looks like a date (Newtonsoft
    // rewrote it in the current culture, "1/1/2026 12:00:00 AM" in en-US).
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("th-TH")]
    public void Script_name_shaped_like_a_date_is_kept_as_sent_D3(string culture)
        => Assert.Equal("2026-01-01T00:00:00Z", InCulture(culture, () =>
            ScriptDisplayNameResolver.ReadScriptDisplayName("""{"displayName":"2026-01-01T00:00:00Z"}""")));

    // D4: a display name that is not text is absent (Newtonsoft read a number as its text).
    [Fact]
    public void Script_name_that_is_a_number_reads_as_null_D4()
        => Assert.Null(ScriptDisplayNameResolver.ReadScriptDisplayName("""{"displayName":42}"""));

    // ── D7: a body cut off mid-object is unreadable ─────────────────────────────

    // Newtonsoft's DeserializeObject read the part before the cut as if the open containers were closed, so a
    // cut-off list could still match. Every parser now answers it like any other unreadable body. HttpClient
    // throws on a premature end before a parser sees the body, so only a server-side cut can get here.

    [Fact]
    public void Token_response_cut_off_throws_D7()
        => Assert.ThrowsAny<Exception>(() => GraphTokenService.ReadTokenResponse("""{"access_token":"t","expires_in":3599"""));

    [Fact]
    public void Entra_rejection_cut_off_keeps_only_the_status_D7()
    {
        var rejection = EntraTokenRejection.Parse(400, """{"error_codes":[53003""");
        Assert.Null(rejection.ErrorCode);
        Assert.Null(rejection.Description);
    }

    [Fact]
    public void Autopilot_list_cut_off_throws_D7()
        => Assert.ThrowsAny<Exception>(() => AutopilotDeviceValidator.FindRegistration(
            """{"value":[{"id":"ap-1","serialNumber":"PF3XKQ7"}""", Serial));

    [Fact]
    public void Corporate_page_cut_off_throws_D7()
        => Assert.ThrowsAny<Exception>(() => CorporateIdentifierValidator.ReadIdentityPage(
            """{"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"Microsoft Corporation,Virtual Machine,1234-5678"}""",
            CorporateId));

    [Fact]
    public void Association_list_cut_off_is_not_found_D7()
    {
        var result = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse("""{"value":[{"serialNumber":"PF3XKQ7"}""", Serial);
        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
    }

    [Fact]
    public void CloudPc_list_cut_off_is_not_found_D7()
    {
        var result = CloudPcDeviceValidator.ParseCloudPcResponse($$"""{"value":[{"managedDeviceId":"{{IntuneId}}"}""", IntuneId);
        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
    }

    [Fact]
    public void Managed_device_cut_off_is_not_found_D7()
        => Assert.Equal(IntuneDeviceBindingOutcome.NotFound, IntuneDeviceBindingValidator.ParseManagedDeviceResponse(
            $$"""{"id":"{{IntuneId}}","azureADRegistered":true""", IntuneId).Outcome);

    private static T InCulture<T>(string culture, Func<T> read)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            return read();
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
