"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { useNotifications } from "@/contexts/NotificationContext";
import { useTenantList } from "@/hooks/useTenantList";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { api } from "@/lib/api";

/** One delegated-admin assignment as returned by /api/global/delegated-admins (camelCase JSON). */
interface DelegatedAssignment {
  upn: string;
  tenantId: string;
  role: string; // "DelegatedReader" | "DelegatedAdmin"
  isEnabled: boolean;
  status: string; // "Active" | "PendingApproval" | "Revoked"
  source: string; // "OperatorGranted" | "CustomerDelegated"
  grantedAt: string;
  grantedBy: string;
}

const ROLE_LABELS: Record<string, string> = {
  DelegatedReader: "Reader (read-only)",
  DelegatedAdmin: "Admin (read + write)",
};

/**
 * GlobalAdmin-only management of delegated-admin ("MSP mode") assignments: grant a UPN read access to a
 * subset of tenants, then enable/disable/revoke. Mirrors the MCP-user management section. Writes go through
 * /api/global/delegated-admins (GlobalAdminOnly); the backend stamps source=OperatorGranted, status=Active
 * and invalidates the caller's scope cache, so changes take effect on the next request (no 5-min wait).
 */
export function SectionDelegatedAdmins() {
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();
  const tenants = useTenantList(true);

  const [assignments, setAssignments] = useState<DelegatedAssignment[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  const [newUpn, setNewUpn] = useState("");
  const [newTenantId, setNewTenantId] = useState("");
  const [newRole, setNewRole] = useState("DelegatedReader");
  const [granting, setGranting] = useState(false);
  const [busyKey, setBusyKey] = useState<string | null>(null);

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

  const fetchAssignments = useCallback(async () => {
    try {
      setLoading(true);
      const response = await authenticatedFetch(api.delegatedAdmins.list(), getAccessToken);
      if (!response.ok) throw new Error(`Failed to load delegated admins: ${response.statusText}`);
      const data = await response.json();
      setAssignments(data.assignments ?? []);
    } catch (err) {
      handleError(err, "Failed to load delegated admins");
    } finally {
      setLoading(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [getAccessToken]);

  useEffect(() => {
    fetchAssignments();
  }, [fetchAssignments]);

  const handleGrant = useCallback(async () => {
    const upn = newUpn.trim();
    if (!upn || !newTenantId) return;
    try {
      setGranting(true);
      setError(null);
      const response = await authenticatedFetch(api.delegatedAdmins.grant(), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ upn, tenantId: newTenantId, role: newRole }),
      });
      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error(data.error || `Failed to grant: ${response.statusText}`);
      }
      const dom = domainOf(newTenantId);
      flash(`Granted ${upn} ${ROLE_LABELS[newRole] ?? newRole} on ${dom || newTenantId}.`);
      setNewUpn("");
      setNewTenantId("");
      setNewRole("DelegatedReader");
      await fetchAssignments();
    } catch (err) {
      handleError(err, "Failed to grant");
    } finally {
      setGranting(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [newUpn, newTenantId, newRole, getAccessToken, fetchAssignments, domainOf]);

  const handleToggle = useCallback(async (a: DelegatedAssignment) => {
    const key = `${a.upn}|${a.tenantId}`;
    try {
      setBusyKey(key);
      setError(null);
      const url = a.isEnabled
        ? api.delegatedAdmins.disable(a.upn, a.tenantId)
        : api.delegatedAdmins.enable(a.upn, a.tenantId);
      const response = await authenticatedFetch(url, getAccessToken, { method: "PATCH" });
      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error(data.error || `Failed to update: ${response.statusText}`);
      }
      flash(`${a.upn} ${a.isEnabled ? "disabled" : "enabled"} on ${domainOf(a.tenantId) || a.tenantId}.`);
      await fetchAssignments();
    } catch (err) {
      handleError(err, "Failed to update assignment");
    } finally {
      setBusyKey(null);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [getAccessToken, fetchAssignments, domainOf]);

  const handleRevoke = useCallback(async (a: DelegatedAssignment) => {
    const dom = domainOf(a.tenantId) || a.tenantId;
    if (!confirm(`Revoke ${a.upn}'s access to ${dom}? This removes the assignment.`)) return;
    const key = `${a.upn}|${a.tenantId}`;
    try {
      setBusyKey(key);
      setError(null);
      const response = await authenticatedFetch(api.delegatedAdmins.revoke(a.upn, a.tenantId), getAccessToken, {
        method: "DELETE",
      });
      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error(data.error || `Failed to revoke: ${response.statusText}`);
      }
      flash(`Revoked ${a.upn}'s access to ${dom}.`);
      await fetchAssignments();
    } catch (err) {
      handleError(err, "Failed to revoke");
    } finally {
      setBusyKey(null);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [getAccessToken, fetchAssignments, domainOf]);

  // Group rows by delegated admin (UPN) so a multi-tenant MSP reads as one block.
  const grouped = useMemo(() => {
    const byUpn = new Map<string, DelegatedAssignment[]>();
    for (const a of assignments) {
      const list = byUpn.get(a.upn) ?? [];
      list.push(a);
      byUpn.set(a.upn, list);
    }
    return [...byUpn.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [assignments]);

  return (
    <div className="bg-white rounded-lg shadow">
      {/* Header */}
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-sky-50 to-indigo-50">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-sky-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
              d="M12 21a9 9 0 100-18 9 9 0 000 18zm0 0c2.5 0 4.5-4 4.5-9S14.5 3 12 3 7.5 7 7.5 12s2 9 4.5 9zM3 12h18" />
          </svg>
          <h2 className="text-lg font-semibold text-gray-900">Delegated Admins (MSP Mode)</h2>
        </div>
        <p className="mt-1 text-sm text-gray-600">
          Grant a person read access to a subset of tenants (a &ldquo;fleet&rdquo;), without making them a
          full platform admin. They sign in to their own tenant and see only the tenants you grant here.
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

        {/* Grant form */}
        <div className="bg-sky-50 border border-sky-200 rounded-lg p-4 space-y-3">
          <p className="text-sm font-medium text-sky-800">Grant access</p>
          <div className="flex flex-col gap-2 sm:flex-row sm:flex-wrap">
            <input
              type="email"
              value={newUpn}
              onChange={(e) => setNewUpn(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  e.preventDefault();
                  handleGrant();
                }
              }}
              placeholder="user@domain.com"
              autoComplete="off"
              className="flex-1 min-w-0 px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-sky-500 focus:border-sky-500 transition-colors"
            />
            <select
              value={newTenantId}
              onChange={(e) => setNewTenantId(e.target.value)}
              className="flex-1 min-w-0 px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-sky-500 transition-colors"
            >
              <option value="">Select tenant…</option>
              {tenants.map((t) => (
                <option key={t.tenantId} value={t.tenantId}>
                  {t.domainName || t.tenantId}
                </option>
              ))}
            </select>
            <select
              value={newRole}
              onChange={(e) => setNewRole(e.target.value)}
              className="px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-sky-500 transition-colors"
            >
              <option value="DelegatedReader">{ROLE_LABELS.DelegatedReader}</option>
              <option value="DelegatedAdmin">{ROLE_LABELS.DelegatedAdmin}</option>
            </select>
            <button
              onClick={handleGrant}
              disabled={granting || !newUpn.trim() || !newTenantId}
              className="px-6 py-2 bg-sky-600 text-white rounded-lg hover:bg-sky-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center justify-center space-x-2"
            >
              {granting ? (
                <>
                  <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" />
                  <span>Granting…</span>
                </>
              ) : (
                <span>Grant</span>
              )}
            </button>
          </div>
          <p className="text-xs text-sky-700">
            Reader is read-only (secrets redacted). The tenant must be onboarded to appear in the list above.
          </p>
        </div>

        {/* Loading */}
        {loading && (
          <div className="flex items-center justify-center py-8">
            <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-sky-600" />
            <span className="ml-3 text-sm text-gray-500">Loading assignments…</span>
          </div>
        )}

        {/* Empty */}
        {!loading && grouped.length === 0 && (
          <p className="text-sm text-gray-500 py-4 text-center">
            No delegated admins yet. Grant access above to enable MSP mode for a person.
          </p>
        )}

        {/* Assignments grouped by admin */}
        {!loading && grouped.length > 0 && (
          <div className="space-y-4">
            {grouped.map(([upn, rows]) => (
              <div key={upn} className="border border-gray-200 rounded-lg">
                <div className="px-4 py-2 border-b border-gray-100 bg-gray-50 rounded-t-lg">
                  <span className="font-medium text-gray-900">{upn}</span>
                  <span className="ml-2 text-xs text-gray-500">
                    {rows.length} tenant{rows.length === 1 ? "" : "s"}
                  </span>
                </div>
                <div className="divide-y divide-gray-100">
                  {rows.map((a) => {
                    const key = `${a.upn}|${a.tenantId}`;
                    const busy = busyKey === key;
                    return (
                      <div key={key} className="px-4 py-3 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
                        <div className="min-w-0">
                          <div className="flex flex-wrap items-center gap-1.5">
                            <span className="font-medium text-gray-900 truncate">
                              {domainOf(a.tenantId) || a.tenantId}
                            </span>
                            <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-sky-100 text-sky-800">
                              {ROLE_LABELS[a.role] ?? a.role}
                            </span>
                            {a.status === "Active" && a.isEnabled ? (
                              <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-green-100 text-green-800">
                                Active
                              </span>
                            ) : a.status === "PendingApproval" ? (
                              <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-amber-100 text-amber-800">
                                Pending approval
                              </span>
                            ) : a.status === "Revoked" ? (
                              <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-200 text-gray-700">
                                Revoked
                              </span>
                            ) : (
                              <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-200 text-gray-700">
                                Disabled
                              </span>
                            )}
                          </div>
                          <div className="text-xs text-gray-400 mt-1 font-mono break-all">{a.tenantId}</div>
                          <div className="text-xs text-gray-500 mt-0.5">
                            Granted {new Date(a.grantedAt).toLocaleDateString()} by {a.grantedBy || "—"}
                          </div>
                        </div>
                        <div className="flex items-center gap-2 shrink-0">
                          <button
                            onClick={() => handleToggle(a)}
                            disabled={busy}
                            className="px-3 py-1 text-sm border border-gray-300 rounded-lg hover:bg-gray-100 disabled:opacity-50 transition-colors"
                          >
                            {busy ? "…" : a.isEnabled ? "Disable" : "Enable"}
                          </button>
                          <button
                            onClick={() => handleRevoke(a)}
                            disabled={busy}
                            className="px-3 py-1 text-sm text-red-600 border border-red-200 rounded-lg hover:bg-red-50 disabled:opacity-50 transition-colors"
                          >
                            {busy ? "…" : "Revoke"}
                          </button>
                        </div>
                      </div>
                    );
                  })}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
