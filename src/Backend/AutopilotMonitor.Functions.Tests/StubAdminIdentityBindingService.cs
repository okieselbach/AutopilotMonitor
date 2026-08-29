using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Test stub for <see cref="AdminIdentityBindingService"/> — answers the identity-binding check with a
/// fixed verdict and records grant-time bindings in memory, without any storage dependency. Used wherever
/// a service under test consumes the binding check but the test's subject is something else (row/role
/// resolution, the entitlement gate, revoke enforcement). The binding logic itself is pinned by
/// <see cref="AdminIdentityBindingServiceTests"/>; the end-to-end authorization effect by
/// <see cref="AdminIdentityBindingAuthorizationTests"/>.
/// </summary>
internal sealed class StubAdminIdentityBindingService : AdminIdentityBindingService
{
    private readonly Func<AdminIdentity?, bool> _verdict;

    /// <summary>Every (upn, tenantId, objectId) passed to EnsureBoundAsync / RebindAsync, in call order.</summary>
    public List<(string Upn, string TenantId, string? ObjectId)> Bindings { get; } = new();

    public StubAdminIdentityBindingService(bool bound = true) : this(_ => bound)
    {
    }

    public StubAdminIdentityBindingService(Func<AdminIdentity?, bool> verdict)
        : base(Mock.Of<IAdminRepository>(), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AdminIdentityBindingService>.Instance)
    {
        _verdict = verdict;
    }

    public override Task<bool> IsBoundAsync(AdminIdentity? identity) => Task.FromResult(_verdict(identity));

    public override Task<AdminIdentityBinding> EnsureBoundAsync(string upn, string tenantId, string? objectId, string boundBy)
    {
        Bindings.Add((upn.ToLowerInvariant(), tenantId.ToLowerInvariant(), objectId?.ToLowerInvariant()));
        return Task.FromResult(new AdminIdentityBinding
        {
            Upn = upn.ToLowerInvariant(),
            TenantId = tenantId.ToLowerInvariant(),
            ObjectId = objectId?.ToLowerInvariant() ?? string.Empty,
            BoundBy = boundBy,
        });
    }

    public override Task<AdminIdentityBinding> RebindAsync(string upn, string tenantId, string? objectId, string boundBy)
        => EnsureBoundAsync(upn, tenantId, objectId, boundBy);

    public override Task RemoveAsync(string upn) => Task.CompletedTask;
}
