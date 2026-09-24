"use client";

import { useCallback, useEffect, useState } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { useTenant } from "@/contexts/TenantContext";
import { useTenantConfig } from "../../TenantConfigContext";
import { apiErrorText, fetchJson, fetchOk, jsonBody } from "@/lib/apiClient";
import { api } from "@/lib/api";
import { SectionCardHeader } from "@/components/SectionCardHeader";
import { DOCS_PATHS } from "@/lib/docsPaths";
import type {
  CreateMcpClientRegistrationRequest,
  CreateMcpClientRegistrationResponse,
  McpClientRegistrationListResponse,
} from "@/utils/wire-types.generated";

function formatDay(iso: string | undefined | null): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "—" : d.toLocaleDateString();
}

/**
 * Settings → Tenant → Self-hosted AI clients. A Tenant Admin registers the exact OAuth callback of an AI
 * client the organization runs itself; the client then connects to the MCP server with client id
 * amc_&lt;id&gt; through the normal browser sign-in, bound to this tenant. Every change is audited.
 */
export function SectionSelfHostedClients() {
  const { getAccessToken } = useAuth();
  const { tenantId } = useTenant();
  const { canEditConfig } = useTenantConfig();

  const [data, setData] = useState<McpClientRegistrationListResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [flash, setFlash] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [redirectUri, setRedirectUri] = useState("");
  const [copied, setCopied] = useState<string | null>(null);

  const notify = (msg: string) => {
    setFlash(msg);
    setTimeout(() => setFlash(null), 4000);
  };

  const load = useCallback(async () => {
    if (!tenantId) return;
    setLoading(true);
    setError(null);
    try {
      setData(await fetchJson<McpClientRegistrationListResponse>(api.tenants.mcpClientRegistrations(tenantId), getAccessToken));
    } catch (err) {
      setError(apiErrorText(err, "Failed to load self-hosted clients"));
    } finally {
      setLoading(false);
    }
  }, [tenantId, getAccessToken]);

  useEffect(() => {
    if (!canEditConfig) return;
    const run = async () => {
      await load();
    };
    void run();
  }, [load, canEditConfig]);

  const create = useCallback(async () => {
    if (!tenantId) return;
    setBusy("create");
    setError(null);
    try {
      const created = await fetchJson<CreateMcpClientRegistrationResponse>(api.tenants.mcpClientRegistrations(tenantId), getAccessToken, {
        method: "POST",
        body: jsonBody<CreateMcpClientRegistrationRequest>({ name: name.trim(), redirectUri: redirectUri.trim() }),
      });
      setName("");
      setRedirectUri("");
      notify(`${created.registration.name} registered. Configure the client with the client ID shown below.`);
      await load();
    } catch (err) {
      setError(apiErrorText(err, "Failed to register the client"));
    } finally {
      setBusy(null);
    }
  }, [tenantId, name, redirectUri, getAccessToken, load]);

  const remove = useCallback(async (registrationId: string, label: string) => {
    if (!tenantId) return;
    setBusy(registrationId);
    setError(null);
    try {
      await fetchOk(api.tenants.mcpClientRegistration(tenantId, registrationId), getAccessToken, { method: "DELETE" });
      setConfirmDelete(null);
      notify(`${label} deleted. The client can no longer sign in.`);
      await load();
    } catch (err) {
      setError(apiErrorText(err, "Failed to delete the registration"));
    } finally {
      setBusy(null);
    }
  }, [tenantId, getAccessToken, load]);

  const copy = useCallback(async (value: string, key: string) => {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(key);
      setTimeout(() => setCopied(null), 2000);
    } catch {
      setCopied(null);
    }
  }, []);

  if (!canEditConfig) {
    return (
      <div className="bg-amber-50 border border-amber-200 rounded-lg p-4 text-sm text-amber-800">
        This page is available to tenant administrators only.
      </div>
    );
  }

  const registrations = data?.registrations ?? [];
  const atCap = data ? registrations.length >= data.maxRegistrations : false;
  const inputClass = "px-3 py-1.5 border border-gray-300 rounded-lg text-sm bg-white text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-indigo-500";

  return (
    <div className="space-y-6">
      {flash && <div className="bg-green-50 border border-green-200 rounded-lg p-4 text-sm text-green-800">{flash}</div>}
      {error && <div className="bg-red-50 border border-red-200 rounded-lg p-4 text-sm text-red-800">{error}</div>}

      <div className="bg-white rounded-lg shadow">
        <SectionCardHeader
          tone="skyIndigo"
          iconPath="M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z"
          title="Self-hosted AI clients"
          subtitle="Connect an AI client your organization runs itself through the normal sign-in. Only accounts of this tenant can sign in through a registered client."
          docsPath={DOCS_PATHS.selfHostedClients}
        />
        <div className="p-6 space-y-4">
          {data && !data.enabled && (
            <p className="text-sm text-amber-800 bg-amber-50 border border-amber-200 rounded-lg px-3 py-2">
              Self-hosted client registration is currently switched off on this platform. Existing registrations do not work until it is switched on again.
            </p>
          )}

          <div className="flex flex-wrap items-center gap-2">
            <input
              type="text"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="Name, e.g. Team chat"
              maxLength={64}
              className={`${inputClass} w-44`}
              aria-label="Client name"
            />
            <input
              type="url"
              value={redirectUri}
              onChange={(e) => setRedirectUri(e.target.value)}
              placeholder="https://chat.example.com/api/mcp/autopilot-monitor/oauth/callback"
              className={`${inputClass} flex-1 min-w-[18rem] font-mono`}
              aria-label="Callback URL"
            />
            <button
              type="button"
              onClick={() => void create()}
              disabled={busy !== null || atCap || !data?.enabled || !name.trim() || !redirectUri.trim()}
              className="px-3 py-1.5 text-sm font-medium text-white bg-indigo-600 rounded-lg hover:bg-indigo-700 disabled:opacity-50"
            >
              {busy === "create" ? "Registering…" : "Register"}
            </button>
          </div>
          {data && (
            <p className="text-xs text-gray-500">
              {registrations.length} of {data.maxRegistrations} registrations used. The callback URL must match exactly what the client sends: https, or http only for localhost.
            </p>
          )}

          {loading && !data ? (
            <p className="text-sm text-gray-500">Loading…</p>
          ) : registrations.length === 0 ? (
            <p className="text-sm text-gray-500">No self-hosted client is registered.</p>
          ) : (
            <ul className="divide-y divide-gray-100">
              {registrations.map((r) => (
                <li key={r.registrationId} className="py-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm">
                  <span className="font-medium text-gray-900">{r.name}</span>
                  <span className="font-mono text-xs text-gray-600 truncate max-w-[28rem]" title={r.redirectUri}>{r.redirectUri}</span>
                  <span className="text-xs text-gray-500">
                    {r.createdBy} · {formatDay(r.createdUtc)}
                  </span>
                  <span className="ml-auto flex items-center gap-2">
                    <code className="text-xs bg-gray-100 text-gray-800 rounded px-1.5 py-0.5">{r.clientId}</code>
                    <button type="button" onClick={() => void copy(r.clientId, r.registrationId)} className="text-xs text-indigo-600 hover:text-indigo-800">
                      {copied === r.registrationId ? "Copied" : "Copy client ID"}
                    </button>
                    {confirmDelete === r.registrationId ? (
                      <>
                        <span className="text-xs text-gray-600">Delete? The client stops working.</span>
                        <button
                          type="button"
                          disabled={busy !== null}
                          onClick={() => void remove(r.registrationId, r.name)}
                          className="text-xs font-medium text-white bg-red-600 rounded px-2 py-1 hover:bg-red-700 disabled:opacity-50"
                        >
                          {busy === r.registrationId ? "Deleting…" : "Confirm"}
                        </button>
                        <button type="button" onClick={() => setConfirmDelete(null)} className="text-xs text-gray-500 hover:text-gray-700">Cancel</button>
                      </>
                    ) : (
                      <button type="button" onClick={() => setConfirmDelete(r.registrationId)} className="text-xs text-red-600 hover:text-red-800">
                        Delete
                      </button>
                    )}
                  </span>
                </li>
              ))}
            </ul>
          )}

          {data && (
            <details className="text-sm text-gray-700 bg-gray-50 border border-gray-200 rounded-lg px-4 py-3">
              <summary className="cursor-pointer font-medium text-gray-800">How to configure the client</summary>
              <ol className="mt-3 space-y-2 list-decimal list-inside">
                <li>
                  Server URL:{" "}
                  <code className="text-xs bg-white border border-gray-200 rounded px-1.5 py-0.5">{data.serverUrl}</code>{" "}
                  <button type="button" onClick={() => void copy(data.serverUrl, "server")} className="text-xs text-indigo-600 hover:text-indigo-800">
                    {copied === "server" ? "Copied" : "Copy"}
                  </button>
                </li>
                <li>Authentication: OAuth. Enter the client ID from the list above and leave the client secret empty.</li>
                <li>Leave the authorization and token URLs empty; the client discovers them from the server.</li>
                <li>
                  Register the callback URL the client really uses. LibreChat, for example, uses{" "}
                  <code className="text-xs bg-white border border-gray-200 rounded px-1.5 py-0.5">https://&lt;your LibreChat host&gt;/api/mcp/&lt;server name&gt;/oauth/callback</code>
                  , where the server name is the name you give the server in LibreChat.
                </li>
                <li>Connecting opens the Microsoft sign-in. Users sign in with their account of this tenant; their role applies as in the portal.</li>
              </ol>
            </details>
          )}
        </div>
      </div>
    </div>
  );
}
