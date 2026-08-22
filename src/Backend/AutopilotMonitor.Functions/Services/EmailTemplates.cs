using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Templates for the product's transactional emails (sent via <see cref="EmailService"/>).
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

        <!-- Header: image-free. Base for every client: solid bgcolor band, text wordmark, brand mark
             drawn with CSS borders (trapezoid + a green wedge for the slanted right edge; must stay
             <div>s — Outlook paints transparent <td> borders white). Progressive enhancement where
             supported (Apple/iOS Mail, most of Gmail): three layered half-plane gradients form the
             facets and clip-path cuts the slanted bottom edge; Outlook shows a flat band instead.
             Geometry is the approved reference (scratch header-render.html): facets
             light right of (40%,0)->(70%,100%), dark right of (55%,0)->(85%,100%) and left of
             (78%,0)->(100%,60%). Change only against a side-by-side render. -->
        <tr>
        <td bgcolor=""#33b161"" style=""background-color:#33b161; background-image:linear-gradient(34.3deg, rgba(0,0,0,0) 0px, rgba(0,0,0,0) 387.6px, #3cbb6a 387.6px), linear-gradient(39.8deg, rgba(0,0,0,0) 0px, rgba(0,0,0,0) 326.5px, #2ea85a 326.5px), linear-gradient(39.8deg, rgba(0,0,0,0) 0px, rgba(0,0,0,0) 268.9px, #3cbb6a 268.9px); clip-path:polygon(0 0, 100% 0, 100% 100%, 0 82%); padding:28px 32px 52px; line-height:0; font-size:0;"">
          <table cellpadding=""0"" cellspacing=""0"" border=""0""><tr>
            <td valign=""middle"" style=""padding:0 12px 0 0; line-height:0; font-size:0;"">
              <div style=""width:0; height:20px; border-left:34px solid #ffffff; border-top:13px solid transparent; border-bottom:0; border-right:0; display:block; font-size:0; line-height:0;""></div>
              <div style=""width:0; height:0; border-right:4px solid #33b161; border-bottom:33px solid transparent; border-left:0; border-top:0; margin:-33px 0 0 30px; display:block; font-size:0; line-height:0;""></div>
              <div style=""width:0; height:0; border-right:34px solid transparent; border-bottom:2px solid #33b161; border-left:0; border-top:0; margin:-2px 0 0 0; display:block; font-size:0; line-height:0;""></div>
            </td>
            <td valign=""middle"" style=""color:#ffffff; font-size:22px; font-weight:600; letter-spacing:-0.2px; line-height:1; white-space:nowrap; font-family:-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;"">Autopilot Monitor</td>
          </tr></table>
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
              puts it to work in a real environment helps shape what it becomes &ndash; we're
              genuinely grateful you gave it a chance.
            </p>

            <!-- Gentle feedback ask -->
            <p style=""color:#374151; font-size:15px; line-height:1.6; margin:0 0 12px;"">
              If something didn't fit &ndash; a missing feature, a bug that got in the way, or
              simply a better alternative &ndash; we'd love to hear about it. A couple of honest
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
              replies &ndash; please use the links above if you'd like to get in touch.
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

        <!-- Header: image-free. Base for every client: solid bgcolor band, text wordmark, brand mark
             drawn with CSS borders (trapezoid + a green wedge for the slanted right edge; must stay
             <div>s — Outlook paints transparent <td> borders white). Progressive enhancement where
             supported (Apple/iOS Mail, most of Gmail): three layered half-plane gradients form the
             facets and clip-path cuts the slanted bottom edge; Outlook shows a flat band instead.
             Geometry is the approved reference (scratch header-render.html): facets
             light right of (40%,0)->(70%,100%), dark right of (55%,0)->(85%,100%) and left of
             (78%,0)->(100%,60%). Change only against a side-by-side render. -->
        <tr>
        <td bgcolor=""#33b161"" style=""background-color:#33b161; background-image:linear-gradient(34.3deg, rgba(0,0,0,0) 0px, rgba(0,0,0,0) 387.6px, #3cbb6a 387.6px), linear-gradient(39.8deg, rgba(0,0,0,0) 0px, rgba(0,0,0,0) 326.5px, #2ea85a 326.5px), linear-gradient(39.8deg, rgba(0,0,0,0) 0px, rgba(0,0,0,0) 268.9px, #3cbb6a 268.9px); clip-path:polygon(0 0, 100% 0, 100% 100%, 0 82%); padding:28px 32px 52px; line-height:0; font-size:0;"">
          <table cellpadding=""0"" cellspacing=""0"" border=""0""><tr>
            <td valign=""middle"" style=""padding:0 12px 0 0; line-height:0; font-size:0;"">
              <div style=""width:0; height:20px; border-left:34px solid #ffffff; border-top:13px solid transparent; border-bottom:0; border-right:0; display:block; font-size:0; line-height:0;""></div>
              <div style=""width:0; height:0; border-right:4px solid #33b161; border-bottom:33px solid transparent; border-left:0; border-top:0; margin:-33px 0 0 30px; display:block; font-size:0; line-height:0;""></div>
              <div style=""width:0; height:0; border-right:34px solid transparent; border-bottom:2px solid #33b161; border-left:0; border-top:0; margin:-2px 0 0 0; display:block; font-size:0; line-height:0;""></div>
            </td>
            <td valign=""middle"" style=""color:#ffffff; font-size:22px; font-weight:600; letter-spacing:-0.2px; line-height:1; white-space:nowrap; font-family:-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;"">Autopilot Monitor</td>
          </tr></table>
        </td>
        </tr>

        <!-- Body -->
        <tr>
          <td style=""padding:40px;"">
            <h2 style=""color:#111827; margin:0 0 16px; font-size:20px;"">Welcome to Autopilot Monitor!</h2>

            <p style=""color:#374151; font-size:15px; line-height:1.6; margin:0 0 16px;"">
              Great news &ndash; access for <strong>{displayDomain}</strong> has been activated and is ready to use.
              You can now <a href=""{Constants.PortalBaseUrl}"" target=""_blank"" style=""color:#1e8a4c; text-decoration:underline;"">sign in</a> and start monitoring your Windows Autopilot enrollments in real time.
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
              Thanks for being part of the journey &ndash; enjoy Autopilot Monitor!
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
