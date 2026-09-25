using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.GraphResolution;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Graph;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Intune Enrollment Validation: the certificate's Intune device id admits a device without any
/// pre-registration (<see cref="IntuneDeviceBindingValidator.ValidateAsync"/>), and observes
/// devices another validator admitted (<see cref="IntuneDeviceBindingValidator.ObserveInBackground"/>)
/// — the latter only when the tenant granted the Graph permission, so a tenant without the add-on
/// costs no Graph call at all.
/// </summary>
public class IntuneEnrollmentValidationTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string DeviceId = "07bc0167-9061-43b7-b243-0bb6aeda736e";

    private static readonly DateTimeOffset OldCert = DateTimeOffset.UtcNow.AddDays(-30);
    private static readonly DateTimeOffset RecentCert = DateTimeOffset.UtcNow.AddMinutes(-5);

    private const string MatchBody =
        "{\"id\":\"07bc0167-9061-43b7-b243-0bb6aeda736e\",\"deviceName\":\"TEST-DEVICE-01\",\"managementState\":\"managed\","
        + "\"serialNumber\":\"TESTSERIAL01\",\"managedDeviceOwnerType\":\"company\",\"deviceEnrollmentType\":\"windowsAzureADJoin\"}";

    // ------------------------------------------------------------------ harness

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();

        public ScriptedHandler(params Func<HttpResponseMessage>[] responses) => _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            var next = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return Task.FromResult(next());
        }
    }

    private static Func<HttpResponseMessage> Respond(HttpStatusCode status, string body = "{}")
        => () => new HttpResponseMessage(status) { Content = new StringContent(body) };

    private sealed record Sut(
        IntuneDeviceBindingValidator Validator,
        ScriptedHandler Handler,
        MemoryCache Cache,
        Mock<GraphTokenService> Tokens,
        Mock<IGraphFeatureDetector> Detector);

    private static Sut Build(bool permissionGranted, params Func<HttpResponseMessage>[] responses)
    {
        var handler = new ScriptedHandler(responses.Length == 0 ? new[] { Respond(HttpStatusCode.OK, MatchBody) } : responses);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));

        var cache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EntraId:ClientId"] = "aaaaaaaa-0000-0000-0000-000000000001",
            ["EntraId:ClientSecret"] = "secret",
        }).Build();
        var registry = new EntraAppRegistry(configuration, NullLogger<EntraAppRegistry>.Instance);
        var tenantConfig = new Mock<TenantConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<TenantConfigurationService>.Instance, cache)
        { CallBase = false };
        var tokens = new Mock<GraphTokenService>(
            NullLogger<GraphTokenService>.Instance, Mock.Of<IHttpClientFactory>(), cache,
            configuration, registry, tenantConfig.Object)
        { CallBase = false };
        tokens.Setup(t => t.GetAccessTokenAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GraphTokenResult.Success("token"));

        var detector = new Mock<IGraphFeatureDetector>();
        detector.Setup(d => d.HasPermissionAsync(TenantId, GraphAppPermissions.DeviceManagementManagedDevicesReadAll, It.IsAny<CancellationToken>()))
            .ReturnsAsync(permissionGranted);

        var validator = new IntuneDeviceBindingValidator(
            NullLogger<IntuneDeviceBindingValidator>.Instance, factory.Object, cache, tokens.Object, detector.Object);
        return new Sut(validator, handler, cache, tokens, detector);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Background observation did not settle in time.");
            await Task.Delay(20);
        }
    }

    private static bool InFlight(MemoryCache cache)
        => cache.TryGetValue(IntuneDeviceBindingValidator.BuildCacheKey(TenantId, DeviceId) + ":inflight", out _);

    // ------------------------------------------------------------------ admitting lookups

    [Fact]
    public async Task Admitting_Match_IsCached_SoTheNextRequestCostsNoGraphCall()
    {
        var sut = Build(permissionGranted: true, Respond(HttpStatusCode.OK, MatchBody));

        var first = await sut.Validator.ValidateAsync(TenantId, DeviceId, OldCert, IntuneDeviceBindingRole.Admitting);
        var second = await sut.Validator.ValidateAsync(TenantId, DeviceId, OldCert, IntuneDeviceBindingRole.Admitting);

        Assert.Equal(IntuneDeviceBindingOutcome.Match, first.Outcome);
        Assert.False(first.ServedFromCache);
        Assert.True(second.ServedFromCache);
        Assert.Single(sut.Handler.Requests);
        Assert.Equal("TESTSERIAL01", second.SerialNumber);
    }

    [Fact]
    public async Task Admitting_UsesAPointGetWithTheTelemetrySelect()
    {
        var sut = Build(permissionGranted: true);

        await sut.Validator.ValidateAsync(TenantId, DeviceId.ToUpperInvariant(), OldCert, IntuneDeviceBindingRole.Admitting);

        var uri = Uri.UnescapeDataString(Assert.Single(sut.Handler.Requests).RequestUri!.ToString());
        Assert.Contains($"/v1.0/deviceManagement/managedDevices/{DeviceId}?", uri);
        Assert.DoesNotContain("$filter", uri);
        foreach (var field in new[] { "managementState", "serialNumber", "managedDeviceOwnerType", "deviceEnrollmentType", "azureADRegistered" })
            Assert.Contains(field, uri);
    }

    [Fact]
    public async Task Admitting_NotFound_WithAnOldCertificate_IsDefinitiveAndCached()
    {
        var sut = Build(permissionGranted: true, Respond(HttpStatusCode.NotFound));

        var result = await sut.Validator.ValidateAsync(TenantId, DeviceId, OldCert, IntuneDeviceBindingRole.Admitting);

        Assert.Equal(IntuneDeviceBindingOutcome.NotFound, result.Outcome);
        Assert.Single(sut.Handler.Requests);
        Assert.Equal(IntuneDeviceBindingOutcome.NotFound, sut.Validator.TryGetCached(TenantId, DeviceId)?.Outcome);
    }

    [Fact]
    public async Task Admitting_NotFound_WithARecentCertificate_IsTransientAndNeverCached()
    {
        // The enrollment race: the device may not be visible in Graph yet. Caching the 404 would
        // keep a genuine device out for the whole negative TTL.
        var sut = Build(permissionGranted: true, Respond(HttpStatusCode.NotFound));

        var result = await sut.Validator.ValidateAsync(TenantId, DeviceId, RecentCert, IntuneDeviceBindingRole.Admitting);

        Assert.Equal(IntuneDeviceBindingOutcome.NotFoundRecentEnrollment, result.Outcome);
        Assert.True(result.IsTransient);
        Assert.Equal(2, sut.Handler.Requests.Count);
        Assert.Null(sut.Validator.TryGetCached(TenantId, DeviceId));
    }

    [Fact]
    public async Task Admitting_RecentCertificate_ThenTheObjectAppears_IsAdmittedOnTheRetry()
    {
        var sut = Build(permissionGranted: true, Respond(HttpStatusCode.NotFound), Respond(HttpStatusCode.OK, MatchBody));

        var result = await sut.Validator.ValidateAsync(TenantId, DeviceId, RecentCert, IntuneDeviceBindingRole.Admitting);

        Assert.Equal(IntuneDeviceBindingOutcome.Match, result.Outcome);
        Assert.Equal(2, sut.Handler.Requests.Count);
    }

    [Fact]
    public async Task Admitting_ForbiddenWithAFreshTokenToo_IsPermissionMissing()
    {
        var sut = Build(permissionGranted: false, Respond(HttpStatusCode.Forbidden), Respond(HttpStatusCode.Forbidden));

        var result = await sut.Validator.ValidateAsync(TenantId, DeviceId, OldCert, IntuneDeviceBindingRole.Admitting);

        Assert.Equal(IntuneDeviceBindingOutcome.PermissionMissing, result.Outcome);
        sut.Tokens.Verify(t => t.InvalidateTenant(TenantId), Times.Once);
    }

    [Fact]
    public async Task Admitting_RunsEvenWhenThePermissionWasNotDetected()
    {
        // An admin who just ran the grant script must not wait for the detector's token cache:
        // the admitting lookup goes to Graph and the 401/403 refresh picks up the new role.
        var sut = Build(permissionGranted: false, Respond(HttpStatusCode.Forbidden), Respond(HttpStatusCode.OK, MatchBody));

        var result = await sut.Validator.ValidateAsync(TenantId, DeviceId, OldCert, IntuneDeviceBindingRole.Admitting);

        Assert.Equal(IntuneDeviceBindingOutcome.Match, result.Outcome);
        sut.Detector.Verify(d => d.HasPermissionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Admitting_InvalidDeviceId_NeverReachesGraph()
    {
        var sut = Build(permissionGranted: true);

        var result = await sut.Validator.ValidateAsync(TenantId, "not-a-guid", OldCert, IntuneDeviceBindingRole.Admitting);

        Assert.Equal(IntuneDeviceBindingOutcome.NoDeviceIdInCert, result.Outcome);
        Assert.Empty(sut.Handler.Requests);
    }

    // ------------------------------------------------------------------ observation

    [Fact]
    public async Task Observe_WithoutThePermission_MakesNoGraphCall_AndAsksTheDetectorOncePerWindow()
    {
        var sut = Build(permissionGranted: false);

        sut.Validator.ObserveInBackground(TenantId, DeviceId, OldCert, null);
        await WaitUntil(() => !InFlight(sut.Cache));

        // Later requests of the same tenant stop at the cached "not granted" verdict.
        sut.Validator.ObserveInBackground(TenantId, DeviceId, OldCert, null);
        sut.Validator.ObserveInBackground(TenantId, "22222222-3333-4444-5555-666666666666", OldCert, null);
        await WaitUntil(() => !InFlight(sut.Cache));

        Assert.Empty(sut.Handler.Requests);
        sut.Tokens.Verify(t => t.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        sut.Detector.Verify(d => d.HasPermissionAsync(TenantId, GraphAppPermissions.DeviceManagementManagedDevicesReadAll, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(sut.Validator.TryGetCached(TenantId, DeviceId));
    }

    [Fact]
    public async Task Observe_WithThePermission_FillsTheCacheInTheBackground()
    {
        var sut = Build(permissionGranted: true, Respond(HttpStatusCode.OK, MatchBody));

        sut.Validator.ObserveInBackground(TenantId, DeviceId, OldCert, "session-1");
        await WaitUntil(() => sut.Validator.TryGetCached(TenantId, DeviceId) != null);

        var observed = sut.Validator.TryGetCached(TenantId, DeviceId)!;
        Assert.Equal(IntuneDeviceBindingOutcome.Match, observed.Outcome);
        Assert.True(observed.ServedFromCache);
        Assert.Single(sut.Handler.Requests);
    }

    [Fact]
    public async Task Observe_WhenAlreadyCached_DoesNothing()
    {
        var sut = Build(permissionGranted: true, Respond(HttpStatusCode.OK, MatchBody));
        await sut.Validator.ValidateAsync(TenantId, DeviceId, OldCert, IntuneDeviceBindingRole.Admitting);

        sut.Validator.ObserveInBackground(TenantId, DeviceId, OldCert, null);

        Assert.False(InFlight(sut.Cache));
        Assert.Single(sut.Handler.Requests);
        sut.Detector.Verify(d => d.HasPermissionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Observe_TransientOutcome_KeepsACooldownInsteadOfALookupPerRequest()
    {
        // A recent certificate whose device is not visible yet caches nothing; without the cooldown
        // every request of that device would start a new Graph lookup.
        var sut = Build(permissionGranted: true, Respond(HttpStatusCode.NotFound));

        sut.Validator.ObserveInBackground(TenantId, DeviceId, RecentCert, null);
        await WaitUntil(() => sut.Handler.Requests.Count == 2);
        await Task.Delay(100);

        sut.Validator.ObserveInBackground(TenantId, DeviceId, RecentCert, null);
        await Task.Delay(100);

        Assert.True(InFlight(sut.Cache));
        Assert.Equal(2, sut.Handler.Requests.Count);
        Assert.Null(sut.Validator.TryGetCached(TenantId, DeviceId));
    }

    [Fact]
    public void Observe_InvalidDeviceId_DoesNothing()
    {
        var sut = Build(permissionGranted: true);

        sut.Validator.ObserveInBackground(TenantId, null, OldCert, null);

        sut.Detector.Verify(d => d.HasPermissionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ------------------------------------------------------------------ SecurityValidator chain

    private static (SecurityValidator Validator, Mock<IntuneDeviceBindingValidator> Binding) BuildChain(TenantConfiguration config)
    {
        var configRepo = Mock.Of<IConfigRepository>();
        var cache = new MemoryCache(new MemoryCacheOptions());

        config.ManufacturerWhitelist = "*";
        config.ModelWhitelist = "*";

        var configService = new Mock<TenantConfigurationService>(
            configRepo, Mock.Of<ILogger<TenantConfigurationService>>(), cache)
        { CallBase = false };
        configService.Setup(x => x.TryGetConfigurationAsync(It.IsAny<string>())).ReturnsAsync((config, true));

        var adminConfigService = new Mock<AdminConfigurationService>(
            configRepo, Mock.Of<ILogger<AdminConfigurationService>>(), cache)
        { CallBase = false };
        adminConfigService.Setup(x => x.GetConfigurationAsync()).ReturnsAsync(new AdminConfiguration());

        var binding = new Mock<IntuneDeviceBindingValidator>(
            NullLogger<IntuneDeviceBindingValidator>.Instance, Mock.Of<IHttpClientFactory>(), cache,
            null!, Mock.Of<IGraphFeatureDetector>())
        { CallBase = false };

        var validator = new SecurityValidator(
            configService.Object, adminConfigService.Object,
            new RateLimitService(cache, Mock.Of<ILogger<RateLimitService>>()),
            autopilotDeviceValidator: null!, corporateIdentifierValidator: null!,
            NullLogger.Instance,
            intuneDeviceBindingValidator: binding.Object);
        return (validator, binding);
    }

    private static string CertTenant => CertTenantBindingTests.SampleCertTenantId.ToString();

    private static void SetupAdmitting(Mock<IntuneDeviceBindingValidator> binding, IntuneDeviceBindingResult result)
        => binding.Setup(b => b.ValidateAsync(CertTenant, DeviceId, It.IsAny<DateTimeOffset?>(), IntuneDeviceBindingRole.Admitting,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    [Fact]
    public async Task Chain_OnlyIntuneEnrollmentEnabled_PassesTheGate_AndAdmitsAnEnrolledDevice()
    {
        var (validator, binding) = BuildChain(new TenantConfiguration { ValidateIntuneDeviceBinding = true });
        SetupAdmitting(binding, new IntuneDeviceBindingResult
        {
            Outcome = IntuneDeviceBindingOutcome.Match, IntuneDeviceId = DeviceId,
            SerialNumber = "TESTSERIAL01", OwnerType = "personal", EnrollmentType = "windowsAzureADJoin",
        });
        var items = new Dictionary<object, object>();

        var result = await validator.ValidateRequestAsync(
            CertTenantBindingTests.BuildRequestWithCert(CertTenantBindingTests.SampleCertBase64(), items), CertTenant);

        Assert.True(result.IsValid, result.ErrorMessage + " " + result.Details);
        Assert.Equal(ValidatorType.IntuneEnrollment, result.ValidatedBy);
        Assert.Equal("IntuneEnrollment", items[RequestRowMarkers.DeviceValidationKey]);
        Assert.Equal("Match", items[RequestRowMarkers.CertDeviceBindingKey]);
        Assert.Equal("Admitting", items[RequestRowMarkers.CertDeviceBindingRoleKey]);
        Assert.Equal("true", items[RequestRowMarkers.CertDeviceSerialMatchKey]);
        Assert.Equal("personal", items[RequestRowMarkers.CertDeviceOwnerTypeKey]);
        Assert.Equal("windowsAzureADJoin", items[RequestRowMarkers.CertDeviceEnrollmentTypeKey]);
        // The admitting lookup already stamped the row; no second, observing lookup.
        binding.Verify(b => b.ObserveInBackground(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Chain_PassesTheCertificateNotBeforeToTheLookup()
    {
        var (validator, binding) = BuildChain(new TenantConfiguration { ValidateIntuneDeviceBinding = true });
        DateTimeOffset? seen = null;
        binding.Setup(b => b.ValidateAsync(CertTenant, DeviceId, It.IsAny<DateTimeOffset?>(), IntuneDeviceBindingRole.Admitting,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, DateTimeOffset?, IntuneDeviceBindingRole, string?, CancellationToken>((_, _, nb, _, _, _) => seen = nb)
            .ReturnsAsync(new IntuneDeviceBindingResult { Outcome = IntuneDeviceBindingOutcome.Match });

        await validator.ValidateRequestAsync(
            CertTenantBindingTests.BuildRequestWithCert(CertTenantBindingTests.SampleCertBase64()), CertTenant);

        Assert.Equal(new DateTimeOffset(2026, 5, 8, 12, 16, 49, TimeSpan.Zero), seen);
    }

    [Theory]
    [InlineData(IntuneDeviceBindingOutcome.NotFound)]
    [InlineData(IntuneDeviceBindingOutcome.NotManaged)]
    [InlineData(IntuneDeviceBindingOutcome.NoDeviceIdInCert)]
    public async Task Chain_DefinitiveMiss_IsA403(IntuneDeviceBindingOutcome outcome)
    {
        var (validator, binding) = BuildChain(new TenantConfiguration { ValidateIntuneDeviceBinding = true });
        SetupAdmitting(binding, new IntuneDeviceBindingResult { Outcome = outcome, ErrorMessage = "miss" });
        var items = new Dictionary<object, object>();

        var result = await validator.ValidateRequestAsync(
            CertTenantBindingTests.BuildRequestWithCert(CertTenantBindingTests.SampleCertBase64(), items), CertTenant);

        Assert.False(result.IsValid);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal(outcome.ToString(), items[RequestRowMarkers.CertDeviceBindingKey]);
        Assert.Equal(RequestRowMarkers.DeviceValidation.Rejected, items[RequestRowMarkers.DeviceValidationKey]);
    }

    [Theory]
    [InlineData(IntuneDeviceBindingOutcome.NotFoundRecentEnrollment, 30)]
    [InlineData(IntuneDeviceBindingOutcome.Transient, 30)]
    [InlineData(IntuneDeviceBindingOutcome.PermissionMissing, 120)]
    public async Task Chain_NotYetDecidable_IsA503WithRetryAfter(IntuneDeviceBindingOutcome outcome, int retryAfter)
    {
        var (validator, binding) = BuildChain(new TenantConfiguration { ValidateIntuneDeviceBinding = true });
        SetupAdmitting(binding, new IntuneDeviceBindingResult { Outcome = outcome, ErrorMessage = "later" });

        var result = await validator.ValidateRequestAsync(
            CertTenantBindingTests.BuildRequestWithCert(CertTenantBindingTests.SampleCertBase64()), CertTenant);

        Assert.False(result.IsValid);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.Equal(retryAfter, result.RetryAfterSeconds);
    }

    [Fact]
    public async Task Chain_ToggleOff_NoAdmittingLookup_OnlyABackgroundObservation()
    {
        var (validator, binding) = BuildChain(new TenantConfiguration { AllowInsecureAgentRequests = true });
        var items = new Dictionary<object, object>();

        var result = await validator.ValidateRequestAsync(
            CertTenantBindingTests.BuildRequestWithCert(CertTenantBindingTests.SampleCertBase64(), items), CertTenant);

        Assert.True(result.IsValid);
        binding.Verify(b => b.ValidateAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(),
            It.IsAny<IntuneDeviceBindingRole>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        binding.Verify(b => b.ObserveInBackground(CertTenant, DeviceId, It.IsAny<DateTimeOffset?>(), It.IsAny<string?>()), Times.Once);
        Assert.False(items.ContainsKey(RequestRowMarkers.CertDeviceBindingKey));
    }

    [Fact]
    public async Task Chain_CachedObservation_IsStampedAsObserving_AndNeverRejects()
    {
        var (validator, binding) = BuildChain(new TenantConfiguration { AllowInsecureAgentRequests = true });
        binding.Setup(b => b.TryGetCached(CertTenant, DeviceId)).Returns(new IntuneDeviceBindingResult
        {
            Outcome = IntuneDeviceBindingOutcome.NotFound, SerialNumber = "OTHER-SERIAL",
        });
        var items = new Dictionary<object, object>();

        var result = await validator.ValidateRequestAsync(
            CertTenantBindingTests.BuildRequestWithCert(CertTenantBindingTests.SampleCertBase64(), items), CertTenant);

        Assert.True(result.IsValid);
        Assert.Equal("NotFound", items[RequestRowMarkers.CertDeviceBindingKey]);
        Assert.Equal("Observing", items[RequestRowMarkers.CertDeviceBindingRoleKey]);
        Assert.Equal("false", items[RequestRowMarkers.CertDeviceSerialMatchKey]);
        binding.Verify(b => b.ObserveInBackground(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Chain_NothingEnabled_IsStillRejectedAtTheGate()
    {
        var (validator, _) = BuildChain(new TenantConfiguration());

        var result = await validator.ValidateRequestAsync(
            CertTenantBindingTests.BuildRequestWithCert(CertTenantBindingTests.SampleCertBase64()), CertTenant);

        Assert.False(result.IsValid);
        Assert.Equal("Device validation is required", result.ErrorMessage);
    }

    // ------------------------------------------------------------------ gate + wire

    [Theory]
    [InlineData(nameof(TenantConfiguration.ValidateAutopilotDevice))]
    [InlineData(nameof(TenantConfiguration.ValidateCorporateIdentifier))]
    [InlineData(nameof(TenantConfiguration.ValidateDeviceAssociation))]
    [InlineData(nameof(TenantConfiguration.ValidateCloudPcDevice))]
    [InlineData(nameof(TenantConfiguration.ValidateIntuneDeviceBinding))]
    public void HasAnyDeviceValidation_EachMethodCountsAlone(string property)
    {
        var config = new TenantConfiguration();
        Assert.False(config.HasAnyDeviceValidation());

        typeof(TenantConfiguration).GetProperty(property)!.SetValue(config, true);

        Assert.True(config.HasAnyDeviceValidation());
    }

    [Fact]
    public void HasAnyDeviceValidation_CoversEveryValidateFlag()
    {
        // A new Validate* flag that the gate does not know would leave its tenants locked out.
        var flags = typeof(TenantConfiguration).GetProperties()
            .Where(p => p.PropertyType == typeof(bool) && p.Name.StartsWith("Validate", StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToList();

        foreach (var flag in flags)
        {
            var config = new TenantConfiguration();
            typeof(TenantConfiguration).GetProperty(flag)!.SetValue(config, true);
            Assert.True(config.HasAnyDeviceValidation(), $"{flag} is not part of HasAnyDeviceValidation");
        }
    }

    [Fact]
    public void RegisterWire_EveryValidatorTheBackendSends_ParsesInTheAgent()
    {
        // The backend writes enum names (ApiJsonOptions); the agent reads the same Shared model
        // with a plain JsonConvert.DeserializeObject. An agent built from this Shared therefore
        // parses every member — which is why a new member ships with an agent release first.
        foreach (var value in Enum.GetValues<ValidatorType>())
        {
            var json = JsonSerializer.Serialize(new RegisterSessionResponse { ValidatedBy = value }, ApiJsonOptions.Create());
            Assert.Contains($"\"validatedBy\":\"{value}\"", json);

            var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<RegisterSessionResponse>(json);
            Assert.Equal(value, parsed!.ValidatedBy);
        }
    }

    private enum ReleasedAgentValidatorType { Unknown = 0, AutopilotV1 = 1, CorporateIdentifier = 2, DeviceAssociation = 3, Bootstrap = 4, CloudPc = 5 }

    private sealed class ReleasedAgentResponse
    {
        public ReleasedAgentValidatorType ValidatedBy { get; set; }
    }

    [Fact]
    public void RegisterWire_AnAgentWithoutTheMember_FailsTheRegistration()
    {
        // Why the release order matters: an agent that predates IntuneEnrollment throws on it,
        // and a failed parse counts as a failed registration. Agents self-update at every start,
        // so releasing the agent before the backend closes the gap.
        Assert.ThrowsAny<Newtonsoft.Json.JsonException>(() =>
            Newtonsoft.Json.JsonConvert.DeserializeObject<ReleasedAgentResponse>("{\"validatedBy\":\"IntuneEnrollment\"}"));
    }

    [Theory]
    [InlineData("TESTSERIAL01", "testserial01", true)]
    [InlineData(" TESTSERIAL01 ", "TESTSERIAL01", true)]
    [InlineData("TESTSERIAL01", "OTHER", false)]
    [InlineData(null, "TESTSERIAL01", null)]
    [InlineData("TESTSERIAL01", "", null)]
    public void SerialMatch_ComparesTrimmedAndCaseInsensitive(string? header, string? intune, bool? expected)
        => Assert.Equal(expected, RequestRowMarkers.SerialMatch(header, intune));
}
