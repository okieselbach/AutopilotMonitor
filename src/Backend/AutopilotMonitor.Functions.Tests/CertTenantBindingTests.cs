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
/// CERT-TENANT-BINDING-SHADOW — stage 1 coverage.
///
/// The agent authenticates with the Intune MDM device certificate, whose chain is pinned to
/// Microsoft's Intune roots. Those roots are shared by every Intune tenant, so a valid chain alone
/// does not prove the caller belongs to the tenant it claims. The CA stamps the Entra tenant id
/// into OID 1.2.840.113556.5.14, which lets the backend bind the two.
///
/// These tests pin three things:
///   - the tenant id is actually extracted from a REAL field certificate (not a synthetic fixture),
///   - the pure comparison covers every outcome the telemetry can report,
///   - shadow mode observes without deciding: a cross-tenant certificate must still pass the
///     certificate stage in stage 1, or we would be enforcing before we measured.
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
    public void ValidateCertificate_CachedResult_KeepsTenantIdAndFlagsCacheHit()
    {
        // FromCache is what samples the high-volume "Match" telemetry down to ~one line per cert
        // per cache window, so it has to survive the cache and not corrupt the cached entry.
        var b64 = SampleCertBase64();

        _ = CertificateValidator.ValidateCertificate(b64);
        var second = CertificateValidator.ValidateCertificate(b64);
        var third = CertificateValidator.ValidateCertificate(b64);

        Assert.True(second.FromCache);
        Assert.Equal(SampleCertTenantId, second.CertTenantId);

        // The cache-hit copy must not have mutated the shared cached instance.
        Assert.True(third.FromCache);
        Assert.Equal(SampleCertTenantId, third.CertTenantId);
        Assert.True(third.IsValid);
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

    // ---------------------------------------------------------------- shadow mode

    [Fact]
    public async Task ValidateRequestAsync_ForeignTenantCert_IsObservedButNotRejected()
    {
        // Stage 1 contract: a cross-tenant certificate is logged and still passes the certificate
        // stage. If this test ever fails, enforcement was switched on without the telemetry review.
        var logger = new CapturingLogger();
        var validator = BuildValidator(logger);

        var result = await validator.ValidateRequestAsync(
            BuildRequestWithCert(SampleCertBase64()),
            tenantId: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        // Deliberately accepted: stage 1 measures, it does not enforce. This assertion is the one
        // that must be inverted (to a 403) when stage 2 turns enforcement on.
        Assert.True(result.IsValid, $"Shadow mode changed the outcome: {result.ErrorMessage} / {result.Details}");
        Assert.NotEqual(HttpStatusCode.Unauthorized, result.StatusCode);

        var observed = Assert.Single(logger.Entries, e => e.Message.Contains("AgentCertTenantBinding"));
        Assert.Equal(LogLevel.Warning, observed.Level);
        Assert.Contains(CertTenantBinding.Outcome.Mismatch, observed.Message);
        Assert.Contains("enforced=False", observed.Message);
        Assert.Contains("wouldReject=True", observed.Message);
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

        // Match is only logged on a fresh validation; the cache is process-wide and shared with the
        // other tests here, so assert the outcome only when a line was actually emitted.
        var observed = logger.Entries.FirstOrDefault(e => e.Message.Contains("AgentCertTenantBinding"));
        if (observed != null)
        {
            Assert.Equal(LogLevel.Information, observed.Level);
            Assert.Contains(CertTenantBinding.Outcome.Match, observed.Message);
        }
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

    private static HttpRequestData BuildRequestWithCert(string certBase64)
    {
        var contextMock = new Mock<Microsoft.Azure.Functions.Worker.FunctionContext>();
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
