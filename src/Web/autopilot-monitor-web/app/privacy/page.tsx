import { LandingNavbar } from "../../components/landing/LandingNavbar";
import { SiteFooter } from "../../components/SiteFooter";
import { DOCS_URL } from "@/utils/config";

const LAST_UPDATED = "29 August 2026";
const DOCS_SECURITY_FAQ = `${DOCS_URL}/trust/security-faq`;
const DOCS_DATA_FLOWS = `${DOCS_URL}/trust/data-flows`;

function DocsLink({ href, children }: { href: string; children: React.ReactNode }) {
  return (
    <a
      href={href}
      target="_blank"
      rel="noopener noreferrer"
      className="text-[var(--lp-accent-ink)] hover:opacity-80 underline"
    >
      {children}
    </a>
  );
}

export default function PrivacyPage() {
  return (
    <div className="landing-v2 min-h-screen bg-[var(--lp-bg)]">
      <LandingNavbar />
      <header className="px-4 sm:px-6 lg:px-8 pt-14 sm:pt-16">
        <div className="max-w-7xl mx-auto">
          <p className="text-[11px] font-semibold uppercase tracking-[0.24em] text-[var(--lp-ink-faint)]">Legal</p>
          <h1 className="mt-3 text-3xl sm:text-4xl font-bold tracking-tight text-[var(--lp-ink)]">
            Privacy Policy
          </h1>
        </div>
      </header>
      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-6 text-[15px]">
        <div className="bg-[var(--lp-surface)] border border-[var(--lp-line-soft)] rounded-xl p-6 space-y-4">
          <p className="text-sm text-gray-500">Last updated: {LAST_UPDATED}</p>
          <p className="text-gray-700">
            Autopilot Monitor collects technical telemetry about Windows Autopilot enrollments so that IT teams can see
            what happened during a device provisioning and why it failed. This policy explains what is collected, where it
            is stored, who can reach it, and how long it is kept. The technical detail behind every statement here is
            published in the <DocsLink href={DOCS_SECURITY_FAQ}>Security &amp; Privacy FAQ</DocsLink>.
          </p>

          <h2 className="text-lg font-semibold text-gray-900 mt-6">Roles: Who Is Responsible for What</h2>
          <p className="text-gray-700">
            For enrollment telemetry, <strong>your organization is the controller and Autopilot Monitor acts as your
            processor</strong>. You decide which devices are monitored, what additional data your gather rules collect,
            how long it is retained, and when it is deleted.
          </p>
          <p className="text-gray-700">
            For operating the service itself — administrator accounts, the audit trail of portal actions, and operational
            telemetry — <strong>glueckkanja AG</strong> is the controller.
          </p>
          <p className="text-gray-700">
            glueckkanja AG, a German company certified to ISO/IEC 27001, operates Autopilot Monitor for{" "}
            <strong>both plans</strong> — the same service, the same infrastructure, the same protection measures. Company
            details are in the <a href="https://www.glueckkanja.com/en/imprint" target="_blank" rel="noopener noreferrer" className="text-[var(--lp-accent-ink)] hover:opacity-80 underline">Imprint</a>.
            The project is maintained by Oliver Kieselbach, who acts in that role on behalf of glueckkanja AG and is the
            contact for the open-source project and the Community edition.
          </p>
          <p className="text-gray-700">
            A <strong>data processing agreement (DPA / AVV) is available on request</strong>, concluded with
            glueckkanja AG. On the Pro plan it forms part of the written agreement.
          </p>

          <h2 className="text-lg font-semibold text-gray-900 mt-6">What We Collect</h2>
          <h3 className="text-lg font-medium text-gray-800 mt-4">From enrolling devices</h3>
          <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
            <li><strong>Device identity</strong> — serial number, device name, manufacturer, model, and the Entra ID tenant the device enrolls into</li>
            <li><strong>Enrollment progress</strong> — phases, ESP stages, application and script results, policy activity, reboots, timings, and failure codes</li>
            <li><strong>Device context</strong> — OS build, hardware characteristics, disk and network state, plus whatever your own gather rules request</li>
            <li><strong>Approximate location</strong> — country, region, city, and approximate coordinates, if geolocation is enabled for your tenant</li>
          </ul>

          <h3 className="text-lg font-medium text-gray-800 mt-4">From portal users</h3>
          <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
            <li>Sign-in identity from Microsoft Entra ID — user principal name, display name, and tenant ID</li>
            <li>Audit records of administrative actions, including who performed them and when</li>
            <li>Content you choose to enter about a session — report comments and annotations (a verdict on the analysis plus notes) — stored with your tenant&apos;s data and attributed to the author</li>
            <li>Operational request telemetry used to run and support the service</li>
            <li>A <strong>contact address</strong> for your tenant, if one is provided</li>
          </ul>
          <p className="text-gray-700">
            The contact address is used <strong>only to reach you about this service</strong> — a technical problem
            affecting your tenant, a security matter, or a change that needs an administrator&apos;s attention. It is
            never used for marketing and never shared. Your administrators set and change it under Settings → Tenant →
            Contact, and clearing it removes it. Where an organization gave a notification address during sign-up
            (tenant activation), that address is copied once as the initial contact and is yours to change from then on.
          </p>

          <h2 className="text-lg font-semibold text-gray-900 mt-6">What We Do Not Collect</h2>
          <p className="text-gray-700">
            During Autopilot provisioning the user signs in once to start the process and then does not interact with the
            device while it provisions. The agent captures <strong>no browsing history, no file or document content, no
            keystrokes, no screen content, and no application usage tracking</strong>. It removes itself when enrollment
            finishes. The service is designed for operational transparency, not user surveillance.
          </p>
          <p className="text-gray-700">
            Gather rules cannot be used to widen this: <code className="text-sm bg-gray-100 px-1 py-0.5 rounded">C:\Users</code>{" "}
            is always blocked for privacy reasons, and downloading files, creating users, manipulating boot configuration,
            and establishing persistence are hard-blocked and cannot be enabled by configuration.
          </p>

          <h2 className="text-lg font-semibold text-gray-900 mt-6">IP Addresses and Geolocation</h2>
          <p className="text-gray-700">
            Geolocation is a tenant setting and is <strong>enabled by default</strong>. While it is enabled:
          </p>
          <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
            <li>The <strong>session record</strong> stores only the derived location — country, region, city, and approximate coordinates. The IP is deliberately excluded from the location event shown in the timeline.</li>
            <li>The <strong>outbound public IP is additionally stored once per session as a separate diagnostic event.</strong> It is hidden from the timeline view by default, but it is retained and queryable, and it expires with your retention period like any other event.</li>
            <li>Service request telemetry does not carry device IP addresses; the platform masks them and the application never sets them.</li>
            <li>A source IP is also stored on <strong>distress reports</strong> — the emergency channel an agent uses to report that it is failing — as part of that incident record.</li>
          </ul>
          <p className="text-gray-700">
            <strong>Disabling geolocation stops all of this</strong> — no IP and no location data is collected. It is a
            tenant setting and also an agent command-line switch. Only the Geographic Performance view is affected.
          </p>

          <h2 className="text-lg font-semibold text-gray-900 mt-6">Where Your Data Is Stored</h2>
          <p className="text-gray-700">
            All customer data — sessions, events, configuration, audit logs, diagnostics, and backups — is stored in
            Microsoft Azure in <strong>Germany West Central</strong>. The only component outside that region is the portal
            front-end, served as static assets from West Europe; it stores no customer data. There is no cross-region
            replication and no transfer of customer data outside the EU by the platform.
          </p>

          <h2 className="text-lg font-semibold text-gray-900 mt-6">Who Can Access Your Data</h2>
          <p className="text-gray-700">
            Your data is not sold, and it is not shared with third parties for their own purposes. Access is limited to:
          </p>
          <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
            <li><strong>Authenticated users in your own tenant</strong>, according to their role — Admin, Operator, Viewer, or a role-less Member limited to the Progress Portal</li>
            <li><strong>Platform operators</strong> — Global Admin for operations and support, and Global Reader for read-only support with configuration secrets redacted</li>
            <li><strong>Delegated (MSP) administrators you or your provider have been granted</strong> — read-only, limited to exactly the tenants in scope, with configuration secrets redacted, and with every grant and revocation written to <em>your</em> tenant&apos;s audit log so you can always see who was given access to your data</li>
          </ul>
          <p className="text-gray-700">
            Delegated administration is a Pro capability and is off unless explicitly granted. Full detail on the
            isolation model is in the <DocsLink href={DOCS_SECURITY_FAQ}>Security &amp; Privacy FAQ</DocsLink>.
          </p>

          <h2 className="text-lg font-semibold text-gray-900 mt-6">External Services</h2>
          <p className="text-gray-700">
            <strong>Microsoft Azure is the only place your data is stored.</strong> Mailchimp Transactional (Mandrill)
            delivers the welcome email when a tenant is activated and the farewell email after offboarding — it receives
            an administrator&apos;s email address and the tenant domain, never enrollment telemetry; open and click
            tracking is disabled. Vulnerability reference data is read inbound from NVD, the CISA KEV
            catalog, MSRC, and the FIRST EPSS service; nothing about your environment is sent to them — the EPSS
            lookup carries only public CVE identifiers from the shared reference cache.
          </p>
          <p className="text-gray-700">
            Further connections exist only because you configure them: your own Azure storage account for diagnostics,
            notification channels such as Teams, Slack, Discord, or a generic webhook, and your own AI assistant if a user connects
            one through the MCP integration. The platform itself makes no calls to any AI or LLM provider.{" "}
            <DocsLink href={DOCS_DATA_FLOWS}>Data Flows &amp; External Services</DocsLink> maps every outbound connection
            and what it carries. The data processing agreement, available on request, is the authoritative document for
            the parties engaged and the terms of their engagement.
          </p>

          <h2 className="text-lg font-semibold text-gray-900 mt-6">Diagnostics Uploads</h2>
          <p className="text-gray-700">
            Diagnostics upload is <strong>off by default</strong>. When enabled, the default destination is{" "}
            <strong>your own Azure storage account</strong> — the package never reaches our infrastructure. Hosted upload
            exists as an alternative but is opt-in only and requires an explicit administrator action — in Settings, or
            in the one-step dialog offered on a session — always behind a clearly marked disclosure that data leaves your
            tenant. It is never enabled silently. One explicit exception: when an administrator submits a session report
            for analysis and chooses to include the session&apos;s uploaded diagnostics archive, a copy of that package
            is stored with the report so it remains available for the investigation.
          </p>
          <p className="text-gray-700">
            Once upload is configured, an <strong>Admin or Operator in your own tenant</strong> can also request a
            package on demand from an enrollment that is still running, instead of waiting for it to finish. It collects
            exactly what the automatic package collects — the configured paths, nothing wider — the device only acts on
            the request when the agent next checks in, and both the request and its outcome appear as events on that
            session&apos;s timeline. Viewers and Progress-Portal members cannot trigger it, and it does nothing on a
            tenant that has no upload destination configured.
          </p>

          <h2 className="text-lg font-semibold text-gray-900 mt-6">How Long We Keep Data</h2>
          <p className="text-gray-700">
            Retention is <strong>configured by you per tenant, defaulting to 90 days</strong> — 7 to 90 days on the
            Community plan and 7 to 365 days on Pro. Expired sessions are purged automatically. You additionally
            control:
          </p>
          <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
            <li><strong>Delete session</strong> — remove an individual monitoring session on demand</li>
            <li><strong>Offboard tenant</strong> — remove your tenant&apos;s data and configuration from the service entirely, as a verified multi-phase cascade</li>
          </ul>
          <p className="text-gray-700">
            Two things intentionally survive tenant offboarding, neither of which contains enrollment telemetry or
            personal data:
          </p>
          <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
            <li><strong>Product feedback</strong> you submitted — it is not tied to enrollment data and is what improves the product.</li>
            <li><strong>Custom rules and IME log patterns</strong> you authored are archived rather than deleted. Detection knowledge is what makes this product useful, so contributed rules and patterns are subject to entering the community pool — the next organization hitting the same enrollment failure gets a diagnosis instead of a mystery. A rule is a detection definition, not device data.</li>
          </ul>
          <p className="text-gray-700">
            Either can be removed on request.
          </p>

          <h2 className="text-lg font-semibold text-gray-900 mt-6">How Your Data Is Protected</h2>
          <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
            <li><strong>Device authentication by mutual TLS</strong> using the Intune MDM client certificate, validated against pinned Intune root CAs rather than the operating system trust store — and rejected if no trust anchor loads</li>
            <li><strong>Device registration validation via Microsoft Graph</strong> — only devices your tenant actually knows are accepted (Windows Autopilot registration, corporate device identifiers, or the Windows 365 Cloud PC inventory), with an optional hardware allow-list on top</li>
            <li><strong>Entra ID authentication</strong> for portal users with a restricted signing-algorithm allow-list and identity PII logging disabled</li>
            <li><strong>Fail-closed authorization</strong> — every API route must be registered with an access policy; an unregistered route is unreachable</li>
            <li><strong>Structural tenant isolation</strong> — storage partitioning by tenant, with the tenant identity taken from the validated token and never from a client-supplied header</li>
            <li><strong>Encryption</strong> — HTTPS with a TLS 1.2 floor in transit, Azure Storage encryption at rest with platform-managed keys</li>
            <li><strong>Managed identity instead of storage keys</strong>, and secret-less OIDC deployment pipelines</li>
            <li><strong>Rate limiting</strong> per device, per portal user, and per MCP user</li>
            <li><strong>Verifiable agent binaries</strong> — Sigstore build attestation plus a four-stage integrity chain through download, backend cross-check, and runtime self-verification</li>
            <li><strong>Manifest-first deletion</strong> — what will be deleted is captured, restorably, before anything is removed</li>
          </ul>

          <h2 className="text-lg font-semibold text-gray-900 mt-6">Your Rights</h2>
          <p className="text-gray-700">
            Where the GDPR applies, you have the right to access, correction, deletion, restriction of processing,
            portability, and to object to processing. We act on such requests within the statutory time limits. In
            practice most requests resolve immediately in the portal, because deletion of individual sessions, full tenant
            offboarding, and the retention period are all controls you hold yourself.
          </p>
          <p className="text-gray-700">
            Where Autopilot Monitor acts as your processor, requests from your own employees should be directed to your
            organization; we support you in fulfilling them.
          </p>

          <h2 className="text-lg font-semibold text-gray-900 mt-6">Changes to This Policy</h2>
          <p className="text-gray-700">
            Material changes are reflected in the &quot;last updated&quot; date above and announced through the service
            announcements in the portal. If you hold a signed data processing agreement, notification follows the terms of
            that agreement.
          </p>

          <h2 className="text-lg font-semibold text-gray-900 mt-6">Contact</h2>
          <p className="text-gray-700">
            For privacy questions, a data processing agreement, or a data subject request, contact glueckkanja AG using
            the details in the <a href="https://www.glueckkanja.com/en/imprint" target="_blank" rel="noopener noreferrer" className="text-[var(--lp-accent-ink)] hover:opacity-80 underline">Imprint</a>.
            For the open-source project and the Community edition you can also reach the maintainer via{" "}
            <a href="https://www.linkedin.com/in/oliver-kieselbach" target="_blank" rel="noopener noreferrer" className="text-[var(--lp-accent-ink)] hover:opacity-80 underline">LinkedIn</a>{" "}
            or open a{" "}
            <a href="https://github.com/okieselbach/AutopilotMonitor/issues" target="_blank" rel="noopener noreferrer" className="text-[var(--lp-accent-ink)] hover:opacity-80 underline">GitHub issue</a>.
          </p>
        </div>
      </main>
      <SiteFooter />
    </div>
  );
}
