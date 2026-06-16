"use client";

export function SectionMcpIntegration() {
  return (
    <section className="space-y-10">
      {/* Header */}
      <div className="bg-white rounded-lg shadow-md p-8">
        <div className="flex items-center space-x-3 mb-4">
          <svg className="w-8 h-8 text-blue-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="m6.75 7.5 3 2.25-3 2.25m4.5 0h3m-9 8.25h13.5A2.25 2.25 0 0 0 21 18V6a2.25 2.25 0 0 0-2.25-2.25H5.25A2.25 2.25 0 0 0 3 6v12a2.25 2.25 0 0 0 2.25 2.25Z" />
          </svg>
          <h2 className="text-2xl font-bold text-gray-900">MCP Integration</h2>
        </div>
        <p className="text-gray-700 leading-relaxed mb-6">
          Autopilot Monitor exposes a <strong>Model Context Protocol (MCP)</strong> server that lets AI assistants
          query enrollment data, analyze sessions, and search across your fleet using natural language.
          Connect your preferred MCP client &mdash; such as Claude Desktop, VS Code with Claude, or any
          MCP-compatible tool &mdash; and interact with your enrollment data conversationally.
        </p>
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
          <div className="bg-blue-50 rounded-lg p-4">
            <p className="font-semibold text-blue-900 mb-1">Natural Language Queries</p>
            <p className="text-sm text-blue-800">Ask questions about enrollments, failures, and device health in plain language.</p>
          </div>
          <div className="bg-indigo-50 rounded-lg p-4">
            <p className="font-semibold text-indigo-900 mb-1">22 Specialized Tools</p>
            <p className="text-sm text-indigo-800">Search sessions, analyze failures, query metrics, and explore device properties.</p>
          </div>
          <div className="bg-violet-50 rounded-lg p-4">
            <p className="font-semibold text-violet-900 mb-1">Secure &amp; Scoped</p>
            <p className="text-sm text-violet-800">Uses your existing auth token &mdash; data access is scoped to your tenant.</p>
          </div>
        </div>
      </div>

      {/* Architecture */}
      <div className="bg-white rounded-lg shadow-md p-8">
        <h3 className="text-xl font-semibold text-gray-900 mb-4">How It Works</h3>
        <p className="text-gray-700 leading-relaxed mb-4">
          The MCP server acts as a bridge between your AI assistant and the Autopilot Monitor backend.
          Your existing authentication token is passed through &mdash; the MCP server never stores credentials.
        </p>
        <div className="bg-gray-50 rounded-lg p-6 font-mono text-sm text-gray-700">
          <div className="flex items-center justify-center gap-3 flex-wrap">
            <span className="bg-blue-100 text-blue-800 px-3 py-1.5 rounded-md">AI Client</span>
            <span className="text-gray-400">&rarr;</span>
            <span className="bg-indigo-100 text-indigo-800 px-3 py-1.5 rounded-md">MCP Server</span>
            <span className="text-gray-400">&rarr;</span>
            <span className="bg-violet-100 text-violet-800 px-3 py-1.5 rounded-md">Backend API</span>
            <span className="text-gray-400">&rarr;</span>
            <span className="bg-gray-200 text-gray-800 px-3 py-1.5 rounded-md">Your Data</span>
          </div>
          <p className="text-center text-xs text-gray-500 mt-3">Bearer token is forwarded with every request &mdash; no separate service credentials needed</p>
        </div>
      </div>

      {/* Prerequisites */}
      <div className="bg-white rounded-lg shadow-md p-8">
        <h3 className="text-xl font-semibold text-gray-900 mb-4">Prerequisites</h3>
        <ul className="space-y-3">
          <li className="flex items-start gap-3">
            <span className="mt-0.5 flex-shrink-0 w-6 h-6 bg-blue-100 text-blue-700 rounded-full flex items-center justify-center text-sm font-medium">1</span>
            <div>
              <p className="font-medium text-gray-900">Autopilot Monitor account with MCP access</p>
              <p className="text-sm text-gray-600">Your administrator must enable MCP for your account. MCP access is managed under Settings &rarr; MCP Users.</p>
            </div>
          </li>
          <li className="flex items-start gap-3">
            <span className="mt-0.5 flex-shrink-0 w-6 h-6 bg-blue-100 text-blue-700 rounded-full flex items-center justify-center text-sm font-medium">2</span>
            <div>
              <p className="font-medium text-gray-900">An MCP-compatible AI client</p>
              <p className="text-sm text-gray-600">Claude Desktop, VS Code with Claude extension, or any client supporting Streamable HTTP transport.</p>
            </div>
          </li>
        </ul>
      </div>

      {/* Client Configuration */}
      <div className="bg-white rounded-lg shadow-md p-8">
        <h3 className="text-xl font-semibold text-gray-900 mb-4">Client Configuration</h3>
        <p className="text-gray-700 leading-relaxed mb-4">
          The MCP server uses <strong>Streamable HTTP</strong> transport with OAuth Bearer token authentication.
          Add the following configuration to your MCP client:
        </p>

        <div className="space-y-6">
          {/* Server URL */}
          <div>
            <h4 className="text-sm font-semibold text-gray-700 mb-2">Server URL</h4>
            <div className="bg-gray-900 rounded-lg p-4 font-mono text-sm text-green-400 overflow-x-auto">
              https://autopilotmonitor-mcp.kindwave-58b4b547.westeurope.azurecontainerapps.io/mcp
            </div>
          </div>

          {/* Claude Desktop */}
          <div>
            <h4 className="text-sm font-semibold text-gray-700 mb-2">Claude Desktop</h4>
            <p className="text-sm text-gray-600 mb-2">
              In Claude Desktop, go to <strong>Settings &rarr; MCP Servers &rarr; Add</strong> and enter the server URL above.
              OAuth authentication will be handled automatically via the browser.
            </p>
          </div>

          {/* VS Code */}
          <div>
            <h4 className="text-sm font-semibold text-gray-700 mb-2">VS Code with Claude Extension</h4>
            <p className="text-sm text-gray-600 mb-2">
              Add the server to your <code className="bg-gray-100 px-1.5 py-0.5 rounded text-xs">.vscode/mcp.json</code> or user settings:
            </p>
            <div className="bg-gray-900 rounded-lg p-4 font-mono text-sm text-green-400 overflow-x-auto whitespace-pre">
{`{
  "servers": {
    "autopilot-monitor": {
      "type": "http",
      "url": "https://autopilotmonitor-mcp.kindwave-58b4b547.westeurope.azurecontainerapps.io/mcp"
    }
  }
}`}
            </div>
          </div>

          {/* Verification */}
          <div className="bg-blue-50 border border-blue-200 rounded-lg p-4">
            <p className="text-sm font-medium text-blue-900 mb-1">Verify your connection</p>
            <p className="text-sm text-blue-800">
              After configuring the server, ask your AI assistant: <em>&quot;List all available tools from Autopilot Monitor&quot;</em>.
              You should see 20+ tools listed. If authentication fails, ensure your MCP access has been enabled by an administrator.
            </p>
          </div>
        </div>
      </div>

      {/* Available Tools */}
      <div className="bg-white rounded-lg shadow-md p-8">
        <h3 className="text-xl font-semibold text-gray-900 mb-6">Available Tools</h3>

        {/* Search & Discovery */}
        <div className="mb-8">
          <h4 className="text-lg font-semibold text-gray-800 mb-3 flex items-center gap-2">
            <span className="w-2 h-2 rounded-full bg-blue-500" />
            Search &amp; Discovery
          </h4>
          <div className="overflow-x-auto">
            <table className="min-w-full text-sm">
              <thead>
                <tr className="border-b border-gray-200">
                  <th className="text-left py-2 pr-4 font-medium text-gray-700 w-1/3">Tool</th>
                  <th className="text-left py-2 font-medium text-gray-700">Description</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-blue-700">search_sessions</td>
                  <td className="py-2 text-gray-600">Search enrollment sessions by status, device properties, serial number, manufacturer, model, OS build, geo location, and more. Supports dynamic device property filters with dot notation.</td>
                </tr>
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-blue-700">search_sessions_by_event</td>
                  <td className="py-2 text-gray-600">Find sessions containing a specific event type (e.g. app install failure, phase transition, error). Use the <code className="bg-gray-100 px-1 rounded text-xs">event_types</code> resource for valid values.</td>
                </tr>
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-blue-700">search_sessions_by_cve</td>
                  <td className="py-2 text-gray-600">Find devices affected by a specific CVE. Returns sessions where the vulnerability was detected during enrollment.</td>
                </tr>
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-blue-700">search_events</td>
                  <td className="py-2 text-gray-600">Hybrid keyword + semantic event search. Maps a natural-language description to the right event types even with no literal word overlap (e.g. &quot;machine restarted unexpectedly&quot;). Use depth=&quot;deep&quot; for exhaustive recall.</td>
                </tr>
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-blue-700">search_knowledge</td>
                  <td className="py-2 text-gray-600">Semantic search over analysis rules, gather rules, and IME log patterns. Helps the AI understand your configured rules and diagnostics.</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>

        {/* Session Analysis */}
        <div className="mb-8">
          <h4 className="text-lg font-semibold text-gray-800 mb-3 flex items-center gap-2">
            <span className="w-2 h-2 rounded-full bg-indigo-500" />
            Session Analysis
          </h4>
          <div className="overflow-x-auto">
            <table className="min-w-full text-sm">
              <thead>
                <tr className="border-b border-gray-200">
                  <th className="text-left py-2 pr-4 font-medium text-gray-700 w-1/3">Tool</th>
                  <th className="text-left py-2 font-medium text-gray-700">Description</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-indigo-700">get_session</td>
                  <td className="py-2 text-gray-600">Full session details with all device metadata. Optionally includes AI rule analysis with failure explanations and remediation suggestions.</td>
                </tr>
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-indigo-700">get_session_events</td>
                  <td className="py-2 text-gray-600">Event timeline for a session. Filter by event type, severity, or source (app name) for focused root cause analysis.</td>
                </tr>
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-indigo-700">get_session_summary</td>
                  <td className="py-2 text-gray-600">Concise structured summary: overview, key events (noise filtered), rule analysis, and aggregate stats. Best starting point for session investigation.</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>

        {/* Metrics & Observability */}
        <div className="mb-8">
          <h4 className="text-lg font-semibold text-gray-800 mb-3 flex items-center gap-2">
            <span className="w-2 h-2 rounded-full bg-emerald-500" />
            Metrics &amp; Observability
          </h4>
          <div className="overflow-x-auto">
            <table className="min-w-full text-sm">
              <thead>
                <tr className="border-b border-gray-200">
                  <th className="text-left py-2 pr-4 font-medium text-gray-700 w-1/3">Tool</th>
                  <th className="text-left py-2 font-medium text-gray-700">Description</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-emerald-700">get_metrics</td>
                  <td className="py-2 text-gray-600">Aggregated enrollment metrics: failure rates, slowest/most-failing apps, session counts. Supports 7, 30, or 90 day windows.</td>
                </tr>
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-emerald-700">get_app_install_metrics</td>
                  <td className="py-2 text-gray-600">App-install health over a window: top failing apps with failure rates and their most common failure codes, slowest apps by average install duration, and a Delivery Optimization rollup (bytes from peers / Microsoft Connected Cache vs. CDN, plus peer-offload percentage). 1&ndash;365 day window (default 30).</td>
                </tr>
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-emerald-700">get_geographic_metrics</td>
                  <td className="py-2 text-gray-600">Geographic distribution of enrollments with performance comparisons across locations.</td>
                </tr>
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-emerald-700">get_geographic_sessions</td>
                  <td className="py-2 text-gray-600">Drill into a specific location by country, region, or city to see enrollment details.</td>
                </tr>
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-emerald-700">get_vulnerability_summary</td>
                  <td className="py-2 text-gray-600">Vulnerability exposure summary from detected CVEs: affected device count, distinct CVE and CISA Known-Exploited (KEV) counts, severity breakdown, and the top CVEs ranked by affected devices. Use <code className="bg-gray-100 px-1 rounded text-xs">search_sessions_by_cve</code> for the device list of a single CVE.</td>
                </tr>
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-emerald-700">get_rule_stats</td>
                  <td className="py-2 text-gray-600">Firing statistics for analyze and gather rules: which rules fire most often, their hit rates (fires/evaluations), and daily trends. Filter by rule type and date window to keep responses lean.</td>
                </tr>
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-emerald-700">get_ime_version_history</td>
                  <td className="py-2 text-gray-600">History of all IME (Intune Management Extension) agent versions seen across enrollments &mdash; first/last seen and session counts per version. A permanent archive that survives data retention, useful for tracking Microsoft IME rollouts.</td>
                </tr>
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-emerald-700">get_usage_metrics</td>
                  <td className="py-2 text-gray-600">Usage statistics for your tenant: session volumes, feature adoption, success rate, and active users. 1&ndash;365 day window (default 30).</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>

        {/* Device Management & Admin */}
        <div className="mb-8">
          <h4 className="text-lg font-semibold text-gray-800 mb-3 flex items-center gap-2">
            <span className="w-2 h-2 rounded-full bg-amber-500" />
            Inventory &amp; Audit
          </h4>
          <div className="overflow-x-auto">
            <table className="min-w-full text-sm">
              <thead>
                <tr className="border-b border-gray-200">
                  <th className="text-left py-2 pr-4 font-medium text-gray-700 w-1/3">Tool</th>
                  <th className="text-left py-2 font-medium text-gray-700">Description</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-amber-700">get_software_inventory</td>
                  <td className="py-2 text-gray-600">Installed-software catalog discovered on enrolled devices, deduplicated by normalized vendor/name/version with publisher, registry source, CPE mapping for vulnerability correlation, session count, and last-seen. Paginated &mdash; pass the returned <span className="font-mono text-xs">nextLink</span> back as <span className="font-mono text-xs">continuation</span>.</td>
                </tr>
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-amber-700">get_audit_logs</td>
                  <td className="py-2 text-gray-600">Audit trail of configuration changes, device blocks, and user management actions. Filterable by date window, action, actor (performedBy), entity type, and entity id; paginated.</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>

        {/* Raw Data */}
        <div>
          <h4 className="text-lg font-semibold text-gray-800 mb-3 flex items-center gap-2">
            <span className="w-2 h-2 rounded-full bg-gray-500" />
            Raw Data Queries
          </h4>
          <div className="overflow-x-auto">
            <table className="min-w-full text-sm">
              <thead>
                <tr className="border-b border-gray-200">
                  <th className="text-left py-2 pr-4 font-medium text-gray-700 w-1/3">Tool</th>
                  <th className="text-left py-2 font-medium text-gray-700">Description</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-gray-700">query_raw_events</td>
                  <td className="py-2 text-gray-600">Query raw event data with flexible filters and field projection. Pass a lean <span className="font-mono text-xs">fields=</span> list to drop the heavy per-event <span className="font-mono text-xs">data</span> payload for counting/aggregation. When querying by session ID the tenant is auto-resolved from the session. Useful for custom analysis beyond the high-level tools.</td>
                </tr>
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-gray-700">query_raw_sessions</td>
                  <td className="py-2 text-gray-600">Query raw session data with field projection. Select specific fields to reduce response size for bulk analysis.</td>
                </tr>
                <tr>
                  <td className="py-2 pr-4 font-mono text-xs text-gray-700">get_resource</td>
                  <td className="py-2 text-gray-600">Returns a named discovery catalog (<code className="bg-gray-100 px-1 rounded text-xs">event_types</code> or <code className="bg-gray-100 px-1 rounded text-xs">device_properties</code>) for clients that cannot read MCP-protocol resources directly. Consult it before filtering by event type or device property.</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
      </div>

      {/* Resources */}
      <div className="bg-white rounded-lg shadow-md p-8">
        <h3 className="text-xl font-semibold text-gray-900 mb-4">Resources</h3>
        <p className="text-gray-700 leading-relaxed mb-4">
          The MCP server exposes two discovery resources that help the AI assistant understand available data:
        </p>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <div className="border border-gray-200 rounded-lg p-4">
            <p className="font-mono text-sm text-blue-700 mb-1">event_types</p>
            <p className="text-sm text-gray-600">
              Catalog of all enrollment event type strings, organized by category (phase events, app events,
              network events, device info, errors, vulnerabilities). The AI consults this before searching by event type.
            </p>
          </div>
          <div className="border border-gray-200 rounded-lg p-4">
            <p className="font-mono text-sm text-blue-700 mb-1">device_properties</p>
            <p className="text-sm text-gray-600">
              Searchable device property keys using dot notation (e.g.{" "}
              <code className="bg-gray-100 px-1 rounded text-xs">tpm_status.specVersion</code>,{" "}
              <code className="bg-gray-100 px-1 rounded text-xs">hardware_spec.ramTotalGB</code>).
              Covers TPM, Secure Boot, BitLocker, Autopilot profile, hardware specs, network, and AAD join status.
            </p>
          </div>
        </div>
      </div>

      {/* Example Workflows */}
      <div className="bg-white rounded-lg shadow-md p-8">
        <h3 className="text-xl font-semibold text-gray-900 mb-4">Example Workflows</h3>
        <p className="text-gray-700 leading-relaxed mb-6">
          Here are some examples of what you can ask your AI assistant once connected. The AI will automatically
          select the right tools and chain multiple queries together to answer your question.
        </p>

        <div className="space-y-4">
          <ExampleWorkflow
            prompt="Show me all failed enrollments from the last 24 hours"
            description="Uses search_sessions with status=Failed and startedAfter filter. Returns device names, failure reasons, and session IDs for further investigation."
            tools={["search_sessions"]}
          />
          <ExampleWorkflow
            prompt="Which devices are affected by CVE-2024-30078?"
            description="Uses search_sessions_by_cve to find sessions where the vulnerability was detected. Shows affected device details and enrollment status."
            tools={["search_sessions_by_cve"]}
          />
          <ExampleWorkflow
            prompt="Summarize session abc-123 and suggest fixes"
            description="Uses get_session_summary for a concise overview with filtered key events, then includes rule analysis results with probable causes and remediation steps."
            tools={["get_session_summary"]}
          />
          <ExampleWorkflow
            prompt="Compare enrollment performance between Germany and the US"
            description="Uses get_geographic_metrics to retrieve per-country stats, then compares failure rates, average durations, and session counts across locations."
            tools={["get_geographic_metrics"]}
          />
          <ExampleWorkflow
            prompt="Which apps cause the most timeouts during enrollment?"
            description="Uses get_metrics to retrieve the slowest and most-failing apps, ranked by failure count and average install duration."
            tools={["get_metrics"]}
          />
          <ExampleWorkflow
            prompt="Show me the event timeline for device DESKTOP-ABC123"
            description="First uses search_sessions to find the session by device name, then get_session_events to retrieve the full event timeline with severity filtering."
            tools={["search_sessions", "get_session_events"]}
          />
          <ExampleWorkflow
            prompt="Find enrollments with BitLocker issues"
            description="Uses search_events with a natural language query to find sessions mentioning BitLocker errors, or search_sessions with deviceProperties filter on bitlocker_status."
            tools={["search_events", "search_sessions"]}
          />
          <ExampleWorkflow
            prompt="How has the failure rate changed this week?"
            description="Uses get_metrics with a 7-day window to show current failure rates, then compares with 30-day metrics to identify trends."
            tools={["get_metrics"]}
          />
        </div>
      </div>

      {/* Rate Limits */}
      <div className="bg-white rounded-lg shadow-md p-8">
        <h3 className="text-xl font-semibold text-gray-900 mb-4">Rate Limits</h3>
        <p className="text-gray-700 leading-relaxed mb-4">
          MCP requests are rate-limited per user to ensure fair usage. The limit is <strong>60 requests per minute</strong>,
          enforced as a sliding 60-second window per signed-in user.
        </p>
        <div className="bg-amber-50 border border-amber-200 rounded-lg p-4">
          <p className="text-sm text-amber-800">
            If you hit the limit, the server returns HTTP 429 with <code className="bg-amber-100 px-1 rounded text-xs">retryAfterSeconds: 60</code>.
            Your AI client will typically retry automatically once the window clears.
          </p>
        </div>
      </div>
    </section>
  );
}

function ExampleWorkflow({ prompt, description, tools }: { prompt: string; description: string; tools: string[] }) {
  return (
    <div className="border border-gray-200 rounded-lg p-4 hover:border-blue-200 transition-colors">
      <p className="font-medium text-gray-900 mb-1">&quot;{prompt}&quot;</p>
      <p className="text-sm text-gray-600 mb-2">{description}</p>
      <div className="flex flex-wrap gap-1.5">
        {tools.map((tool) => (
          <span key={tool} className="inline-flex items-center px-2 py-0.5 rounded text-xs font-mono bg-blue-50 text-blue-700 border border-blue-100">
            {tool}
          </span>
        ))}
      </div>
    </div>
  );
}
