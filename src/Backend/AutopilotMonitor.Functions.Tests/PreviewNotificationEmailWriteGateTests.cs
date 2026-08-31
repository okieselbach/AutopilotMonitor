using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Rules;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// PUT /api/preview/notification-email writes tenant-shared state that feeds the welcome and
/// farewell mails. The route must admit roleless callers (first-touch signup) but only while the
/// tenant has no member yet — afterwards a member role is required, so an ordinary employee with a
/// valid JWT and no product role can no longer redirect or suppress the tenant's correspondence.
/// </summary>
public class PreviewNotificationEmailWriteGateTests
{
    private const string TenantId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void Route_is_AuthenticatedUserWithRole_so_the_function_can_see_the_resolved_role()
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy("PUT", "/api/preview/notification-email");

        Assert.NotNull(entry);
        Assert.Equal("preview/notification-email", entry!.RouteTemplate);
        Assert.Equal(EndpointPolicy.AuthenticatedUserWithRole, entry.Policy);
    }

    [Theory]
    [InlineData(Constants.TenantRoles.Admin)]
    [InlineData(Constants.TenantRoles.Operator)]
    [InlineData(Constants.TenantRoles.Viewer)]
    public async Task Member_role_may_write_even_when_the_tenant_has_members(string role)
    {
        var admins = AdminsWith(enabledMember: true);
        var ctx = new RequestContext { UserRole = role };

        Assert.True(await PreviewWhitelistFunction.MayWriteNotificationEmailAsync(ctx, TenantId, admins.Object));
        admins.Verify(a => a.GetTenantAdminsAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Global_admin_may_write()
    {
        var admins = AdminsWith(enabledMember: true);
        var ctx = new RequestContext { IsGlobalAdmin = true };

        Assert.True(await PreviewWhitelistFunction.MayWriteNotificationEmailAsync(ctx, TenantId, admins.Object));
    }

    [Fact]
    public async Task Roleless_caller_may_write_while_the_tenant_has_no_member_yet()
    {
        var admins = AdminsWith(enabledMember: false);
        var ctx = new RequestContext { UserRole = "Authenticated" };

        Assert.True(await PreviewWhitelistFunction.MayWriteNotificationEmailAsync(ctx, TenantId, admins.Object));
    }

    [Fact]
    public async Task Roleless_caller_is_rejected_once_the_tenant_has_an_enabled_member()
    {
        var admins = AdminsWith(enabledMember: true);
        var ctx = new RequestContext { UserRole = "Authenticated" };

        Assert.False(await PreviewWhitelistFunction.MayWriteNotificationEmailAsync(ctx, TenantId, admins.Object));
    }

    [Fact]
    public async Task Disabled_members_do_not_close_the_signup_window()
    {
        var admins = new Mock<TenantAdminsService>(
            Mock.Of<IAdminRepository>(), Mock.Of<IMemoryCache>(), Mock.Of<ILogger<TenantAdminsService>>())
        { CallBase = false };
        admins.Setup(a => a.GetTenantAdminsAsync(TenantId)).ReturnsAsync(new List<TenantAdminRow>
        {
            new() { TenantId = TenantId, Upn = "former@contoso.com", IsEnabled = false, Role = Constants.TenantRoles.Admin },
        });
        var ctx = new RequestContext { UserRole = "Authenticated" };

        Assert.True(await PreviewWhitelistFunction.MayWriteNotificationEmailAsync(ctx, TenantId, admins.Object));
    }

    private static Mock<TenantAdminsService> AdminsWith(bool enabledMember)
    {
        var admins = new Mock<TenantAdminsService>(
            Mock.Of<IAdminRepository>(), Mock.Of<IMemoryCache>(), Mock.Of<ILogger<TenantAdminsService>>())
        { CallBase = false };
        var members = new List<TenantAdminRow>();
        if (enabledMember)
            members.Add(new TenantAdminRow { TenantId = TenantId, Upn = "admin@contoso.com", IsEnabled = true, Role = Constants.TenantRoles.Admin });
        admins.Setup(a => a.GetTenantAdminsAsync(TenantId)).ReturnsAsync(members);
        return admins;
    }
}
