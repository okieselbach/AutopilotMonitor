using System.Net;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Self-hosted MCP client registrations: storage round trip, wire parity, callback validation (the MCP proxy
/// redirects authorization codes to the stored URL), the per-tenant cap, the operator switch (create refused,
/// lookup 404, delete still possible) and the audit trail.
/// </summary>
public class McpClientRegistrationTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private const string Callback = "https://chat.contoso.example/api/mcp/autopilot-monitor/oauth/callback";
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    // ── Storage and wire ────────────────────────────────────────────────────────

    [Fact]
    public void Roundtrip_FullRow_SurvivesBuildAndMap()
    {
        var row = new McpClientRegistration
        {
            RegistrationId = "0123456789abcdef0123456789abcdef",
            TenantId = TenantA.ToUpperInvariant(),
            Name = "Team chat",
            RedirectUri = Callback,
            CreatedBy = "admin@contoso.example",
            CreatedAt = Now,
        };

        var entity = TableMcpClientRegistrationRepository.Build(row);
        var back = TableMcpClientRegistrationRepository.Map(entity);

        Assert.Equal(TableMcpClientRegistrationRepository.PartitionKey, entity.PartitionKey);
        Assert.Equal(row.RegistrationId, entity.RowKey);
        Assert.Equal(row.RegistrationId, back.RegistrationId);
        Assert.Equal(TenantA, back.TenantId); // lowercase: the offboarding wipe matches it
        Assert.Equal(row.Name, back.Name);
        Assert.Equal(row.RedirectUri, back.RedirectUri);
        Assert.Equal(row.CreatedBy, back.CreatedBy);
        Assert.Equal(Now, back.CreatedAt);
    }

    [Fact]
    public void TenantFilter_IsPartitionPlusLowercaseTenant()
        => Assert.Equal($"PartitionKey eq 'registrations' and TenantId eq '{TenantA}'",
            TableMcpClientRegistrationRepository.BuildTenantFilter(TenantA.ToUpperInvariant()));

    [Fact]
    public void ListResponse_WireShape()
    {
        var item = McpClientRegistrationFunction.ToItem(new McpClientRegistration
        {
            RegistrationId = "abc", TenantId = TenantA, Name = "Team chat", RedirectUri = Callback,
            CreatedBy = "admin@contoso.example", CreatedAt = Now,
        });
        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                enabled = true,
                maxRegistrations = 3,
                serverUrl = "https://mcp.autopilotmonitor.com/mcp",
                registrations = new[]
                {
                    new { registrationId = "abc", clientId = "amc_abc", name = "Team chat", redirectUri = Callback, createdBy = "admin@contoso.example", createdUtc = Now },
                },
            },
            new McpClientRegistrationListResponse
            {
                Enabled = true, MaxRegistrations = 3, ServerUrl = "https://mcp.autopilotmonitor.com/mcp",
                Registrations = new[] { item },
            });
    }

    [Fact]
    public void CreateAndLookupResponses_WireShape()
    {
        var item = new McpClientRegistrationItem
        {
            RegistrationId = "abc", ClientId = "amc_abc", Name = "Team chat", RedirectUri = Callback,
            CreatedBy = "admin@contoso.example", CreatedUtc = Now,
        };
        ApiResponseWireParityTests.AssertWireIdentical(
            new { registration = new { registrationId = "abc", clientId = "amc_abc", name = "Team chat", redirectUri = Callback, createdBy = "admin@contoso.example", createdUtc = Now } },
            new CreateMcpClientRegistrationResponse { Registration = item });
        ApiResponseWireParityTests.AssertWireIdentical(
            new { registrationId = "abc", tenantId = TenantA, redirectUri = Callback, name = "Team chat" },
            new McpClientRegistrationLookupResponse { RegistrationId = "abc", TenantId = TenantA, RedirectUri = Callback, Name = "Team chat" });
    }

    // ── Validation ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Callback)]
    [InlineData("http://localhost:3080/api/mcp/autopilot-monitor/oauth/callback")]
    [InlineData("http://127.0.0.1:3080/cb")]
    [InlineData("https://chat.contoso.example:8443/cb/")]
    public void AcceptableCallbacks(string uri) => Assert.Null(McpClientRegistrationService.ValidateRedirectUri(uri));

    [Theory]
    [InlineData("")]
    [InlineData("chat.contoso.example/cb")]
    [InlineData("http://chat.contoso.example/cb")]        // http off loopback
    [InlineData("https://chat.contoso.example/cb?x=1")]   // query
    [InlineData("https://chat.contoso.example/cb#frag")]  // fragment
    [InlineData("https://*.contoso.example/cb")]          // wildcard
    [InlineData("https://user:pw@chat.contoso.example/cb")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://chat.contoso.example/cb")]
    public void RejectedCallbacks(string uri) => Assert.NotNull(McpClientRegistrationService.ValidateRedirectUri(uri));

    [Fact]
    public void OverlongCallback_IsRejected()
        => Assert.NotNull(McpClientRegistrationService.ValidateRedirectUri("https://chat.contoso.example/" + new string('a', 1100)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tab\there")]
    public void RejectedNames(string name) => Assert.NotNull(McpClientRegistrationService.ValidateName(name));

    [Fact]
    public void OverlongName_IsRejected() => Assert.NotNull(McpClientRegistrationService.ValidateName(new string('n', 65)));

    // ── Service: switch, cap, audit, tenant isolation ───────────────────────────

    private sealed class Harness
    {
        public required McpClientRegistrationService Sut { get; init; }
        public required Mock<IMcpClientRegistrationRepository> Repo { get; init; }
        public required Mock<IMaintenanceRepository> Audit { get; init; }
    }

    private static Harness Build(bool enabled = true, int? tenantLimit = null)
    {
        var rows = new List<McpClientRegistration>();
        var repo = new Mock<IMcpClientRegistrationRepository>();
        repo.Setup(r => r.GetForTenantAsync(It.IsAny<string>()))
            .ReturnsAsync((string t) => rows.Where(r => r.TenantId == t.ToLowerInvariant()).ToList());
        repo.Setup(r => r.GetAsync(It.IsAny<string>())).ReturnsAsync((string id) => rows.FirstOrDefault(r => r.RegistrationId == id));
        repo.Setup(r => r.CreateAsync(It.IsAny<McpClientRegistration>())).Callback<McpClientRegistration>(rows.Add).ReturnsAsync(true);
        repo.Setup(r => r.DeleteAsync(It.IsAny<string>())).ReturnsAsync((string id) => rows.RemoveAll(r => r.RegistrationId == id) > 0);
        var audit = new Mock<IMaintenanceRepository>();
        audit.Setup(a => a.LogAuditEntryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
            .ReturnsAsync(true);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var adminConfig = new Mock<AdminConfigurationService>(Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, cache) { CallBase = false };
        adminConfig.Setup(x => x.GetConfigurationAsync()).ReturnsAsync(new AdminConfiguration { McpClientRegistrationEnabled = enabled });
        var configRepo = new Mock<IConfigRepository>();
        configRepo.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>()))
            .ReturnsAsync((string t) => new TenantConfiguration { TenantId = t, McpClientRegistrationLimit = t == TenantA ? tenantLimit : null });
        var tenantConfig = new TenantConfigurationService(configRepo.Object, NullLogger<TenantConfigurationService>.Instance, cache);
        var sut = new McpClientRegistrationService(repo.Object, audit.Object, adminConfig.Object, tenantConfig, NullLogger<McpClientRegistrationService>.Instance);
        return new Harness { Sut = sut, Repo = repo, Audit = audit };
    }

    [Fact]
    public async Task Create_StoresARegistration_WithARandomHexId_AndAudits()
    {
        var h = Build();

        var result = await h.Sut.CreateAsync(TenantA.ToUpperInvariant(), "  Team chat ", Callback, "admin@contoso.example");

        Assert.Equal(HttpStatusCode.Created, result.Status);
        var reg = result.Registration!;
        Assert.True(McpClientRegistrationService.IsRegistrationId(reg.RegistrationId));
        Assert.Equal(TenantA, reg.TenantId);
        Assert.Equal("Team chat", reg.Name);
        Assert.Equal($"amc_{reg.RegistrationId}", McpClientRegistrationService.ClientIdOf(reg.RegistrationId));
        h.Audit.Verify(a => a.LogAuditEntryAsync(TenantA, "CREATE", "McpClientRegistration", reg.RegistrationId, "admin@contoso.example",
            It.Is<Dictionary<string, string>?>(d => d != null && d["RedirectUri"] == Callback)), Times.Once);
    }

    [Fact]
    public async Task Create_IsRefused_WhileTheSwitchIsOff()
    {
        var h = Build(enabled: false);

        var result = await h.Sut.CreateAsync(TenantA, "Team chat", Callback, "admin@contoso.example");

        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        h.Repo.Verify(r => r.CreateAsync(It.IsAny<McpClientRegistration>()), Times.Never);
    }

    [Fact]
    public async Task Create_AllowsOneRegistrationByDefault_PerTenant()
    {
        var h = Build();

        Assert.Equal(HttpStatusCode.Created, (await h.Sut.CreateAsync(TenantA, "chat", Callback, "a")).Status);
        var second = await h.Sut.CreateAsync(TenantA, "chat 2", "https://chat2.contoso.example/cb", "a");
        var otherTenant = await h.Sut.CreateAsync(TenantB, "chat", Callback, "b");

        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Contains("raise the limit", second.Error);
        Assert.Equal(HttpStatusCode.Created, otherTenant.Status); // the limit and the duplicate check are per tenant
        Assert.Equal(1, await h.Sut.GetLimitAsync(TenantA));
    }

    [Fact]
    public async Task AGlobalAdminOverride_RaisesTheLimit_AndDuplicatesStayRefused()
    {
        var h = Build(tenantLimit: 3);
        Assert.Equal(3, await h.Sut.GetLimitAsync(TenantA.ToUpperInvariant()));
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Created, (await h.Sut.CreateAsync(TenantA, $"chat {i}", $"https://chat{i}.contoso.example/cb", "a")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await h.Sut.CreateAsync(TenantA, "chat 4", "https://chat4.contoso.example/cb", "a")).Status);

        var dup = Build(tenantLimit: 3);
        await dup.Sut.CreateAsync(TenantA, "one", Callback, "a");
        Assert.Equal(HttpStatusCode.Conflict, (await dup.Sut.CreateAsync(TenantA, "two", Callback.ToUpperInvariant(), "a")).Status);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(50, 10)]
    public async Task AnOutOfRangeOverride_IsClamped(int stored, int effective)
        => Assert.Equal(effective, await Build(tenantLimit: stored).Sut.GetLimitAsync(TenantA));

    [Fact]
    public async Task Create_ValidatesBeforeStoring()
    {
        var h = Build();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Sut.CreateAsync(TenantA, "x", "http://chat.contoso.example/cb", "a")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Sut.CreateAsync(TenantA, "", Callback, "a")).Status);
        h.Repo.Verify(r => r.CreateAsync(It.IsAny<McpClientRegistration>()), Times.Never);
    }

    [Fact]
    public async Task Delete_OnlyTheOwnTenantsRegistration_AlsoWhileTheSwitchIsOff()
    {
        var h = Build();
        var reg = (await h.Sut.CreateAsync(TenantA, "Team chat", Callback, "a")).Registration!;

        Assert.False(await h.Sut.DeleteAsync(TenantB, reg.RegistrationId, "b"));
        Assert.False(await h.Sut.DeleteAsync(TenantA, "not-an-id", "a"));
        Assert.True(await h.Sut.DeleteAsync(TenantA.ToUpperInvariant(), reg.RegistrationId, "a"));
        h.Audit.Verify(a => a.LogAuditEntryAsync(TenantA, "DELETE", "McpClientRegistration", reg.RegistrationId, "a", It.IsAny<Dictionary<string, string>?>()), Times.Once);
    }

    [Fact]
    public async Task Lookup_AnswersOnlyWhileEnabled_AndOnlyForWellFormedIds()
    {
        var on = Build();
        var reg = (await on.Sut.CreateAsync(TenantA, "Team chat", Callback, "a")).Registration!;

        Assert.NotNull(await on.Sut.LookupAsync(reg.RegistrationId));
        Assert.Null(await on.Sut.LookupAsync(reg.RegistrationId.ToUpperInvariant()));
        Assert.Null(await on.Sut.LookupAsync("../../etc"));

        var off = Build(enabled: false);
        off.Repo.Setup(r => r.GetAsync(reg.RegistrationId)).ReturnsAsync(reg);
        Assert.Null(await off.Sut.LookupAsync(reg.RegistrationId));
        off.Repo.Verify(r => r.GetAsync(It.IsAny<string>()), Times.Never);
    }
}
