using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Scripted <see cref="TenantMemberRoleResolver"/>: the verdict function receives (tenantId, upn, appRoles)
/// and answers with the effective member role (null = non-member). Default verdict = nobody is a member.
/// </summary>
internal sealed class StubTenantMemberRoleResolver : TenantMemberRoleResolver
{
    /// <summary>The scripted verdict; replace it mid-test to change who is a member.</summary>
    public Func<string, string, IReadOnlyList<string>?, MemberRoleInfo?> Verdict { get; set; }

    /// <summary>Every (tenantId, upn) the resolver was asked about, in call order.</summary>
    public List<(string TenantId, string Upn)> Lookups { get; } = new();

    public StubTenantMemberRoleResolver(Func<string, string, IReadOnlyList<string>?, MemberRoleInfo?>? verdict = null)
        : base(null!, null!)
    {
        Verdict = verdict ?? ((_, _, _) => null);
    }

    public static StubTenantMemberRoleResolver Everyone(string role = AutopilotMonitor.Shared.Constants.TenantRoles.Admin)
        => new((_, _, _) => new MemberRoleInfo { Role = role });

    public override Task<MemberRoleInfo?> ResolveAsync(string tenantId, string upn, IReadOnlyList<string>? appRoles)
    {
        Lookups.Add((tenantId, upn));
        return Task.FromResult(Verdict(tenantId, upn, appRoles));
    }
}
