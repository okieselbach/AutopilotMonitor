"use client";

import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../../../../contexts/AuthContext";
import { useTenant } from "../../../../contexts/TenantContext";
import { api } from "@/lib/api";
import { AGENT_DOWNLOAD_URL } from "@/utils/config";
import { authenticatedFetch } from "@/lib/authenticatedFetch";
import { trackEvent } from "@/lib/appInsights";

interface FeatureStatus {
  name: string;
  /** null when the backend couldn't determine permission state (transient/budget exhausted). */
  granted: boolean | null;
  requiredPermissions: string[];
}

interface StatusResponse {
  clientId: string;
  /** True when the underlying token acquire timed out or hit a retryable failure. */
  isTransient?: boolean;
  grantedRoles: string[];
  features: FeatureStatus[];
}

export function SectionOptionalGraphCapabilities() {
  const { getAccessToken, user } = useAuth();
  const { tenantId } = useTenant();

  const [status, setStatus] = useState<StatusResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState<"command" | "permissions" | null>(null);

  const fetchStatus = useCallback(async () => {
    if (!tenantId) return;
    setLoading(true);
    setError(null);
    try {
      const response = await authenticatedFetch(api.graphPermissions.status(tenantId), getAccessToken);
      if (!response.ok) {
        setError(`Failed to load status (HTTP ${response.status}).`);
        setStatus(null);
        return;
      }
      const body = (await response.json()) as StatusResponse;
      setStatus(body);
      // Tracks distinct admins/tenants looking at the capability page. The
      // backend's GraphAddOnStatusChecked event covers the same signal server-side
      // (more authoritative), but the client-side flavour also captures admins who
      // bounce off the page before the round-trip lands.
      trackEvent("GraphAddOnStatusViewed", {
        isTransient: !!body.isTransient,
        scriptDisplayNamesGranted: !!body.features.find(f => f.name === "ScriptDisplayNames")?.granted,
        grantedRoleCount: body.grantedRoles?.length ?? 0,
      });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load status.");
      setStatus(null);
    } finally {
      setLoading(false);
    }
  }, [tenantId, getAccessToken]);

  useEffect(() => {
    const run = async () => {
      await fetchStatus();
    };
    void run();
  }, [fetchStatus]);

  const onRefresh = useCallback(async () => {
    if (!tenantId) return;
    setRefreshing(true);
    setError(null);
    try {
      // Inspect the response — without the !res.ok guard a 403 or 500 would silently
      // fall through to fetchStatus() and pretend the cache invalidation worked.
      const res = await authenticatedFetch(
        api.graphPermissions.refresh(tenantId),
        getAccessToken,
        { method: "POST" },
      );
      if (!res.ok) {
        throw new Error(`Refresh failed (HTTP ${res.status}).`);
      }
      await fetchStatus();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Refresh failed.");
    } finally {
      setRefreshing(false);
    }
  }, [tenantId, getAccessToken, fetchStatus]);

  const copy = useCallback(async (text: string, label: "command" | "permissions") => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(label);
      window.setTimeout(() => setCopied(null), 2000);
      // High-intent signal: admin is about to run (or share) the grant command.
      trackEvent("GraphAddOnGrantCommandCopied", { kind: label });
    } catch {
      // Older browsers / iframes — best effort only.
    }
  }, []);

  if (!user?.isTenantAdmin && !user?.isGlobalAdmin) {
    return (
      <div className="bg-amber-50 border border-amber-200 rounded-lg p-4 text-sm text-amber-800">
        This page is available to tenant administrators only.
      </div>
    );
  }

  const featureRows = status?.features ?? [];
  const allPermissions = Array.from(new Set(featureRows.flatMap(f => f.requiredPermissions)));
  const allPermissionsLine = allPermissions.map(p => `"${p}"`).join(",");

  const scriptDownloadUrl = `${AGENT_DOWNLOAD_URL}/Grant-AutopilotMonitorAddOn.ps1`;
  const psCommand = status?.clientId
    ? `irm '${scriptDownloadUrl}' -OutFile .\\Grant-AutopilotMonitorAddOn.ps1\n.\\Grant-AutopilotMonitorAddOn.ps1 \`\n    -ClientId "${status.clientId}" \`\n    -Permissions ${allPermissionsLine} \`\n    -TenantId "${tenantId ?? "<your-tenant-id>"}"`
    : "";

  return (
    <div className="space-y-6">
      <div className="bg-white shadow rounded-lg p-6">
        <header className="mb-4">
          <h2 className="text-lg font-semibold text-gray-900">Optional Graph capabilities</h2>
          <p className="mt-1 text-sm text-gray-600">
            Autopilot Monitor&apos;s default Microsoft Graph permissions are intentionally minimal. Optional features
            (like resolving Intune Platform Script and Remediation display names in session timelines) require
            additional permissions on the Autopilot Monitor service principal in your tenant. Grant them at any
            time by running the small <span className="font-mono">Grant-AutopilotMonitorAddOn.ps1</span>{" "}script
            with a Global Administrator (or Privileged Role Administrator) sign-in — the script does not change
            the published app manifest, only your tenant&apos;s local grant.
          </p>
        </header>

        {error && (
          <div className="mb-3 bg-red-50 border border-red-200 rounded-lg p-3 text-sm text-red-800">
            {error}
          </div>
        )}

        {loading ? (
          <div className="text-sm text-gray-500">Loading capability status …</div>
        ) : !status ? (
          <div className="text-sm text-gray-500">No status available.</div>
        ) : (
          <>
            {status.isTransient && (
              <div className="mb-3 bg-amber-50 border border-amber-200 rounded-lg p-3 text-sm text-amber-800">
                Could not determine the permission state right now (transient error or
                token-acquire timeout). Try the refresh button again in a moment.
              </div>
            )}
            <div className="border border-gray-200 rounded-lg overflow-hidden">
              <table className="min-w-full divide-y divide-gray-200 text-sm">
                <thead className="bg-gray-50">
                  <tr>
                    <th scope="col" className="px-4 py-2 text-left font-medium text-gray-700">Feature</th>
                    <th scope="col" className="px-4 py-2 text-left font-medium text-gray-700">Required Graph permissions</th>
                    <th scope="col" className="px-4 py-2 text-left font-medium text-gray-700">Status</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {featureRows.map((f) => (
                    <tr key={f.name}>
                      <td className="px-4 py-2 font-medium text-gray-900">{f.name}</td>
                      <td className="px-4 py-2 font-mono text-xs text-gray-700">
                        {f.requiredPermissions.join(", ")}
                      </td>
                      <td className="px-4 py-2">
                        {f.granted === null ? (
                          <span className="inline-flex items-center px-2 py-0.5 rounded-full bg-amber-100 text-amber-800 text-xs font-medium">
                            unknown
                          </span>
                        ) : f.granted ? (
                          <span className="inline-flex items-center px-2 py-0.5 rounded-full bg-green-100 text-green-700 text-xs font-medium">
                            granted
                          </span>
                        ) : (
                          <span className="inline-flex items-center px-2 py-0.5 rounded-full bg-gray-100 text-gray-700 text-xs font-medium">
                            not granted
                          </span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            <div className="mt-4 flex flex-wrap items-center gap-3">
              <button
                onClick={onRefresh}
                disabled={refreshing}
                className="px-3 py-1.5 text-sm rounded-md bg-green-600 text-white font-medium hover:bg-green-700 disabled:opacity-50"
              >
                {refreshing ? "Refreshing …" : "Refresh permission status"}
              </button>
              <span className="text-xs text-gray-500">
                Backends cache the Graph token for up to ~1 h. Hit refresh right after running the grant script.
              </span>
            </div>
          </>
        )}
      </div>

      {status && status.clientId && (
        <div className="bg-white shadow rounded-lg p-6">
          <h3 className="text-md font-semibold text-gray-900 mb-2">PowerShell grant command</h3>
          <p className="text-sm text-gray-600 mb-3">
            Open a Windows PowerShell or PowerShell 7 prompt as a tenant administrator and run the commands
            below — the first line downloads the script (
            <a
              href={scriptDownloadUrl}
              className="text-green-700 underline hover:text-green-800"
              download
            >
              direct download
            </a>
            ), the second runs it. It is idempotent — re-running it skips permissions that are already granted.
          </p>

          <pre className="bg-gray-900 text-gray-100 text-xs font-mono p-3 rounded overflow-x-auto">
{psCommand}
          </pre>

          <div className="mt-3 flex flex-wrap gap-2">
            <button
              onClick={() => copy(psCommand, "command")}
              className="px-3 py-1.5 text-sm rounded-md bg-gray-100 text-gray-800 font-medium hover:bg-gray-200"
            >
              {copied === "command" ? "Copied!" : "Copy command"}
            </button>
            {allPermissionsLine.length > 0 && (
              <button
                onClick={() => copy(allPermissionsLine, "permissions")}
                className="px-3 py-1.5 text-sm rounded-md bg-gray-100 text-gray-800 font-medium hover:bg-gray-200"
                title="Copy just the -Permissions value (handy if you keep your own runbook)"
              >
                {copied === "permissions" ? "Copied!" : "Copy permissions"}
              </button>
            )}
          </div>

          <details className="mt-4 text-sm text-gray-600">
            <summary className="cursor-pointer text-gray-700 font-medium">What this script does (no surprises)</summary>
            <ul className="mt-2 list-disc list-inside space-y-1">
              <li>Signs in to Microsoft Graph with delegated permissions <span className="font-mono">AppRoleAssignment.ReadWrite.All</span> + <span className="font-mono">Application.Read.All</span>.</li>
              <li>Finds the Autopilot Monitor service principal in your tenant (by ClientId above).</li>
              <li>Adds an <span className="font-mono">appRoleAssignment</span> for each listed Graph permission, only if missing.</li>
              <li>Never touches the published app manifest. Revoke at any time via <span className="font-mono">-Revoke</span>.</li>
            </ul>
          </details>
        </div>
      )}
    </div>
  );
}
