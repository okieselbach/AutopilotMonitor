namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Sends the post-completion "sorry to see you go" farewell email to the tenant's
/// Preview-Notification-Email captured at Phase 1 of tenant offboarding.
/// <para>
/// Invocation point: <c>TenantOffboardingHandler.RunPostDrainPhasesAsync</c> immediately
/// after the History terminal write (Side-effect 6). Always fail-soft — implementations
/// must NOT throw; the offboarding correctness contract does not depend on email delivery.
/// The return value is what separates "sent" from "provider refused / not configured" for the
/// <c>FarewellEmailSent</c> / <c>FarewellEmailFailed</c> ops events the handler records.
/// </para>
/// </summary>
public interface IOffboardFarewellEmailSender
{
    /// <returns>True when the provider accepted the message; false when the send was skipped
    /// (provider not configured, empty recipient) or the provider refused it.</returns>
    Task<bool> SendAsync(string toEmail, string domainName, string tenantId, CancellationToken ct = default);
}
