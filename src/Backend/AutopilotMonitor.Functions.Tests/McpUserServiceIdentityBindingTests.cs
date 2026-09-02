using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The McpUsers whitelist is keyed on a mutable, globally non-unique UPN string while the API accepts
/// tokens from every Entra tenant. A whitelist row (its grant, its usage-plan override AND its Disabled
/// kill-switch) must therefore be honoured only for the identity the UPN is bound to (tid + oid via
/// AdminIdentityBindings) — a foreign-tenant or recycled-UPN token carrying the same UPN sees no row.
/// </summary>
public class McpUserServiceIdentityBindingTests
{
    private const string Upn = "user@customer.example";
    private const string HomeTenant = "11111111-1111-1111-1111-111111111111";
    private const string ForeignTenant = "22222222-2222-2222-2222-222222222222";
    private const string Oid = "aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa";
    private const string OtherOid = "bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb";

    private static readonly AdminIdentity Bound = AdminIdentity.Create(Upn, HomeTenant, Oid)!;

    private readonly Mock<IAdminRepository> _adminRepo = new();
    private readonly StubAdminIdentityBindingService _bindings;
    // Home-tenant membership for the AllMembers path: this suite is about the identity binding, so every
    // caller counts as a tenant member and only the binding decides (see McpUserServiceAllMembersTests).
    private readonly StubTenantMemberRoleResolver _memberRoles = StubTenantMemberRoleResolver.Everyone();
    private readonly McpUserService _sut;

    public McpUserServiceIdentityBindingTests()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        // Binding verdict = "is this exactly the bound identity?" — the real service's semantics for a row
        // bound to (HomeTenant, Oid).
        _bindings = new StubAdminIdentityBindingService(id =>
            id != null
            && string.Equals(id.Upn, Upn, StringComparison.OrdinalIgnoreCase)
            && string.Equals(id.TenantId, HomeTenant, StringComparison.OrdinalIgnoreCase)
            && string.Equals(id.ObjectId, Oid, StringComparison.OrdinalIgnoreCase));

        var globalAdmin = new Mock<GlobalAdminService>(
            _adminRepo.Object, _bindings, cache, NullLogger<GlobalAdminService>.Instance) { CallBase = false };
        globalAdmin.Setup(x => x.GetGlobalRoleAsync(It.IsAny<AdminIdentity?>())).ReturnsAsync((string?)null);
        var delegatedAdmin = new Mock<DelegatedAdminService>(
            _adminRepo.Object, _bindings,
            new StubTenantEntitlementService(TenantEdition.Pro),
            cache, NullLogger<DelegatedAdminService>.Instance) { CallBase = false };
        delegatedAdmin.Setup(x => x.GetScopeAsync(It.IsAny<AdminIdentity?>())).ReturnsAsync(DelegatedScope.Empty);
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, cache) { CallBase = false };
        adminConfig.Setup(x => x.GetConfigurationAsync())
            .ReturnsAsync(new AdminConfiguration { McpAccessPolicy = McpAccessPolicy.WhitelistOnly.ToString() });

        _sut = new McpUserService(
            _adminRepo.Object, _bindings, cache, NullLogger<McpUserService>.Instance,
            globalAdmin.Object, delegatedAdmin.Object, adminConfig.Object, _memberRoles);
    }

    private void SetRow(bool enabled, string? plan = "pro") =>
        _adminRepo.Setup(x => x.GetMcpUserAsync(Upn))
            .ReturnsAsync(new McpUserEntry { Upn = Upn, IsEnabled = enabled, UsagePlan = plan });

    // ── grant ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BoundIdentity_WithEnabledRow_IsGranted()
    {
        SetRow(enabled: true);

        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, Oid);

        Assert.True(result.IsAllowed);
        Assert.Equal("McpUser", result.AccessGrant);
    }

    [Fact]
    public async Task ForeignTenantToken_WithSameUpn_IsDenied()
    {
        // The nOAuth-class premise: a token from an attacker-controlled tenant whose UPN claim renders as the
        // whitelisted string. Genuine token, only tid differs — must not inherit the row.
        SetRow(enabled: true);

        var result = await _sut.IsAllowedAsync(Upn, ForeignTenant, Oid);

        Assert.False(result.IsAllowed);
        Assert.Contains("not on the MCP whitelist", result.Reason);
    }

    [Fact]
    public async Task RecycledUpn_SameTenantDifferentOid_IsDenied()
    {
        SetRow(enabled: true);

        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, OtherOid);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task IncompleteIdentity_NoTidOid_IsDenied()
    {
        SetRow(enabled: true);

        var result = await _sut.IsAllowedAsync(Upn, null, null);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task NoRow_NeverConsultsBinding()
    {
        // Ordinary tenant users have no McpUsers row: one cached row read, no binding lookup, no log line.
        var probed = false;
        var bindings = new StubAdminIdentityBindingService(_ => { probed = true; return true; });
        var cache = new MemoryCache(new MemoryCacheOptions());
        var globalAdmin = new Mock<GlobalAdminService>(
            _adminRepo.Object, bindings, cache, NullLogger<GlobalAdminService>.Instance) { CallBase = false };
        globalAdmin.Setup(x => x.GetGlobalRoleAsync(It.IsAny<AdminIdentity?>())).ReturnsAsync((string?)null);
        var delegatedAdmin = new Mock<DelegatedAdminService>(
            _adminRepo.Object, bindings, new StubTenantEntitlementService(TenantEdition.Pro),
            cache, NullLogger<DelegatedAdminService>.Instance) { CallBase = false };
        delegatedAdmin.Setup(x => x.GetScopeAsync(It.IsAny<AdminIdentity?>())).ReturnsAsync(DelegatedScope.Empty);
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, cache) { CallBase = false };
        adminConfig.Setup(x => x.GetConfigurationAsync())
            .ReturnsAsync(new AdminConfiguration { McpAccessPolicy = McpAccessPolicy.WhitelistOnly.ToString() });
        var sut = new McpUserService(_adminRepo.Object, bindings, cache, NullLogger<McpUserService>.Instance,
            globalAdmin.Object, delegatedAdmin.Object, adminConfig.Object, _memberRoles);
        _adminRepo.Setup(x => x.GetMcpUserAsync(It.IsAny<string>())).ReturnsAsync((McpUserEntry?)null);

        var result = await sut.IsAllowedAsync(Upn, HomeTenant, Oid);

        Assert.False(result.IsAllowed);
        Assert.False(probed);
    }

    // ── kill-switch ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisabledRow_DeniesTheBoundIdentity()
    {
        SetRow(enabled: false);

        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, Oid);

        Assert.False(result.IsAllowed);
        Assert.Contains("disabled on the MCP whitelist", result.Reason);
    }

    [Fact]
    public async Task DisabledRow_DoesNotFireForAForeignSameUpnIdentity_UnderAllMembers()
    {
        // The kill-switch belongs to the bound identity: an operator's per-account disable must not deny an
        // unrelated same-UPN identity in another tenant (whose access comes from its own tenant's policy).
        var cache = new MemoryCache(new MemoryCacheOptions());
        var globalAdmin = new Mock<GlobalAdminService>(
            _adminRepo.Object, _bindings, cache, NullLogger<GlobalAdminService>.Instance) { CallBase = false };
        globalAdmin.Setup(x => x.GetGlobalRoleAsync(It.IsAny<AdminIdentity?>())).ReturnsAsync((string?)null);
        var delegatedAdmin = new Mock<DelegatedAdminService>(
            _adminRepo.Object, _bindings, new StubTenantEntitlementService(TenantEdition.Pro),
            cache, NullLogger<DelegatedAdminService>.Instance) { CallBase = false };
        delegatedAdmin.Setup(x => x.GetScopeAsync(It.IsAny<AdminIdentity?>())).ReturnsAsync(DelegatedScope.Empty);
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, cache) { CallBase = false };
        adminConfig.Setup(x => x.GetConfigurationAsync())
            .ReturnsAsync(new AdminConfiguration { McpAccessPolicy = McpAccessPolicy.AllMembers.ToString() });
        var sut = new McpUserService(_adminRepo.Object, _bindings, cache, NullLogger<McpUserService>.Instance,
            globalAdmin.Object, delegatedAdmin.Object, adminConfig.Object, _memberRoles);
        SetRow(enabled: false);

        var bound = await sut.IsAllowedAsync(Upn, HomeTenant, Oid);
        var foreign = await sut.IsAllowedAsync(Upn, ForeignTenant, Oid);

        Assert.False(bound.IsAllowed);
        Assert.True(foreign.IsAllowed);
        Assert.Equal("AllMembers", foreign.AccessGrant);
    }

    // ── usage-plan override ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetBoundMcpUser_ReturnsRowOnlyForTheBoundIdentity()
    {
        SetRow(enabled: true, plan: "pro");

        Assert.NotNull(await _sut.GetBoundMcpUserAsync(Bound));
        Assert.Null(await _sut.GetBoundMcpUserAsync(AdminIdentity.Create(Upn, ForeignTenant, Oid)));
        Assert.Null(await _sut.GetBoundMcpUserAsync(AdminIdentity.Create(Upn, HomeTenant, OtherOid)));
        Assert.Null(await _sut.GetBoundMcpUserAsync(null));
    }

    // ── grant-time binding ───────────────────────────────────────────────────────

    [Fact]
    public async Task AddMcpUser_BindsTheUpnBeforeWritingTheRow()
    {
        var calls = new List<string>();
        _bindings.OnEnsureBound = () => calls.Add("bind");
        _adminRepo.Setup(x => x.AddMcpUserAsync(Upn, "ga@vendor.example"))
            .Callback(() => calls.Add("row")).ReturnsAsync(true);

        await _sut.AddMcpUserAsync(Upn, "GA@vendor.example", HomeTenant, Oid);

        Assert.Equal(new[] { "bind", "row" }, calls);
        Assert.Single(_bindings.Bindings);
        Assert.Equal((Upn, HomeTenant, Oid), _bindings.Bindings[0]);
    }

    [Fact]
    public async Task AddMcpUser_BindingConflict_WritesNoRow()
    {
        _bindings.OnEnsureBound = () => throw new IdentityBindingConflictException("already bound elsewhere");

        await Assert.ThrowsAsync<IdentityBindingConflictException>(
            () => _sut.AddMcpUserAsync(Upn, "ga@vendor.example", ForeignTenant, null));

        _adminRepo.Verify(x => x.AddMcpUserAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
