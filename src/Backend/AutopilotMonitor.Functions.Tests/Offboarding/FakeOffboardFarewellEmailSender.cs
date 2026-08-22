using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests.Offboarding;

/// <summary>
/// Test double for <see cref="IOffboardFarewellEmailSender"/>. Records every invocation so
/// the handler-side tests can assert "did the farewell send fire after History → Completed?"
/// without booting the real mail provider client. Default = silent noop; set <see cref="ThrowOnSend"/>
/// to simulate downstream failures and prove the handler's fail-soft contract.
/// </summary>
internal sealed class FakeOffboardFarewellEmailSender : IOffboardFarewellEmailSender
{
    public readonly List<(string ToEmail, string DomainName, string TenantId)> Calls = new();
    public System.Exception? ThrowOnSend { get; set; }

    public Task SendAsync(string toEmail, string domainName, string tenantId, CancellationToken ct = default)
    {
        Calls.Add((toEmail, domainName, tenantId));
        if (ThrowOnSend is { } ex) throw ex;
        return Task.CompletedTask;
    }
}
