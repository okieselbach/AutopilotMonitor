using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Email templates for Resend notifications.
/// Keep all email content here for easy maintenance.
/// </summary>
public static class EmailTemplates
{
    public const string PreviewApprovedSubject = "Your Autopilot Monitor access is ready!";

    // ── Offboarding farewell email ────────────────────────────────────────────
    //
    // Sent once, after the offboarding worker finishes Phase 2 (post History terminal
    // write). Deliberately makes no claims about what data was or wasn't deleted —
    // custom rules are archived and the audit history row survives, so the only durable
    // statement is "the offboarding is complete". Feedback pointers go to GitHub/LinkedIn
    // (same channels as the welcome email) because this is a noreply sender and the
    // in-app feedback widget is gone once the tenant is offboarded.

    public const string OffboardingFarewellSubject = "Thank you for using Autopilot Monitor";

    /// <summary>
    /// Returns the HTML body for the post-offboarding farewell email.
    /// </summary>
    public static string GetOffboardingFarewellHtml(string domainName)
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
          <td style=""background:linear-gradient(135deg,#1e8a4c,#33b161); padding:32px 40px; text-align:center;"">
            <h1 style=""color:#ffffff; margin:0; font-size:24px; font-weight:700;"">Autopilot Monitor</h1>
            <p style=""color:#c9efd8; margin:8px 0 0; font-size:14px;"">Windows Autopilot monitoring</p>
          </td>
        </tr>

        <!-- Body -->
        <tr>
          <td style=""padding:40px;"">
            <h2 style=""color:#111827; margin:0 0 16px; font-size:20px;"">Sorry to see you go</h2>

            <p style=""color:#374151; font-size:15px; line-height:1.6; margin:0 0 16px;"">
              The offboarding of <strong>{displayDomain}</strong> from Autopilot Monitor is complete.
            </p>

            <p style=""color:#374151; font-size:15px; line-height:1.6; margin:0 0 24px;"">
              Thank you for taking the time to try Autopilot Monitor. Every organization that
              puts it to work in a real environment helps shape what it becomes &mdash; we're
              genuinely grateful you gave it a chance.
            </p>

            <!-- Gentle feedback ask -->
            <p style=""color:#374151; font-size:15px; line-height:1.6; margin:0 0 12px;"">
              If something didn't fit &mdash; a missing feature, a bug that got in the way, or
              simply a better alternative &mdash; we'd love to hear about it. A couple of honest
              sentences help more than you'd think:
            </p>

            <ul style=""color:#374151; font-size:14px; line-height:1.8; margin:0 0 24px; padding-left:20px;"">
              <li><a href=""https://github.com/okieselbach/AutopilotMonitor/issues"" target=""_blank"" style=""color:#1e8a4c; text-decoration:underline;"">Open a GitHub Issue</a></li>
              <li><a href=""https://www.linkedin.com/in/oliver-kieselbach/"" target=""_blank"" style=""color:#1e8a4c; text-decoration:underline;"">Connect on LinkedIn</a></li>
            </ul>

            <p style=""color:#374151; font-size:15px; line-height:1.6; margin:0 0 24px;"">
              And if your needs change down the road, you're welcome back anytime.
            </p>

            <p style=""color:#6b7280; font-size:13px; line-height:1.6; margin:0;"">
              This is the last email you'll receive from us. This mailbox doesn't accept
              replies &mdash; please use the links above if you'd like to get in touch.
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

    /// <summary>
    /// Returns the HTML body for the tenant-activation welcome email.
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
          <td style=""background:linear-gradient(135deg,#1e8a4c,#33b161); padding:32px 40px; text-align:center;"">
            <h1 style=""color:#ffffff; margin:0; font-size:24px; font-weight:700;"">Autopilot Monitor</h1>
            <p style=""color:#c9efd8; margin:8px 0 0; font-size:14px;"">Windows Autopilot monitoring</p>
          </td>
        </tr>

        <!-- Body -->
        <tr>
          <td style=""padding:40px;"">
            <h2 style=""color:#111827; margin:0 0 16px; font-size:20px;"">Welcome to Autopilot Monitor!</h2>

            <p style=""color:#374151; font-size:15px; line-height:1.6; margin:0 0 16px;"">
              Great news &mdash; access for <strong>{displayDomain}</strong> has been activated and is ready to use.
              You can now sign in and start monitoring your Windows Autopilot enrollments in real time.
            </p>

            <p style=""color:#374151; font-size:15px; line-height:1.6; margin:0 0 24px;"">
              To get started, check out the documentation for setup instructions and configuration options:
            </p>

            <!-- CTA Button -->
            <table cellpadding=""0"" cellspacing=""0"" style=""margin:0 auto 32px;"">
              <tr>
                <td style=""background-color:#1e8a4c; border-radius:8px;"">
                  <a href=""{Constants.DocsBaseUrl}"" target=""_blank""
                     style=""display:inline-block; padding:14px 32px; color:#ffffff; font-size:15px; font-weight:600; text-decoration:none;"">
                    View Documentation
                  </a>
                </td>
              </tr>
            </table>

            <!-- Active development note -->
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
              <li><a href=""https://github.com/okieselbach/AutopilotMonitor/issues"" target=""_blank"" style=""color:#1e8a4c; text-decoration:underline;"">Open a GitHub Issue</a></li>
              <li><a href=""https://www.linkedin.com/in/oliver-kieselbach/"" target=""_blank"" style=""color:#1e8a4c; text-decoration:underline;"">Connect on LinkedIn</a></li>
            </ul>

            <p style=""color:#6b7280; font-size:14px; line-height:1.6; margin:0;"">
              Thanks for being part of the journey &mdash; enjoy Autopilot Monitor!
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
