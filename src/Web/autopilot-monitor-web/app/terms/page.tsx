import { PublicPageHeader } from "../../components/PublicPageHeader";

const LAST_UPDATED = "21 July 2026";
const DOCS_SECURITY_FAQ = "https://docs.autopilotmonitor.com/trust/security-faq";
const DOCS_PLANS = "https://docs.autopilotmonitor.com/plans";
const GITHUB_ADVISORY = "https://github.com/okieselbach/Autopilot-Monitor/security/advisories/new";

export default function TermsPage() {
  return (
    <div className="min-h-screen bg-gray-50">
      <PublicPageHeader title="Terms of Use" />
      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-6">
        <div className="bg-white rounded-lg shadow p-6 space-y-4">
          <p className="text-sm text-gray-500">Last updated: {LAST_UPDATED}</p>
          <p className="text-gray-700">
            Autopilot Monitor is provided by <strong>glueckkanja AG</strong>, a German company certified to ISO/IEC
            27001 — see the <a href="https://www.glueckkanja.com/en/imprint" target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">Imprint</a>.
            Both plans run on the same service and the same infrastructure; the plan determines operating limits,
            support, and contractual commitments, not how the service works.
          </p>
          <p className="text-gray-700">
            The project was created and is maintained by <strong>Oliver Kieselbach</strong>, who is also the contact for
            the open-source project and the Community edition. He acts in that role on behalf of glueckkanja AG; he is
            not a separate contracting party.
          </p>

          <div className="grid gap-4 md:grid-cols-2 mt-4">
            <div className="p-4 border border-gray-200 rounded-lg">
              <h3 className="font-semibold text-gray-900 mb-1">Community</h3>
              <p className="text-sm text-gray-700">
                Free, invite-only, currently in Private Preview, and maintained by Oliver Kieselbach as an open
                community contribution. Provided <strong>without any commitment by glueckkanja AG</strong> as to
                availability, support, or fitness for a particular purpose. Support is community-based via GitHub.
              </p>
            </div>
            <div className="p-4 border border-gray-200 rounded-lg">
              <h3 className="font-semibold text-gray-900 mb-1">Enterprise</h3>
              <p className="text-sm text-gray-700">
                Commercial plan under a written agreement with glueckkanja AG. Includes support and reliability
                commitments, a data processing agreement, higher operating limits, extended retention, and delegated
                (MSP) administration. Where that agreement differs from these terms, the agreement prevails.
              </p>
            </div>
          </div>
          <p className="text-gray-700">
            The sections below apply to both plans unless stated otherwise. Plan details are documented under{" "}
            <a href={DOCS_PLANS} target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">Plans</a>.
          </p>

          <div className="bg-blue-50 border-l-4 border-blue-400 p-4 rounded">
            <p className="text-blue-900 text-sm">
              <strong>Who can use it.</strong> Autopilot Monitor requires a Microsoft Entra ID tenant with Intune and
              registered Autopilot devices. The <strong>Community</strong> plan is open to anyone with such a tenant —
              organizations, non-profits, and individuals running their own lab or personal tenant alike. The{" "}
              <strong>Enterprise</strong> plan is a commercial agreement and is offered to businesses and organizations
              acting in a commercial or professional capacity.
            </p>
          </div>
        </div>

        <div className="bg-white rounded-lg shadow p-6 space-y-4">
          <h2 className="text-xl font-semibold text-gray-900">Acceptable Use</h2>
          <p className="text-gray-700">By using this service, you agree that:</p>
          <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
            <li>The service is used to monitor and troubleshoot Windows Autopilot deployments for devices your organization is entitled to manage.</li>
            <li>You are responsible for ensuring your use complies with your organization&apos;s policies, your works council or employee representation obligations, and applicable law — including informing your users about monitoring where required.</li>
            <li>Access requires authentication via Microsoft Entra ID, and each tenant&apos;s data is accessible only to authorized users of that tenant, or to parties you have granted delegated access.</li>
            <li>You will not attempt to access another tenant&apos;s data, circumvent security controls, or probe the service for vulnerabilities without prior written agreement.</li>
            <li>You will not use gather rules or any other configuration to collect data unrelated to enrollment diagnostics, or to monitor individual employees.</li>
            <li>You will not use the service to build a competing product, or resell access without an agreement permitting it.</li>
            <li>Automated access, including through the MCP integration, stays within the published rate limits and quotas.</li>
          </ul>
          <p className="text-gray-700">
            <strong>Enforcement.</strong> Where use threatens the service or other tenants, we respond proportionately.
            Depending on the situation that may be a warning, reduced rate limits, suspension, or termination — chosen
            to fit the case, not worked through as a fixed sequence. Where the risk is immediate, access can be
            restricted at once. A warning is sent where a tenant contact address is on file and time permits.
          </p>
          <p className="text-gray-700">
            <strong>Security research is welcome</strong> and is not a violation of these terms when reported privately.
            Report findings through{" "}
            <a href={GITHUB_ADVISORY} target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">GitHub Security Advisories</a>{" "}
            rather than a public issue — see{" "}
            <a href={DOCS_SECURITY_FAQ} target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">Security &amp; Privacy FAQ</a>{" "}
            for what happens next. Do not access other tenants&apos; data, degrade the service, or run automated scans
            against production while testing.
          </p>
        </div>

        <div className="bg-white rounded-lg shadow p-6 space-y-4">
          <h2 className="text-xl font-semibold text-gray-900">Intellectual Property &amp; Licensing</h2>
          <div className="space-y-2 text-gray-700">
            <p><strong>Open source.</strong> The Autopilot Monitor source code is published on <a href="https://github.com/okieselbach/Autopilot-Monitor" target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">GitHub</a> under the licence stated in that repository. That licence governs the code — it does not grant rights to the hosted service, its infrastructure, its data, or the Autopilot Monitor name and branding.</p>
            <p><strong>Your data stays yours.</strong> Enrollment telemetry, configuration, and diagnostics belong to your organization. Using the service grants only the rights needed to operate it for you, as described in the <a href="/privacy" className="text-indigo-600 hover:text-indigo-800 underline">Privacy Policy</a>.</p>
            <p><strong>Rules you contribute</strong> are covered by the licence grant described under &quot;Your Data, Suspension and Termination&quot; below.</p>
            <p><strong>Third-party components</strong> remain the property of their respective owners and are used under their own licences; see the attributions below.</p>
          </div>
        </div>

        <div className="bg-white rounded-lg shadow p-6 space-y-4">
          <h2 className="text-xl font-semibold text-gray-900">Delegated (MSP) Administration</h2>
          <p className="text-gray-700">
            Delegated administration lets a managing organization see a defined set of customer tenants from one place. It
            is an Enterprise capability and applies only where access has been granted.
          </p>
          <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
            <li>Delegated access is <strong>read-only</strong> and limited to exactly the tenants in scope. Configuration secrets are redacted.</li>
            <li>A grant is either assigned by platform operators or delegated by the customer&apos;s own tenant admin; customer-initiated delegations require approval before they take effect.</li>
            <li>Every grant, revocation, and disablement is recorded in the <strong>managed customer tenant&apos;s</strong> audit log, so a customer can always determine who holds access to their data.</li>
            <li>A managing organization is responsible for having the necessary data protection agreements in place with the customers it manages.</li>
            <li>Access can be revoked at any time by the customer or by platform operators and takes effect within seconds.</li>
          </ul>
        </div>

        <div className="bg-white rounded-lg shadow p-6 space-y-4">
          <h2 className="text-xl font-semibold text-gray-900">Availability, Support and Data</h2>

          <div className="bg-amber-50 border-l-4 border-amber-500 p-4 rounded">
            <h3 className="font-semibold text-amber-900 mb-2">Community plan: provided &quot;AS-IS&quot;, no warranty</h3>
            <p className="text-amber-800 text-sm">
              The Community plan is provided free of charge, &quot;AS-IS&quot;, and without warranty of any kind, express
              or implied, including the implied warranties of merchantability and fitness for a particular purpose. It is
              currently in Private Preview: updates are frequent, availability is not guaranteed, and data structures may
              change. Production use is permitted and intended — with that trade-off understood.
            </p>
          </div>

          <div className="space-y-2 text-gray-700">
            <p><strong>Availability.</strong> The Community plan carries no uptime or availability commitment; interruptions, maintenance, and changes can occur without prior notice. The Enterprise plan carries the availability commitments set out in its agreement.</p>
            <p><strong>Support.</strong> Community support is best-effort via <a href="https://github.com/okieselbach/Autopilot-Monitor/issues" target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">GitHub issues</a>, provided by the project maintainer and the community, with no guaranteed response or resolution time; built-in rules and IME log patterns are community-maintained. Enterprise support follows the response commitments in its agreement.</p>
            <p><strong>Data durability.</strong> Autopilot Monitor is a monitoring system, not a system of record. Configuration, authorization, and rule data is backed up daily; session and event telemetry is time-bounded operational data and is <strong>not</strong> backed up. Retain anything you need for compliance or reporting purposes in your own systems.</p>
            <p><strong>Liability.</strong> To the extent permitted by law, glueckkanja AG is not liable for indirect, incidental, special, consequential, or punitive damages, or for loss of data, profit, or business, arising from use of or inability to use the service. For the Community plan, which is provided free of charge, liability is limited to intent and gross negligence. Liability for injury to life, body or health and under mandatory statutory provisions remains unaffected. For the Enterprise plan, the liability provisions of the written agreement apply.</p>
            <p><strong>Use at your own risk.</strong> The service reports on enrollments; it does not perform them. Operational decisions you take based on its output remain your responsibility.</p>
          </div>
        </div>

        <div className="bg-white rounded-lg shadow p-6 space-y-4">
          <h2 className="text-xl font-semibold text-gray-900">Your Data, Suspension and Termination</h2>
          <div className="space-y-2 text-gray-700">
            <p><strong>Ownership.</strong> Your enrollment telemetry remains yours. Autopilot Monitor processes it to provide the service, as described in the <a href="/privacy" className="text-indigo-600 hover:text-indigo-800 underline">Privacy Policy</a>.</p>
            <p><strong>Your controls.</strong> You set the retention period, delete individual sessions, and offboard your tenant entirely at any time — no support ticket required.</p>
            <p><strong>Rules you contribute.</strong> Analyze rules, gather rules, and IME log patterns you author are detection definitions, not device data. By creating them you grant a non-exclusive, royalty-free right to retain them and to include them in the shared community rule pool, so that other organizations can benefit from a detection you built. This is the reciprocal side of a product whose built-in rules are community-maintained. You keep the right to use your own rules however you like, and you can request removal from the pool at any time.</p>
            <p><strong>Suspension.</strong> Access may be suspended where use threatens the integrity, security, or availability of the service or of other tenants — for example abusive request volumes or attempts to reach other tenants&apos; data. Where circumstances permit, notice is given first.</p>
            <p><strong>Termination.</strong> You may stop using the service and offboard at any time. For the Community plan, the operator may discontinue the service or an individual tenant&apos;s access; reasonable advance notice will be given except where security requires immediate action. Enterprise termination follows its agreement.</p>
            <p><strong>After offboarding.</strong> Your tenant&apos;s data is removed through a verified multi-phase cascade. Product feedback you submitted, and custom rules and IME log patterns you authored, are intentionally retained — neither contains enrollment telemetry or personal data. Both are removed on request.</p>
          </div>
        </div>

        <div className="bg-white rounded-lg shadow p-6 space-y-4">
          <h2 className="text-xl font-semibold text-gray-900">Security and Transparency</h2>
          <p className="text-gray-700">
            The security architecture, data residency, external services, retention and deletion behaviour, and an explicit
            statement of what the service does <em>not</em> do are published in the{" "}
            <a href={DOCS_SECURITY_FAQ} target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">Security &amp; Privacy FAQ</a>.
            A signed data processing agreement is part of the Enterprise plan.
          </p>
        </div>

        <div className="bg-white rounded-lg shadow p-6 space-y-4">
          <h2 className="text-xl font-semibold text-gray-900">Third-Party Data Sources &amp; Attributions</h2>
          <p className="text-gray-700">
            The vulnerability correlation feature uses the following external data sources to identify known
            vulnerabilities in installed software. These are inbound reference-data pulls — no customer data is sent to
            them.
          </p>
          <div className="space-y-3">
            <div className="p-4 border border-gray-200 rounded-lg">
              <h3 className="font-semibold text-gray-900 mb-1">National Vulnerability Database (NVD)</h3>
              <p className="text-sm text-gray-700 mb-2">
                This product uses the NVD API but is not endorsed or certified by the NVD.
              </p>
              <p className="text-sm text-gray-500">
                The NVD is maintained by the National Institute of Standards and Technology (NIST). CVE and CPE data is sourced from the NVD API 2.0. For more information, visit{" "}
                <a href="https://nvd.nist.gov/" target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">nvd.nist.gov</a>.
              </p>
            </div>
            <div className="p-4 border border-gray-200 rounded-lg">
              <h3 className="font-semibold text-gray-900 mb-1">CISA Known Exploited Vulnerabilities (KEV) Catalog</h3>
              <p className="text-sm text-gray-500">
                Actively exploited vulnerability data is sourced from the CISA KEV Catalog maintained by the Cybersecurity and Infrastructure Security Agency. For more information, visit{" "}
                <a href="https://www.cisa.gov/known-exploited-vulnerabilities-catalog" target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">cisa.gov</a>.
              </p>
            </div>
            <div className="p-4 border border-gray-200 rounded-lg">
              <h3 className="font-semibold text-gray-900 mb-1">Microsoft Security Response Center (MSRC)</h3>
              <p className="text-sm text-gray-500">
                Microsoft-specific vulnerability data is sourced from the MSRC Security Update Guide API. For more information, visit{" "}
                <a href="https://msrc.microsoft.com/" target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">msrc.microsoft.com</a>.
              </p>
            </div>
          </div>
        </div>

        <div className="bg-white rounded-lg shadow p-6 space-y-4">
          <h2 className="text-xl font-semibold text-gray-900">Changes and Governing Law</h2>
          <div className="space-y-2 text-gray-700">
            <p><strong>Changes.</strong> These terms may be updated; the &quot;last updated&quot; date above reflects the current version, and material changes are announced through the service announcements in the portal. Continued use after a change constitutes acceptance.</p>
            <p><strong>Governing law.</strong> These terms are governed by German law. For the Enterprise plan, the governing-law and venue provisions of the written agreement apply.</p>
            <p><strong>Severability.</strong> If a provision is found unenforceable, the remaining provisions stay in effect.</p>
            <p><strong>Contact.</strong> Company details are in the <a href="https://www.glueckkanja.com/en/imprint" target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">Imprint</a>. For the project and the Community edition, reach the maintainer via <a href="https://www.linkedin.com/in/oliver-kieselbach" target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">LinkedIn</a> or open a <a href="https://github.com/okieselbach/Autopilot-Monitor/issues" target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">GitHub issue</a>.</p>
          </div>
        </div>
      </main>
    </div>
  );
}
