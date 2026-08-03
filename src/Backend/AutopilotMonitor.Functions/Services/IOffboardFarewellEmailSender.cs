namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Sends the post-completion "sorry to see you go" farewell email to the tenant's
/// Preview-Notification-Email captured at Phase 1 of tenant offboarding.
/// <para>
/// Invocation point: <c>TenantOffboardingHandler.RunPostDrainPhasesAsync</c> immediately
/// after the History terminal write (Side-effect 6). Always fail-soft — implementations
/// must NOT throw; the offboarding correctness contract does not depend on email delivery.
/// </para>
/// </summary>
public interface IOffboardFarewellEmailSender
{
    Task SendAsync(string toEmail, string domainName, string tenantId, CancellationToken ct = default);
}
