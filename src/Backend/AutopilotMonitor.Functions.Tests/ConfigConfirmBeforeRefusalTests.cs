using System.Net;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The agent gate never refuses on a cached configuration another instance's write has outdated:
/// before a refusal the configuration decides, it reads the stored row and decides on that. An admin
/// allows a manufacturer or switches a validation on, and the next device is admitted at once, on
/// every instance. A request the cached copy admits must not cost a read — that is the hot path.
/// The request comes from <see cref="CertTenantBindingTests"/>: a valid certificate of the sample
/// tenant, manufacturer "Contoso", model "TestBook".
/// </summary>
public sealed class ConfigConfirmBeforeRefusalTests
{
    private static readonly string TenantId = CertTenantBindingTests.SampleCertTenantId.ToString();

    private sealed record Gate(SecurityValidator Validator, Mock<TenantConfigurationService> Config, Dictionary<object, object> Items);

    /// <summary>A permissive tenant: no device validator needed, every hardware allowed.</summary>
    private static TenantConfiguration Admitting(Action<TenantConfiguration>? change = null)
    {
        var config = new TenantConfiguration
        {
            TenantId = TenantId,
            AllowInsecureAgentRequests = true,
            ManufacturerWhitelist = "*",
            ModelWhitelist = "*",
        };
        change?.Invoke(config);
        return config;
    }

    /// <summary>
    /// <paramref name="cached"/> is what this instance holds; <paramref name="stored"/> is what a read
    /// before a refusal returns (null: nothing to read, as inside the confirm interval). Like the real
    /// service, a confirming read makes the stored row the cached one.
    /// </summary>
    private static Gate Build(TenantConfiguration cached, TenantConfiguration? stored)
    {
        var configRepo = Mock.Of<IConfigRepository>();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var current = cached;

        var config = new Mock<TenantConfigurationService>(configRepo, Mock.Of<ILogger<TenantConfigurationService>>(), cache)
        { CallBase = false };
        config.Setup(x => x.TryGetConfigurationAsync(It.IsAny<string>())).ReturnsAsync(() => (current, true));
        config.Setup(x => x.ConfirmBeforeRefusalAsync(It.IsAny<string>())).ReturnsAsync(() =>
        {
            if (stored != null) current = stored;
            return stored;
        });

        var adminConfig = new Mock<AdminConfigurationService>(configRepo, Mock.Of<ILogger<AdminConfigurationService>>(), cache)
        { CallBase = false };
        adminConfig.Setup(x => x.GetConfigurationAsync()).ReturnsAsync(new AdminConfiguration());

        var validator = new SecurityValidator(
            configService: config.Object,
            adminConfigService: adminConfig.Object,
            rateLimitService: new RateLimitService(cache, Mock.Of<ILogger<RateLimitService>>()),
            autopilotDeviceValidator: null!,
            corporateIdentifierValidator: null!,
            logger: Mock.Of<ILogger>(),
            bootstrapSessionService: null,
            deviceAssociationValidator: null);
        return new Gate(validator, config, new Dictionary<object, object>());
    }

    private static Task<SecurityValidationResult> Validate(Gate gate)
        => gate.Validator.ValidateRequestAsync(
            CertTenantBindingTests.BuildRequestWithCert(CertTenantBindingTests.SampleCertBase64(), gate.Items), TenantId);

    private static string? Stamp(Gate gate)
        => gate.Items.TryGetValue(RequestRowMarkers.ConfigConfirmKey, out var value) ? value as string : null;

    // ---------------------------------------------------------------- a refusal is confirmed first

    public static TheoryData<string, TenantConfiguration, string> OutdatedCopies() => new()
    {
        { "manufacturer just allowed", Admitting(c => c.ManufacturerWhitelist = "Dell*"), "Hardware not authorized" },
        { "model just allowed", Admitting(c => c.ModelWhitelist = "Latitude*"), "Hardware not authorized" },
        { "validation requirement just met", Admitting(c => c.AllowInsecureAgentRequests = false), "Device validation is required" },
        { "tenant just re-enabled", Admitting(c => { c.Disabled = true; c.DisabledReason = "paused"; }), "Tenant is suspended" },
    };

    [Theory]
    [MemberData(nameof(OutdatedCopies))]
    public async Task The_stored_row_admits_what_the_outdated_copy_would_refuse(string _, TenantConfiguration outdated, string refusal)
    {
        var gate = Build(cached: outdated, stored: Admitting());

        var result = await Validate(gate);

        Assert.True(result.IsValid, $"refused with '{result.ErrorMessage}' although the stored row admits (outdated copy would say '{refusal}')");
        Assert.Equal(RequestRowMarkers.ConfigConfirm.Averted, Stamp(gate));
        gate.Config.Verify(x => x.ConfirmBeforeRefusalAsync(TenantId), Times.Once);
    }

    [Theory]
    [MemberData(nameof(OutdatedCopies))]
    public async Task The_refusal_stands_when_the_stored_row_says_the_same(string _, TenantConfiguration refusing, string refusal)
    {
        var gate = Build(cached: refusing, stored: ConfigRepoMockExtensions.Clone(refusing));

        var result = await Validate(gate);

        Assert.False(result.IsValid);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal(refusal, result.ErrorMessage);
        Assert.Equal(RequestRowMarkers.ConfigConfirm.Confirmed, Stamp(gate));
        gate.Config.Verify(x => x.ConfirmBeforeRefusalAsync(TenantId), Times.Once);
    }

    [Theory]
    [MemberData(nameof(OutdatedCopies))]
    public async Task Without_a_read_the_cached_copy_decides(string _, TenantConfiguration refusing, string refusal)
    {
        // Inside the confirm interval, or storage failed: nothing newer to decide on.
        var gate = Build(cached: refusing, stored: null);

        var result = await Validate(gate);

        Assert.False(result.IsValid);
        Assert.Equal(refusal, result.ErrorMessage);
        Assert.Null(Stamp(gate));
    }

    // ---------------------------------------------------------------- the hot path stays untouched

    [Fact]
    public async Task A_request_the_cached_copy_admits_reads_nothing()
    {
        var gate = Build(cached: Admitting(), stored: Admitting());

        var result = await Validate(gate);

        Assert.True(result.IsValid);
        Assert.Null(Stamp(gate));
        gate.Config.Verify(x => x.ConfirmBeforeRefusalAsync(It.IsAny<string>()), Times.Never);
        gate.Config.Verify(x => x.TryGetConfigurationAsync(TenantId), Times.Once);
    }

    [Fact]
    public async Task A_refusal_the_configuration_does_not_decide_reads_nothing()
    {
        // A certificate of another tenant: the configuration admits, the certificate does not.
        var gate = Build(cached: Admitting(), stored: Admitting());

        var result = await gate.Validator.ValidateRequestAsync(
            CertTenantBindingTests.BuildRequestWithCert(CertTenantBindingTests.SampleCertBase64(), gate.Items),
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        Assert.False(result.IsValid);
        Assert.Null(Stamp(gate));
        gate.Config.Verify(x => x.ConfirmBeforeRefusalAsync(It.IsAny<string>()), Times.Never);
    }

    // ---------------------------------------------------------------- the device validation chain

    [Fact]
    public async Task A_validation_switched_on_since_runs_the_chain_once_more_and_no_further()
    {
        // The Cloud PC validation is on in both rows, but this gate has no Cloud PC validator wired:
        // nothing admits the device, on either pass. What counts here is the control flow.
        var cached = Admitting(c => { c.AllowInsecureAgentRequests = false; c.ValidateCloudPcDevice = true; });
        var stored = Admitting(c => { c.AllowInsecureAgentRequests = false; c.ValidateCloudPcDevice = true; c.ValidateIntuneDeviceBinding = true; });
        var gate = Build(cached, stored);

        var result = await Validate(gate);

        Assert.False(result.IsValid);
        Assert.Equal("Device not registered", result.ErrorMessage);
        Assert.Equal(RequestRowMarkers.ConfigConfirm.Rerun, Stamp(gate));
        gate.Config.Verify(x => x.TryGetConfigurationAsync(TenantId), Times.Exactly(2));
        gate.Config.Verify(x => x.ConfirmBeforeRefusalAsync(TenantId), Times.Once);
    }

    [Fact]
    public async Task The_chain_does_not_run_again_when_the_stored_row_asks_nothing_new()
    {
        var cached = Admitting(c => { c.AllowInsecureAgentRequests = false; c.ValidateCloudPcDevice = true; });
        var gate = Build(cached, stored: ConfigRepoMockExtensions.Clone(cached));

        var result = await Validate(gate);

        Assert.Equal("Device not registered", result.ErrorMessage);
        Assert.Equal(RequestRowMarkers.ConfigConfirm.Confirmed, Stamp(gate));
        gate.Config.Verify(x => x.TryGetConfigurationAsync(TenantId), Times.Once);
    }

    [Fact]
    public void Only_a_validation_that_was_off_and_is_now_on_asks_for_another_pass()
    {
        var used = new TenantConfiguration { ValidateAutopilotDevice = true };

        Assert.False(SecurityValidator.SwitchesOnUnaskedValidation(used, new TenantConfiguration { ValidateAutopilotDevice = true }));
        Assert.False(SecurityValidator.SwitchesOnUnaskedValidation(used, new TenantConfiguration()));
        Assert.True(SecurityValidator.SwitchesOnUnaskedValidation(used, new TenantConfiguration { ValidateAutopilotDevice = true, ValidateCorporateIdentifier = true }));
        Assert.True(SecurityValidator.SwitchesOnUnaskedValidation(used, new TenantConfiguration { ValidateDeviceAssociation = true }));
        Assert.True(SecurityValidator.SwitchesOnUnaskedValidation(used, new TenantConfiguration { ValidateCloudPcDevice = true }));
        Assert.True(SecurityValidator.SwitchesOnUnaskedValidation(used, new TenantConfiguration { ValidateIntuneDeviceBinding = true }));
    }

    // ---------------------------------------------------------------- the patch marker

    [Fact]
    public void A_field_patch_counts_the_sent_fields_the_stored_row_already_held()
    {
        static PatchOutcome Applied(params string[] changed)
            => new(true, PatchFailure.None, null, null, changed, new Dictionary<string, string>(), null);

        // Wire names are camelCase, applied names are the model's property names.
        Assert.Equal(0, PatchTenantConfigurationFieldsFunction.UnchangedFieldCount(new[] { "logLevel" }, Applied("LogLevel")));
        Assert.Equal(1, PatchTenantConfigurationFieldsFunction.UnchangedFieldCount(
            new[] { "enableTimezoneAutoSet", "rebootOnComplete" }, Applied("RebootOnComplete")));
        Assert.Equal(1, PatchTenantConfigurationFieldsFunction.UnchangedFieldCount(new[] { "diagnosticsUploadMode" }, Applied()));
        Assert.Equal(0, PatchTenantConfigurationFieldsFunction.UnchangedFieldCount(
            new[] { "logLevel" }, PatchOutcome.Fail(PatchFailure.ValidationFailed, "invalid")));
    }
}
