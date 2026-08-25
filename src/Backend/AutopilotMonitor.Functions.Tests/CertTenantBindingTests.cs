using System.Net;
using System.Security.Cryptography.X509Certificates;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Security;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// CERT-TENANT-BINDING — enforced since 2026-08-25.
///
/// The agent authenticates with the Intune MDM device certificate, whose chain is pinned to
/// Microsoft's Intune roots. Those roots are shared by every Intune tenant, so a valid chain alone
/// does not prove the caller belongs to the tenant it claims. The CA stamps the Entra tenant id
/// into OID 1.2.840.113556.5.14, which lets the backend bind the two.
///
/// These tests pin four things:
///   - the tenant id is actually extracted from a REAL field certificate (not a synthetic fixture),
///     because if Microsoft ever changes that stamp, enforcement locks out the whole fleet,
///   - the pure comparison covers every outcome the telemetry can report,
///   - the enforcement rule rejects Mismatch and ONLY Mismatch — ExtensionMissing is tolerated on
///     purpose, and that tolerance has its own test so removing it has to be a decision,
///   - a rejected request still carries its telemetry and does not leak the certificate's tenant
///     back to the caller.
/// </summary>
public class CertTenantBindingTests
{
    private const string DeviceSamplePem = "device-cert-sample.pem";

    /// <summary>
    /// Tenant stamped into <c>device-cert-sample.pem</c> by the Intune MDM Device CA. The sample is
    /// a public-key-only cert from an internal test device; the GUID is the test tenant's own id.
    /// </summary>
    private static readonly Guid SampleCertTenantId = Guid.Parse("b54dc1af-5320-4f60-b5d4-821e0cf2a359");

    private static string SampleCertBase64()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(CertTenantBindingTests).Assembly.Location)!;
        var pem = File.ReadAllText(Path.Combine(assemblyDir, DeviceSamplePem));
        using var cert = X509Certificate2.CreateFromPem(pem);
        return Convert.ToBase64String(cert.Export(X509ContentType.Cert));
    }

    // ---------------------------------------------------------------- extraction

    [Fact]
    public void ValidateCertificate_RealIntuneCert_ExtractsEntraTenantId()
    {
        // The load-bearing test for the whole feature: if Microsoft ever stops stamping OID
        // 1.2.840.113556.5.14, or changes its encoding, enforcement would lock every device out.
        var result = CertificateValidator.ValidateCertificate(SampleCertBase64());

        Assert.True(result.IsValid, $"Real device cert rejected: {result.ErrorMessage}");
        Assert.Equal(CertTenantIdStatus.Present, result.CertTenantIdStatus);
        Assert.Equal(SampleCertTenantId, result.CertTenantId);
    }

    [Fact]
    public void ValidateCertificate_CachedResult_KeepsTenantId()
    {
        // The tenant id is a pure function of the cert and is cached with the validation result,
        // so a cache hit must not silently drop it and turn every repeat request into
        // ExtensionMissing.
        var b64 = SampleCertBase64();

        _ = CertificateValidator.ValidateCertificate(b64);
        var second = CertificateValidator.ValidateCertificate(b64);

        Assert.True(second.IsValid);
        Assert.Equal(CertTenantIdStatus.Present, second.CertTenantIdStatus);
        Assert.Equal(SampleCertTenantId, second.CertTenantId);
    }

    // ---------------------------------------------------------------- encoding

    [Fact]
    public void TryParseGuid_PinsRealFieldEncodings()
    {
        // Exact bytes lifted from the sample cert. OID 5.14 nests the GUID in an OCTET STRING,
        // OID 5.4 stores the 16 bytes bare — both shapes must decode, and both are little-endian.
        var wrapped = Convert.FromHexString("0410afc14db52053604fb5d4821e0cf2a359");
        var bare = Convert.FromHexString("6701bc076190b743b2430bb6aeda736e");

        Assert.True(MsDeviceCertificateOids.TryParseGuid(wrapped, out var tenantId));
        Assert.Equal(SampleCertTenantId, tenantId);

        Assert.True(MsDeviceCertificateOids.TryParseGuid(bare, out var deviceId));
        // The Intune device id is also the certificate's Subject CN.
        Assert.Equal(Guid.Parse("07bc0167-9061-43b7-b243-0bb6aeda736e"), deviceId);
    }

    [Fact]
    public void TryParseGuid_AccountIdOidIsNotTheTenantId()
    {
        // Guards the mistake this feature was nearly built on: OID 5.6 carries the Intune ACCOUNT
        // id, a different GUID. Comparing it against the tenant id would reject every request.
        var accountIdExt = Convert.FromHexString("04103d85dede74e26b409d0d46b6fe65b440");

        Assert.True(MsDeviceCertificateOids.TryParseGuid(accountIdExt, out var accountId));
        Assert.NotEqual(SampleCertTenantId, accountId);
    }

    [Theory]
    [InlineData("")]                                            // empty
    [InlineData("0400")]                                        // OCTET STRING, zero length
    [InlineData("020102")]                                      // not an OCTET STRING
    [InlineData("0411afc14db52053604fb5d4821e0cf2a35900")]      // wrapper claims 17 bytes
    [InlineData("0410afc14db52053604fb5d4821e0cf2a3")]          // wrapper claims 16, only 15 present
    public void TryParseGuid_RejectsMalformed(string hex)
    {
        Assert.False(MsDeviceCertificateOids.TryParseGuid(Convert.FromHexString(hex), out _));
    }

    [Fact]
    public void TryParseGuid_AnySixteenBytesDecodeAsBareGuid()
    {
        // Documents a deliberate limit rather than a gap: the bare form (OID 5.4) is exactly 16
        // bytes with no tag, so a 16-byte input is indistinguishable from a truncated wrapper and
        // is always read as a GUID. Harmless here — the value is only ever compared for equality
        // against a known tenant id, never trusted as a claim in its own right.
        Assert.True(MsDeviceCertificateOids.TryParseGuid(
            Convert.FromHexString("0410afc14db52053604fb5d4821e0cf2"), out _));
    }

    [Fact]
    public void TryParseGuid_RejectsNull()
    {
        Assert.False(MsDeviceCertificateOids.TryParseGuid(null!, out _));
    }

    // ---------------------------------------------------------------- comparison

    [Fact]
    public void Evaluate_MatchingTenant_ReturnsMatch()
    {
        var outcome = CertTenantBinding.Evaluate(
            SampleCertTenantId, CertTenantIdStatus.Present, SampleCertTenantId.ToString());

        Assert.Equal(CertTenantBinding.Outcome.Match, outcome);
        Assert.False(CertTenantBinding.WouldRejectUnderEnforcement(outcome));
    }

    [Fact]
    public void Evaluate_MatchingTenant_IsCaseInsensitive()
    {
        // Tenant ids travel as strings in headers/routes and casing must not decide access.
        var outcome = CertTenantBinding.Evaluate(
            SampleCertTenantId, CertTenantIdStatus.Present, SampleCertTenantId.ToString().ToUpperInvariant());

        Assert.Equal(CertTenantBinding.Outcome.Match, outcome);
    }

    [Fact]
    public void Evaluate_ForeignTenantCert_ReturnsMismatch()
    {
        // The attack this feature exists for: a valid Intune cert from the attacker's own tenant,
        // replayed against a victim tenant whose device serial the attacker knows.
        var outcome = CertTenantBinding.Evaluate(
            SampleCertTenantId, CertTenantIdStatus.Present, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        Assert.Equal(CertTenantBinding.Outcome.Mismatch, outcome);
        Assert.True(CertTenantBinding.WouldRejectUnderEnforcement(outcome));
    }

    [Theory]
    [InlineData(CertTenantIdStatus.ExtensionMissing, CertTenantBinding.Outcome.ExtensionMissing)]
    [InlineData(CertTenantIdStatus.Unparseable, CertTenantBinding.Outcome.Unparseable)]
    public void Evaluate_UndecodableCert_ReportsWhyWithoutClaimingMismatch(
        CertTenantIdStatus status, string expected)
    {
        // These must stay distinguishable from Mismatch: they measure rollout readiness
        // (how many field certs can be enforced against), not an attack.
        var outcome = CertTenantBinding.Evaluate(null, status, SampleCertTenantId.ToString());

        Assert.Equal(expected, outcome);
        Assert.False(CertTenantBinding.WouldRejectUnderEnforcement(outcome));
    }

    [Fact]
    public void Evaluate_PresentButNullGuid_FallsBackToExtensionMissing()
    {
        var outcome = CertTenantBinding.Evaluate(null, CertTenantIdStatus.Present, SampleCertTenantId.ToString());

        Assert.Equal(CertTenantBinding.Outcome.ExtensionMissing, outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void Evaluate_NonGuidRequestTenant_IsReportedSeparately(string? requestedTenantId)
    {
        var outcome = CertTenantBinding.Evaluate(
            SampleCertTenantId, CertTenantIdStatus.Present, requestedTenantId);

        Assert.Equal(CertTenantBinding.Outcome.RequestTenantNotAGuid, outcome);
        Assert.False(CertTenantBinding.WouldRejectUnderEnforcement(outcome));
    }

    // ---------------------------------------------------------------- enforcement rule

    [Fact]
    public void Rejects_OnlyMismatch()
    {
        // Mismatch is the only outcome that is evidence of a foreign certificate. The rest mean
        // "cannot tell", and rejecting on those would cost legitimate devices their enrollment for
        // something outside their control.
        Assert.True(CertTenantBinding.Rejects(CertTenantBinding.Outcome.Mismatch));

        Assert.False(CertTenantBinding.Rejects(CertTenantBinding.Outcome.Match));
        Assert.False(CertTenantBinding.Rejects(CertTenantBinding.Outcome.ExtensionMissing));
        Assert.False(CertTenantBinding.Rejects(CertTenantBinding.Outcome.Unparseable));
        Assert.False(CertTenantBinding.Rejects(CertTenantBinding.Outcome.RequestTenantNotAGuid));
    }

    [Fact]
    public void Rejects_ExtensionMissingIsDeliberatelyTolerated()
    {
        // Pinned on its own because it is the one that is expected to tighten later: certificates
        // predating the extension, or a sovereign-cloud CA, must not be locked out today. Changing
        // this is a decision, and this test is where that decision has to be made explicitly.
        Assert.False(CertTenantBinding.Rejects(CertTenantBinding.Outcome.ExtensionMissing));
    }

    [Fact]
    public void Rejects_AgreesWithTheTelemetryField()
    {
        // WouldRejectUnderEnforcement is what the log line reports. If the two ever diverge, the
        // telemetry stops describing what actually happened.
        foreach (var outcome in new[]
                 {
                     CertTenantBinding.Outcome.Match,
                     CertTenantBinding.Outcome.Mismatch,
                     CertTenantBinding.Outcome.ExtensionMissing,
                     CertTenantBinding.Outcome.Unparseable,
                     CertTenantBinding.Outcome.RequestTenantNotAGuid,
                 })
        {
            Assert.Equal(CertTenantBinding.Rejects(outcome),
                         CertTenantBinding.WouldRejectUnderEnforcement(outcome));
        }
    }

    // ---------------------------------------------------------------- end-to-end

    [Fact]
    public async Task ValidateRequestAsync_ForeignTenantCert_IsRejected()
    {
        // The attack this whole feature exists for: a valid Intune certificate from the attacker's
        // own tenant, replayed against a victim tenant whose device serial they know. Serials are
        // printed on the chassis, so before this gate that was the entire barrier.
        var logger = new CapturingLogger();
        var validator = BuildValidator(logger);

        var result = await validator.ValidateRequestAsync(
            BuildRequestWithCert(SampleCertBase64()),
            tenantId: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        Assert.False(result.IsValid);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);

        // The response must not reveal which tenant the certificate actually belongs to.
        Assert.DoesNotContain(SampleCertTenantId.ToString(), result.Details ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SampleCertTenantId.ToString(), result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);

        var observed = Assert.Single(logger.Entries, e => e.Message.Contains("AgentCertTenantBinding"));
        Assert.Equal(LogLevel.Warning, observed.Level);
        Assert.Contains(CertTenantBinding.Outcome.Mismatch, observed.Message);
        Assert.Contains("enforced=True", observed.Message);
        Assert.Contains("wouldReject=True", observed.Message);

        // The rejection must also land in the shared rejection log, so it shows up in the same KQL
        // raster as every other reason a request was turned away.
        Assert.Contains(logger.Entries, e => e.Message.Contains("AgentRequestRejected")
                                             && e.Message.Contains("stage=certtenant"));
    }

    [Fact]
    public async Task ValidateRequestAsync_MismatchStillStampsTheRequestRow()
    {
        // Enforcement must not cost us the telemetry: a blocked request is exactly the one an
        // operator will go looking for afterwards.
        var items = new Dictionary<object, object>();
        var validator = BuildValidator(new CapturingLogger());

        await validator.ValidateRequestAsync(
            BuildRequestWithCert(SampleCertBase64(), items),
            tenantId: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        Assert.Equal(
            CertTenantBinding.Outcome.Mismatch,
            Assert.Contains(CertTenantBinding.RequestItemKey, items));
    }

    [Fact]
    public async Task ValidateRequestAsync_MatchingTenantCert_IsAccepted()
    {
        var logger = new CapturingLogger();
        var validator = BuildValidator(logger);

        var result = await validator.ValidateRequestAsync(
            BuildRequestWithCert(SampleCertBase64()),
            tenantId: SampleCertTenantId.ToString());

        Assert.True(result.IsValid, $"Matching-tenant request rejected: {result.ErrorMessage}");

        // Match is deliberately NOT a trace line — worker-side LogInformation never reaches App
        // Insights, so a Match trace would be dead code that silently costs us the denominator.
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("AgentCertTenantBinding"));
    }

    [Fact]
    public async Task ValidateRequestAsync_StampsOutcomeOnTheRequestRow()
    {
        // The denominator lives here: RequestTelemetryMiddleware copies this item onto the request
        // row, which already exists per request and is unsampled. Without it, "Match" is invisible
        // and the shadow telemetry cannot say how often the binding held.
        var items = new Dictionary<object, object>();
        var validator = BuildValidator(new CapturingLogger());

        var result = await validator.ValidateRequestAsync(
            BuildRequestWithCert(SampleCertBase64(), items),
            tenantId: SampleCertTenantId.ToString());

        Assert.True(result.IsValid, $"Matching-tenant request rejected: {result.ErrorMessage}");
        Assert.Equal(
            CertTenantBinding.Outcome.Match,
            Assert.Contains(CertTenantBinding.RequestItemKey, items));
    }

    [Fact]
    public async Task ValidateRequestAsync_StampsMismatchOnTheRequestRowToo()
    {
        var items = new Dictionary<object, object>();
        var validator = BuildValidator(new CapturingLogger());

        await validator.ValidateRequestAsync(
            BuildRequestWithCert(SampleCertBase64(), items),
            tenantId: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        Assert.Equal(
            CertTenantBinding.Outcome.Mismatch,
            Assert.Contains(CertTenantBinding.RequestItemKey, items));
    }

    // ---------------------------------------------------------------- harness

    private static SecurityValidator BuildValidator(ILogger logger)
    {
        var configRepo = Mock.Of<IConfigRepository>();
        var cache = new MemoryCache(new MemoryCacheOptions());

        // AllowInsecureAgentRequests clears the "at least one device validator" gate so the request
        // reaches — and passes — the certificate stage without needing Graph mocks.
        var config = new TenantConfiguration
        {
            AllowInsecureAgentRequests = true,
            ManufacturerWhitelist = "*",
            ModelWhitelist = "*",
        };

        var configServiceMock = new Mock<TenantConfigurationService>(
            configRepo, Mock.Of<ILogger<TenantConfigurationService>>(), cache)
        { CallBase = false };
        configServiceMock
            .Setup(x => x.TryGetConfigurationAsync(It.IsAny<string>()))
            .ReturnsAsync((config, true));

        var adminConfigServiceMock = new Mock<AdminConfigurationService>(
            configRepo, Mock.Of<ILogger<AdminConfigurationService>>(), cache)
        { CallBase = false };
        adminConfigServiceMock
            .Setup(x => x.GetConfigurationAsync())
            .ReturnsAsync(new AdminConfiguration());

        return new SecurityValidator(
            configService: configServiceMock.Object,
            adminConfigService: adminConfigServiceMock.Object,
            rateLimitService: new RateLimitService(cache, Mock.Of<ILogger<RateLimitService>>()),
            autopilotDeviceValidator: null!,
            corporateIdentifierValidator: null!,
            logger: logger,
            bootstrapSessionService: null,
            deviceAssociationValidator: null);
    }

    private static HttpRequestData BuildRequestWithCert(
        string certBase64,
        IDictionary<object, object>? items = null)
    {
        var contextMock = new Mock<Microsoft.Azure.Functions.Worker.FunctionContext>();
        contextMock.SetupGet(c => c.Items).Returns(items ?? new Dictionary<object, object>());
        var reqMock = new Mock<HttpRequestData>(contextMock.Object);

        var headers = new HttpHeadersCollection
        {
            { "X-ARR-ClientCert", certBase64 },
            // The hardware whitelist stage runs after the certificate stage and rejects requests
            // with no hardware headers, which would mask what these tests are actually asserting.
            { "X-Device-Manufacturer", "Contoso" },
            { "X-Device-Model", "TestBook" },
            { "X-Device-SerialNumber", "TESTSERIAL01" },
        };
        reqMock.SetupGet(r => r.Headers).Returns(headers);
        reqMock.SetupGet(r => r.Url).Returns(new Uri("https://example.invalid/api/telemetry"));

        return reqMock.Object;
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
