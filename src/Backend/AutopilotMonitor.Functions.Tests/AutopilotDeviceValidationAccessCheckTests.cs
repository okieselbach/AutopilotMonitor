using System.Collections.Generic;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Services.GraphResolution;
using AutopilotMonitor.Shared.Models.Graph;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Covers the reconcile decision behind the <c>access-check</c> probe
/// (<see cref="AutopilotDeviceValidationConsentFunction.ResolveAccessPresent"/>) — the
/// "rights-less admin" path where the multi-tenant app was pre-approved by someone else.
/// The HTTP entry point itself is intentionally not exercised (mocking HttpRequestData +
/// the security pipeline is more setup than it's worth, per the project's function-test
/// convention); the security-relevant logic lives in this pure helper.
/// </summary>
public class AutopilotDeviceValidationAccessCheckTests
{
    private static GraphPermissionSnapshot Snapshot(bool transient, params string[] roles)
        => new GraphPermissionSnapshot
        {
            GrantedRoles = new HashSet<string>(roles),
            IsTransient = transient,
        };

    [Fact]
    public void AccessPresent_WhenCoreRoleGranted()
    {
        var snapshot = Snapshot(false, GraphAppPermissions.DeviceManagementServiceConfigReadAll);

        Assert.True(AutopilotDeviceValidationConsentFunction.ResolveAccessPresent(snapshot));
    }

    [Theory]
    [InlineData("devicemanagementserviceconfig.read.all")]
    [InlineData("DEVICEMANAGEMENTSERVICECONFIG.READ.ALL")]
    public void AccessPresent_IsCaseInsensitive(string roleCasing)
    {
        // Azure AD may issue the role with slightly different casing across tokens.
        var snapshot = Snapshot(false, roleCasing);

        Assert.True(AutopilotDeviceValidationConsentFunction.ResolveAccessPresent(snapshot));
    }

    [Fact]
    public void AccessAbsent_WhenRoleMissing()
    {
        // SP exists / token acquirable, but the core validation role was not consented —
        // must NOT report present (would open the gate then 503 the agent forever).
        var snapshot = Snapshot(false, GraphAppPermissions.DeviceManagementScriptsReadAll);

        Assert.False(AutopilotDeviceValidationConsentFunction.ResolveAccessPresent(snapshot));
    }

    [Fact]
    public void AccessAbsent_WhenNoRolesGranted()
    {
        Assert.False(AutopilotDeviceValidationConsentFunction.ResolveAccessPresent(Snapshot(false)));
    }

    [Fact]
    public void AccessAbsent_WhenTransient_EvenIfRolePresent()
    {
        // Inconclusive probe (timeout / Graph 5xx) must never flip the gate, even if the
        // (stale) granted-roles set happens to carry the role.
        var snapshot = Snapshot(true, GraphAppPermissions.DeviceManagementServiceConfigReadAll);

        Assert.False(AutopilotDeviceValidationConsentFunction.ResolveAccessPresent(snapshot));
    }
}
