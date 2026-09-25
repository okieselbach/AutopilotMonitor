"use client";

import { useCallback, useEffect, useState } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { useTenant } from "@/contexts/TenantContext";
import { useTenantConfig } from "../../TenantConfigContext";
import { apiErrorText, fetchJson, fetchOk, jsonBody } from "@/lib/apiClient";
import { api } from "@/lib/api";
import { SectionCardHeader } from "@/components/SectionCardHeader";
import { DOCS_PATHS } from "@/lib/docsPaths";
import { MCP_SERVER_URL } from "@/utils/config";
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

function useCopy() {
  const [copied, setCopied] = useState<string | null>(null);
  const copy = useCallback(async (value: string, key: string) => {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(key);
      setTimeout(() => setCopied(null), 2000);
    } catch {
      setCopied(null);
    }
  }, []);
  return { copied, copy };
}

/**
 * Settings → Tenant → AI Integration. Part 1 explains the normal connection (Claude, ChatGPT, VS Code and
 * other supported AI clients: add the server URL, sign in — nothing to register here). Part 2, for Tenant
 * Admins while the platform switch is on, registers the exact OAuth callback of an AI client the
 * organization hosts itself; that client then connects with client id amc_&lt;id&gt;, bound to this tenant.
 */
export function SectionAiIntegration() {
  const { user } = useAuth();
  const { canEditConfig } = useTenantConfig();
  const { copied, copy } = useCopy();
  const showSelfHosted = canEditConfig && (user?.mcpClientRegistrationEnabled ?? false);

  return (
    <div className="space-y-6">
      <div className="bg-white rounded-lg shadow">
        <SectionCardHeader
          tone="skyIndigo"
          iconPath="M8 10h.01M12 10h.01M16 10h.01M9 16H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-5l-5 5v-5z"
          title="AI Integration"
          subtitle="Ask Claude, ChatGPT, VS Code or another AI assistant about your enrollments. The assistant reads your data through the Autopilot Monitor MCP server."
          docsPath={DOCS_PATHS.mcpUsers}
        />
        <div className="p-6 space-y-3 text-sm text-gray-700">
          <p>
            <strong>Nothing to register here.</strong> Add this server URL as an MCP server (connector) in your AI client:
          </p>
          <div className="flex flex-wrap items-center gap-2">
            <code className="text-sm bg-gray-100 text-gray-800 rounded px-2 py-1">{MCP_SERVER_URL}</code>
            <button type="button" onClick={() => void copy(MCP_SERVER_URL, "server-main")} className="text-xs text-indigo-600 hover:text-indigo-800">
              {copied === "server-main" ? "Copied" : "Copy"}
            </button>
          </div>
          <p>
            The client opens the Microsoft sign-in in your browser. Sign in with your Autopilot Monitor account; the assistant
            then sees exactly what your role allows in the portal. The setup guide covers each client step by step.
          </p>
        </div>
      </div>

      {showSelfHosted && <SelfHostedClients />}
    </div>
  );
}

/** Part 2: registrations of AI clients the organization hosts itself (Tenant Admin, platform switch on). */
function SelfHostedClients() {
  const { getAccessToken } = useAuth();
  const { tenantId } = useTenant();
  const { copied, copy } = useCopy();

  const [data, setData] = useState<McpClientRegistrationListResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [flash, setFlash] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [redirectUri, setRedirectUri] = useState("");

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
    const run = async () => {
      await load();
    };
    void run();
  }, [load]);

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

  const registrations = data?.registrations ?? [];
  const atLimit = data ? registrations.length >= data.maxRegistrations : false;
  const inputClass = "px-3 py-1.5 border border-gray-300 rounded-lg text-sm bg-white text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-indigo-500";

  return (
    <div className="space-y-4">
      {flash && <div className="bg-green-50 border border-green-200 rounded-lg p-4 text-sm text-green-800">{flash}</div>}
      {error && <div className="bg-red-50 border border-red-200 rounded-lg p-4 text-sm text-red-800">{error}</div>}

      <div className="bg-white rounded-lg shadow">
        <SectionCardHeader
          tone="skyIndigo"
          iconPath="M5 12h14M5 12a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v4a2 2 0 01-2 2M5 12a2 2 0 00-2 2v4a2 2 0 002 2h14a2 2 0 002-2v-4a2 2 0 00-2-2m-2-4h.01M17 16h.01"
          title="Self-hosted AI clients"
          subtitle="Only for an AI client your organization hosts itself. Claude, ChatGPT, VS Code and other hosted assistants need nothing here."
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
              placeholder="Name, e.g. Team AI"
              maxLength={64}
              className={`${inputClass} w-48`}
              aria-label="Client name"
            />
            <input
              type="url"
              value={redirectUri}
              onChange={(e) => setRedirectUri(e.target.value)}
              placeholder="https://ai.example.com/oauth/callback"
              className={`${inputClass} flex-1 min-w-[18rem] font-mono`}
              aria-label="Callback URL"
            />
            <button
              type="button"
              onClick={() => void create()}
              disabled={busy !== null || atLimit || !data?.enabled || !name.trim() || !redirectUri.trim()}
              className="px-3 py-1.5 text-sm font-medium text-white bg-indigo-600 rounded-lg hover:bg-indigo-700 disabled:opacity-50"
            >
              {busy === "create" ? "Registering…" : "Register"}
            </button>
          </div>
          {data && (
            <p className="text-xs text-gray-500">
              {registrations.length} of {data.maxRegistrations} registration{data.maxRegistrations === 1 ? "" : "s"} used; more are available on request. The callback URL must match exactly what the client sends: https, or http only for localhost.
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
                <li>Register the exact callback URL your client uses for its OAuth sign-in. The client&apos;s MCP server settings or its documentation show it.</li>
                <li>Connecting opens the Microsoft sign-in. Users sign in with their account of this tenant; their role applies as in the portal.</li>
              </ol>
            </details>
          )}
        </div>
      </div>
    </div>
  );
}
