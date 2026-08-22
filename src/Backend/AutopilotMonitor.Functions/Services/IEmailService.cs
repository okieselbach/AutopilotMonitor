namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Provider-neutral transactional email contract for the tenant-activation welcome mail.
/// <para>
/// Always fail-soft — implementations must NOT throw; activation correctness never depends
/// on email delivery (the Global Admin "Send welcome email" button is the manual fallback).
/// The farewell mail after offboarding lives on the sibling
/// <see cref="IOffboardFarewellEmailSender"/> contract; both are served by <see cref="EmailService"/>.
/// </para>
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Sends the tenant-activation welcome email. No-op when the provider is not configured
    /// or the recipient is empty.
    /// </summary>
    Task SendPreviewApprovedEmailAsync(string toEmail, string domainName, CancellationToken ct = default);

    /// <summary>
    /// Operator test send: the effective template of <paramref name="kind"/> (or an unsaved
    /// <paramref name="draftHtml"/>, placeholder-rendered) to <paramref name="toEmail"/>.
    /// Returns true when the provider accepted the message. Never throws.
    /// </summary>
    Task<bool> SendTestAsync(EmailTemplateKind kind, string toEmail, string domainName, string? draftHtml, CancellationToken ct = default);
}
