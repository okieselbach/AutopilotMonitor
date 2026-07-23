using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Email templates for Resend notifications.
/// Keep all email content here for easy maintenance.
/// Temporary — remove after GA.
/// </summary>
public static class EmailTemplates
{
    public const string PreviewApprovedSubject = "Your Autopilot Monitor Private Preview is ready!";

    // ── Offboarding farewell email ────────────────────────────────────────────
    //
    // PLACEHOLDER ONLY. The actual subject + HTML body must be written before flipping
    // ResendEmailService.OffboardFarewellEmailArmed to true. The [DRAFT] prefix on the
    // subject + the explicit "TEMPLATE NOT FINALISED" body are deliberate safety nets so
    // an accidental arm-flip cannot ship a polished-looking email.
    //
    // TODO(offboard-farewell-email): write the final copy. Open items for the author:
    //   1. Tone — apologetic / matter-of-fact / curious? "Sorry to see you go" feedback ask?
    //   2. Feedback form — Tally / Typeform / GitHub-Issue link? Embed or just link?
    //   3. Re-onboarding link / docs link?
    //   4. Localisation — single English version for now, or per-tenant locale lookup?
    //   5. Unsubscribe / one-time-mail disclaimer (this is the LAST mail we send this tenant).

    public const string OffboardingFarewellSubject = "[DRAFT] Sorry to see you go";

    /// <summary>
    /// Placeholder offboarding-farewell HTML body. Returns a clearly marked TODO block
    /// so an accidental arm-flip is visually unmistakeable. Replace with the real copy
    /// before arming.
    /// </summary>
    public static string GetOffboardingFarewellHtml(string domainName)
    {
        var displayDomain = string.IsNullOrWhiteSpace(domainName) ? "your organization" : domainName;

        return $@"
<!DOCTYPE html>
<html>
<head><meta charset=""utf-8""></head>
<body style=""margin:0; padding:32px; background-color:#fef2f2; font-family:-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;"">
  <table width=""600"" cellpadding=""0"" cellspacing=""0"" style=""margin:0 auto; background-color:#ffffff; border:2px dashed #dc2626; border-radius:12px;"">
    <tr><td style=""padding:32px;"">
      <h1 style=""color:#dc2626; margin:0 0 16px; font-size:20px;"">[DRAFT] TEMPLATE NOT FINALISED</h1>
      <p style=""color:#374151; font-size:15px; line-height:1.6; margin:0 0 12px;"">
        This is a placeholder for the post-offboarding farewell email for <strong>{displayDomain}</strong>.
      </p>
      <p style=""color:#374151; font-size:15px; line-height:1.6; margin:0;"">
        TODO before arming: final copy, feedback-form link, re-onboarding link. The arm-switch
        lives at <code>ResendEmailService.OffboardFarewellEmailArmed</code>.
      </p>
    </td></tr>
  </table>
</body>
</html>";
    }

    /// <summary>
    /// Returns the HTML body for the Private Preview approval welcome email.
    /// </summary>
    public static string GetPreviewApprovedHtml(string domainName)
    {
        var displayDomain = string.IsNullOrWhiteSpace(domainName) ? "your organization" : domainName;

        return $@"
<!DOCTYPE html>
<html>
<head><meta charset=""utf-8""></head>
<body style=""margin:0; padding:0; background-color:#f3f4f6; font-family:-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;"">
  <table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background-color:#f3f4f6; padding:40px 20px;"">
    <tr><td align=""center"">
      <table width=""600"" cellpadding=""0"" cellspacing=""0"" style=""background-color:#ffffff; border-radius:12px; overflow:hidden; box-shadow:0 4px 6px rgba(0,0,0,0.07);"">

        <!-- Header -->
        <tr>
          <td style=""background:linear-gradient(135deg,#2563eb,#4f46e5); padding:32px 40px; text-align:center;"">
            <h1 style=""color:#ffffff; margin:0; font-size:24px; font-weight:700;"">Autopilot Monitor</h1>
            <p style=""color:#c7d2fe; margin:8px 0 0; font-size:14px;"">Private Preview</p>
          </td>
        </tr>

        <!-- Body -->
        <tr>
          <td style=""padding:40px;"">
            <h2 style=""color:#111827; margin:0 0 16px; font-size:20px;"">Welcome to the Private Preview!</h2>

            <p style=""color:#374151; font-size:15px; line-height:1.6; margin:0 0 16px;"">
              Great news &mdash; the Private Preview for <strong>{displayDomain}</strong> has been approved and is ready to use.
              You can now sign in and start monitoring your Windows Autopilot enrollments in real time.
            </p>

            <p style=""color:#374151; font-size:15px; line-height:1.6; margin:0 0 24px;"">
              To get started, check out the documentation for setup instructions and configuration options:
            </p>

            <!-- CTA Button -->
            <table cellpadding=""0"" cellspacing=""0"" style=""margin:0 auto 32px;"">
              <tr>
                <td style=""background-color:#2563eb; border-radius:8px;"">
                  <a href=""{Constants.DocsBaseUrl}"" target=""_blank""
                     style=""display:inline-block; padding:14px 32px; color:#ffffff; font-size:15px; font-weight:600; text-decoration:none;"">
                    View Documentation
                  </a>
                </td>
              </tr>
            </table>

            <!-- Private Preview Note -->
            <div style=""background-color:#fef3c7; border:1px solid #fde68a; border-radius:8px; padding:16px 20px; margin:0 0 24px;"">
              <p style=""color:#92400e; font-size:14px; line-height:1.5; margin:0;"">
                <strong>Please note:</strong> Autopilot Monitor is in active development. Some features are still being built
                and things may occasionally not work as expected. Your patience and understanding are greatly appreciated!
              </p>
            </div>

            <!-- Feedback -->
            <p style=""color:#374151; font-size:15px; line-height:1.6; margin:0 0 12px;"">
              Your feedback is incredibly valuable and helps shape the product. If you run into issues
              or have ideas for improvements, please don't hesitate to reach out:
            </p>

            <ul style=""color:#374151; font-size:14px; line-height:1.8; margin:0 0 24px; padding-left:20px;"">
              <li><a href=""https://github.com/okieselbach/Autopilot-Monitor/issues"" target=""_blank"" style=""color:#2563eb; text-decoration:underline;"">Open a GitHub Issue</a></li>
              <li><a href=""https://www.linkedin.com/in/oliver-kieselbach/"" target=""_blank"" style=""color:#2563eb; text-decoration:underline;"">Connect on LinkedIn</a></li>
            </ul>

            <p style=""color:#6b7280; font-size:14px; line-height:1.6; margin:0;"">
              Thanks for being an early adopter &mdash; enjoy the Private Preview!
            </p>
          </td>
        </tr>

        <!-- Footer -->
        <tr>
          <td style=""background-color:#f9fafb; padding:20px 40px; border-top:1px solid #e5e7eb; text-align:center;"">
            <p style=""color:#9ca3af; font-size:12px; margin:0;"">
              &copy; 2026 Autopilot Monitor &middot; Powered by Azure and Microsoft Identity
            </p>
          </td>
        </tr>

      </table>
    </td></tr>
  </table>
</body>
</html>";
    }
}
