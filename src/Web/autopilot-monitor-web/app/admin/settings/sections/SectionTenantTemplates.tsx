"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { useNotifications } from "@/contexts/NotificationContext";
import { useTenantList } from "@/hooks/useTenantList";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { api } from "@/lib/api";

/** One UPN assigned to a template (camelCase JSON from the backend). */
interface TemplateAssignee {
  upn: string;
  role: string; // "DelegatedReader" | "DelegatedAdmin"
  isEnabled: boolean;
}

/** A tenant template as returned by /api/global/tenant-templates. */
interface TenantTemplate {
  templateId: string;
  name: string;
  createdBy: string;
  createdAt: string;
  tenantIds: string[];
  assigneeCount: number;
  assignees: TemplateAssignee[];
}

const ROLE_LABELS: Record<string, string> = {
  DelegatedReader: "Reader (read-only)",
  DelegatedAdmin: "Admin (read + write)",
};

/**
 * GlobalAdmin-only management of Tenant Templates — app-internal named bundles of tenants for the
 * delegated-admin ("MSP mode") tier. Assign a person to a template instead of to each tenant; adding a tenant
 * to the template grants it to every assignee at once. Writes go through /api/global/tenant-templates
 * (GlobalAdminOnly); the backend invalidates affected scopes so changes take effect on the next request.
 */
export function SectionTenantTemplates() {
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();
  const tenants = useTenantList(true);

  const [templates, setTemplates] = useState<TenantTemplate[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  const [newName, setNewName] = useState("");
  const [creating, setCreating] = useState(false);
  const [busyKey, setBusyKey] = useState<string | null>(null);

  // Per-template input state (keyed by templateId).
  const [tenantToAdd, setTenantToAdd] = useState<Record<string, string>>({});
  const [assignUpn, setAssignUpn] = useState<Record<string, string>>({});
  const [assignRole, setAssignRole] = useState<Record<string, string>>({});

  const domainOf = useMemo(() => {
    const map = new Map(tenants.map((t) => [t.tenantId.toLowerCase(), t.domainName]));
    return (tenantId: string) => map.get(tenantId.toLowerCase()) || "";
  }, [tenants]);

  const flash = (msg: string) => {
    setSuccessMessage(msg);
    setTimeout(() => setSuccessMessage(null), 3000);
  };

  const handleError = (err: unknown, fallback: string) => {
    if (err instanceof TokenExpiredError) {
      addNotification("error", "Session Expired", err.message, "session-expired-error");
    } else {
      setError(err instanceof Error ? err.message : fallback);
    }
  };

  const fetchTemplates = useCallback(async () => {
    try {
      setLoading(true);
      const response = await authenticatedFetch(api.tenantTemplates.list(), getAccessToken);
      if (!response.ok) throw new Error(`Failed to load groups: ${response.statusText}`);
      const data = await response.json();
      setTemplates(data.templates ?? []);
    } catch (err) {
      handleError(err, "Failed to load groups");
    } finally {
      setLoading(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [getAccessToken]);

  useEffect(() => {
    fetchTemplates();
  }, [fetchTemplates]);

  /** Runs a mutation with shared busy/error/refetch handling. `body` undefined ⇒ no JSON body. */
  const mutate = useCallback(
    async (key: string, url: string, method: string, body: unknown, ok: string) => {
      try {
        setBusyKey(key);
        setError(null);
        const init: RequestInit = { method };
        if (body !== undefined) {
          init.headers = { "Content-Type": "application/json" };
          init.body = JSON.stringify(body);
        }
        const response = await authenticatedFetch(url, getAccessToken, init);
        if (!response.ok) {
          const data = await response.json().catch(() => ({}));
          throw new Error(data.error || `Request failed: ${response.statusText}`);
        }
        flash(ok);
        await fetchTemplates();
        return true;
      } catch (err) {
        handleError(err, "Request failed");
        return false;
      } finally {
        setBusyKey(null);
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [getAccessToken, fetchTemplates],
  );

  const handleCreate = useCallback(async () => {
    const name = newName.trim();
    if (!name) return;
    setCreating(true);
    const ok = await mutate("create", api.tenantTemplates.create(), "POST", { name }, `Created group "${name}".`);
    setCreating(false);
    if (ok) setNewName("");
  }, [newName, mutate]);

  const handleRename = useCallback(
    async (t: TenantTemplate) => {
      const name = prompt("Rename group:", t.name)?.trim();
      if (!name || name === t.name) return;
      await mutate(`rename:${t.templateId}`, api.tenantTemplates.rename(t.templateId), "PATCH", { name }, `Renamed to "${name}".`);
    },
    [mutate],
  );

  const handleDeleteTemplate = useCallback(
    async (t: TenantTemplate) => {
      const extra = t.assigneeCount > 0 ? ` ${t.assigneeCount} assignee(s) will lose this access.` : "";
      if (!confirm(`Delete group "${t.name}"?${extra}`)) return;
      await mutate(`delete:${t.templateId}`, api.tenantTemplates.remove(t.templateId), "DELETE", undefined, `Deleted "${t.name}".`);
    },
    [mutate],
  );

  const handleAddTenant = useCallback(
    async (t: TenantTemplate) => {
      const tenantId = tenantToAdd[t.templateId];
      if (!tenantId) return;
      const ok = await mutate(
        `addtenant:${t.templateId}`,
        api.tenantTemplates.addTenant(t.templateId),
        "POST",
        { tenantId },
        `Added ${domainOf(tenantId) || tenantId} to "${t.name}".`,
      );
      if (ok) setTenantToAdd((prev) => ({ ...prev, [t.templateId]: "" }));
    },
    [tenantToAdd, mutate, domainOf],
  );

  const handleRemoveTenant = useCallback(
    async (t: TenantTemplate, tenantId: string) => {
      const dom = domainOf(tenantId) || tenantId;
      if (!confirm(`Remove ${dom} from "${t.name}"? Assignees lose access to this tenant.`)) return;
      await mutate(
        `rmtenant:${t.templateId}:${tenantId}`,
        api.tenantTemplates.removeTenant(t.templateId, tenantId),
        "DELETE",
        undefined,
        `Removed ${dom} from "${t.name}".`,
      );
    },
    [mutate, domainOf],
  );

  const handleAssign = useCallback(
    async (t: TenantTemplate) => {
      const upn = (assignUpn[t.templateId] || "").trim();
      if (!upn) return;
      const role = assignRole[t.templateId] || "DelegatedReader";
      const ok = await mutate(
        `assign:${t.templateId}`,
        api.tenantTemplates.assign(t.templateId),
        "POST",
        { upn, role },
        `Assigned ${upn} to "${t.name}".`,
      );
      if (ok) setAssignUpn((prev) => ({ ...prev, [t.templateId]: "" }));
    },
    [assignUpn, assignRole, mutate],
  );

  const handleUnassign = useCallback(
    async (t: TenantTemplate, upn: string) => {
      if (!confirm(`Remove ${upn} from "${t.name}"? They lose access to all tenants in this group.`)) return;
      await mutate(
        `unassign:${t.templateId}:${upn}`,
        api.tenantTemplates.unassign(t.templateId, upn),
        "DELETE",
        undefined,
        `Removed ${upn} from "${t.name}".`,
      );
    },
    [mutate],
  );

  const sorted = useMemo(
    () => [...templates].sort((a, b) => a.name.localeCompare(b.name)),
    [templates],
  );

  return (
    <div className="bg-white rounded-lg shadow">
      {/* Header */}
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-sky-50 to-indigo-50">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-sky-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
              d="M19 11H5m14 0a2 2 0 012 2v6a2 2 0 01-2 2H5a2 2 0 01-2-2v-6a2 2 0 012-2m14 0V9a2 2 0 00-2-2M5 11V9a2 2 0 012-2m0 0V5a2 2 0 012-2h6a2 2 0 012 2v2M7 7h10" />
          </svg>
          <h2 className="text-lg font-semibold text-gray-900">Tenant Groups (MSP Mode)</h2>
        </div>
        <p className="mt-1 text-sm text-gray-600">
          Bundle tenants into a named group, then assign people to the group instead of to each tenant.
          Add a tenant once and every assignee gets it; manage your managed-service team by who is assigned here.
        </p>
      </div>

      <div className="p-6 space-y-4">
        {/* Notifications */}
        {successMessage && (
          <div className="bg-green-50 border border-green-200 rounded-lg p-4 flex items-center space-x-3">
            <svg className="w-5 h-5 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
            </svg>
            <span className="text-green-800 font-medium">{successMessage}</span>
          </div>
        )}
        {error && (
          <div className="bg-red-50 border border-red-200 rounded-lg p-4 flex items-center space-x-3">
            <svg className="w-5 h-5 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
            <span className="text-red-800">{error}</span>
          </div>
        )}

        {/* Create form */}
        <div className="bg-sky-50 border border-sky-200 rounded-lg p-4 space-y-3">
          <p className="text-sm font-medium text-sky-800">New group</p>
          <div className="flex flex-col gap-2 sm:flex-row">
            <input
              type="text"
              value={newName}
              onChange={(e) => setNewName(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  e.preventDefault();
                  handleCreate();
                }
              }}
              placeholder="e.g. Managed Service Tenants"
              autoComplete="off"
              className="flex-1 min-w-0 px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-sky-500 focus:border-sky-500 transition-colors"
            />
            <button
              onClick={handleCreate}
              disabled={creating || !newName.trim()}
              className="px-6 py-2 bg-sky-600 text-white rounded-lg hover:bg-sky-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center justify-center space-x-2"
            >
              {creating ? (
                <>
                  <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" />
                  <span>Creating…</span>
                </>
              ) : (
                <span>Create</span>
              )}
            </button>
          </div>
        </div>

        {/* Loading */}
        {loading && (
          <div className="flex items-center justify-center py-8">
            <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-sky-600" />
            <span className="ml-3 text-sm text-gray-500">Loading groups…</span>
          </div>
        )}

        {/* Empty */}
        {!loading && sorted.length === 0 && (
          <p className="text-sm text-gray-500 py-4 text-center">
            No groups yet. Create one above, add tenants to it, then assign your team.
          </p>
        )}

        {/* Templates */}
        {!loading && sorted.length > 0 && (
          <div className="space-y-4">
            {sorted.map((t) => {
              const availableTenants = tenants.filter(
                (x) => !t.tenantIds.some((id) => id.toLowerCase() === x.tenantId.toLowerCase()),
              );
              return (
                <div key={t.templateId} className="border border-gray-200 rounded-lg">
                  {/* Template header */}
                  <div className="px-4 py-2 border-b border-gray-100 bg-gray-50 rounded-t-lg flex items-center justify-between gap-2">
                    <div className="min-w-0">
                      <span className="font-medium text-gray-900 truncate">{t.name}</span>
                      <span className="ml-2 text-xs text-gray-500">
                        {t.tenantIds.length} tenant{t.tenantIds.length === 1 ? "" : "s"} · {t.assigneeCount} assignee
                        {t.assigneeCount === 1 ? "" : "s"}
                      </span>
                    </div>
                    <div className="flex items-center gap-2 shrink-0">
                      <button
                        onClick={() => handleRename(t)}
                        className="px-3 py-1 text-sm border border-gray-300 rounded-lg hover:bg-gray-100 transition-colors"
                      >
                        Rename
                      </button>
                      <button
                        onClick={() => handleDeleteTemplate(t)}
                        className="px-3 py-1 text-sm text-red-600 border border-red-200 rounded-lg hover:bg-red-50 transition-colors"
                      >
                        Delete
                      </button>
                    </div>
                  </div>

                  <div className="p-4 grid gap-4 md:grid-cols-2">
                    {/* Tenants column */}
                    <div className="space-y-2">
                      <p className="text-xs font-semibold text-gray-700 uppercase tracking-wide">Tenants</p>
                      {t.tenantIds.length === 0 ? (
                        <p className="text-sm text-gray-400">No tenants yet.</p>
                      ) : (
                        <ul className="space-y-1">
                          {t.tenantIds.map((id) => (
                            <li key={id} className="flex items-center justify-between gap-2 text-sm">
                              <span className="truncate text-gray-900">{domainOf(id) || id}</span>
                              <button
                                onClick={() => handleRemoveTenant(t, id)}
                                disabled={busyKey === `rmtenant:${t.templateId}:${id}`}
                                className="text-red-600 hover:text-red-800 disabled:opacity-50 text-xs"
                                aria-label={`Remove ${domainOf(id) || id}`}
                              >
                                Remove
                              </button>
                            </li>
                          ))}
                        </ul>
                      )}
                      <div className="flex gap-2 pt-1">
                        <select
                          value={tenantToAdd[t.templateId] || ""}
                          onChange={(e) => setTenantToAdd((p) => ({ ...p, [t.templateId]: e.target.value }))}
                          className="flex-1 min-w-0 px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-sky-500 transition-colors"
                        >
                          <option value="">Add tenant…</option>
                          {availableTenants.map((x) => (
                            <option key={x.tenantId} value={x.tenantId}>
                              {x.domainName || x.tenantId}
                            </option>
                          ))}
                        </select>
                        <button
                          onClick={() => handleAddTenant(t)}
                          disabled={!tenantToAdd[t.templateId] || busyKey === `addtenant:${t.templateId}`}
                          className="px-3 py-1.5 text-sm bg-sky-600 text-white rounded-lg hover:bg-sky-700 disabled:opacity-50 transition-colors"
                        >
                          Add
                        </button>
                      </div>
                    </div>

                    {/* Assignees column */}
                    <div className="space-y-2">
                      <p className="text-xs font-semibold text-gray-700 uppercase tracking-wide">Assignees</p>
                      {t.assignees.length === 0 ? (
                        <p className="text-sm text-gray-400">No one assigned yet.</p>
                      ) : (
                        <ul className="space-y-1">
                          {t.assignees.map((a) => (
                            <li key={a.upn} className="flex items-center justify-between gap-2 text-sm">
                              <span className="min-w-0 truncate text-gray-900">
                                {a.upn}
                                <span className="ml-1.5 inline-flex items-center px-1.5 py-0.5 rounded text-xs font-medium bg-sky-100 text-sky-800">
                                  {ROLE_LABELS[a.role] ?? a.role}
                                </span>
                              </span>
                              <button
                                onClick={() => handleUnassign(t, a.upn)}
                                disabled={busyKey === `unassign:${t.templateId}:${a.upn}`}
                                className="text-red-600 hover:text-red-800 disabled:opacity-50 text-xs shrink-0"
                                aria-label={`Remove ${a.upn}`}
                              >
                                Remove
                              </button>
                            </li>
                          ))}
                        </ul>
                      )}
                      <div className="flex gap-2 pt-1">
                        <input
                          type="email"
                          value={assignUpn[t.templateId] || ""}
                          onChange={(e) => setAssignUpn((p) => ({ ...p, [t.templateId]: e.target.value }))}
                          onKeyDown={(e) => {
                            if (e.key === "Enter") {
                              e.preventDefault();
                              handleAssign(t);
                            }
                          }}
                          placeholder="user@domain.com"
                          autoComplete="off"
                          className="flex-1 min-w-0 px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-sky-500 transition-colors"
                        />
                        <select
                          value={assignRole[t.templateId] || "DelegatedReader"}
                          onChange={(e) => setAssignRole((p) => ({ ...p, [t.templateId]: e.target.value }))}
                          className="px-2 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-sky-500 transition-colors"
                        >
                          <option value="DelegatedReader">Reader</option>
                          <option value="DelegatedAdmin">Admin</option>
                        </select>
                        <button
                          onClick={() => handleAssign(t)}
                          disabled={!(assignUpn[t.templateId] || "").trim() || busyKey === `assign:${t.templateId}`}
                          className="px-3 py-1.5 text-sm bg-sky-600 text-white rounded-lg hover:bg-sky-700 disabled:opacity-50 transition-colors"
                        >
                          Assign
                        </button>
                      </div>
                    </div>
                  </div>
                </div>
              );
            })}
          </div>
        )}

        <p className="text-xs text-gray-500">
          Reader is read-only (secrets redacted). Only onboarded tenants appear in the dropdown. Changes take
          effect on the assignee&rsquo;s next request.
        </p>
      </div>
    </div>
  );
}
