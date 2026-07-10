"use client";

import { useEffect, useMemo, useState } from "react";
import { useTenant } from "../../../contexts/TenantContext";
import { useAuth } from "../../../contexts/AuthContext";
import { useNotifications } from "../../../contexts/NotificationContext";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import type { SoftwareTabScope } from "./types";

interface InventoryItem {
  displayName: string;
  normalizedName: string;
  normalizedVendor: string;
  normalizedVersion: string;
  publisher: string;
  registrySource: string;
  normalizationConfidence: string;
  cpeUri: string;
  lastSeenAt: string;
  sessionCount: number;
}

interface InventoryResponse {
  success: boolean;
  tenantId: string;
  total: number;
  matched: number;
  unmatched: number;
  inventory: InventoryItem[];
}

const PAGE_SIZE = 20;

export default function InventoryTab({ scope }: { scope: SoftwareTabScope }) {
  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();
  const { isGlobalAdmin, routeGlobal, selectedTenantId, scopeInitialized, scopeKey } = scope;

  const [resp, setResp] = useState<InventoryResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(0);

  // A Global Admin must pick a concrete tenant — the inventory endpoint is per-tenant,
  // there is no cross-tenant aggregate.
  const needsTenantPick = isGlobalAdmin && !selectedTenantId;

  useEffect(() => {
    if (!scopeInitialized) return;
    if (!isGlobalAdmin && !tenantId) return;
    if (needsTenantPick) { setResp(null); setLoading(false); return; }
    let cancelled = false;

    const run = async () => {
      try {
        setLoading(true);
        const url = routeGlobal
          ? api.vulnerability.softwareInventory(selectedTenantId)
          : api.metrics.softwareInventory();
        const res = await authenticatedFetch(url, getAccessToken);
        if (cancelled) return;
        if (res.ok) {
          setResp((await res.json()) as InventoryResponse);
          setPage(0);
        } else {
          addNotification("error", "Backend Error", `Failed to load inventory: ${res.statusText}`, "inventory-error");
        }
      } catch (err) {
        if (cancelled) return;
        if (err instanceof TokenExpiredError) {
          addNotification("error", "Session Expired", err.message, "session-expired-error");
        } else {
          console.error("Failed to fetch software inventory", err);
          addNotification("error", "Backend Not Reachable", "Unable to load software inventory.", "inventory-error");
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    };

    void run();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scopeInitialized, scopeKey, needsTenantPick]);

  const filtered = useMemo<InventoryItem[]>(() => {
    const items = resp?.inventory ?? [];
    const q = search.trim().toLowerCase();
    const rows = q
      ? items.filter((i) =>
          (i.displayName || i.normalizedName).toLowerCase().includes(q) ||
          i.publisher.toLowerCase().includes(q))
      : items;
    return [...rows].sort((a, b) => b.sessionCount - a.sessionCount);
  }, [resp, search]);

  const pageCount = Math.max(1, Math.ceil(filtered.length / PAGE_SIZE));
  const pageRows = filtered.slice(page * PAGE_SIZE, page * PAGE_SIZE + PAGE_SIZE);

  function formatDate(iso: string) {
    if (!iso) return "—";
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? "—" : d.toLocaleDateString();
  }

  if (needsTenantPick) {
    return (
      <div className="bg-white rounded-lg shadow p-8 text-center text-gray-500">
        Select a tenant from the scope selector above to view its installed-software inventory.
      </div>
    );
  }

  return (
    <>
      {resp && (
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-4 mb-6">
          <div className="bg-white rounded-lg shadow p-4">
            <div className="text-sm text-gray-500">Distinct titles</div>
            <div className="text-2xl font-semibold text-gray-900 mt-1">{resp.total}</div>
          </div>
          <div className="bg-white rounded-lg shadow p-4">
            <div className="text-sm text-gray-500">CPE-mapped</div>
            <div className="text-2xl font-semibold text-emerald-700 mt-1">{resp.matched}</div>
          </div>
          <div className="bg-white rounded-lg shadow p-4">
            <div className="text-sm text-gray-500">Unmatched</div>
            <div className="text-2xl font-semibold text-amber-700 mt-1">{resp.unmatched}</div>
          </div>
        </div>
      )}

      <div className="bg-white rounded-lg shadow mb-4 p-4">
        <input
          type="text"
          placeholder="Search software by name or publisher…"
          value={search}
          onChange={(e) => { setSearch(e.target.value); setPage(0); }}
          className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent text-sm"
        />
      </div>

      <div className="bg-white rounded-lg shadow overflow-hidden">
        {loading ? (
          <div className="p-8 text-center text-gray-500">Loading…</div>
        ) : filtered.length === 0 ? (
          <div className="p-8 text-center text-gray-500">
            {search ? "No software matches your search." : "No software inventory collected for this tenant yet."}
          </div>
        ) : (
          <>
            <table className="min-w-full divide-y divide-gray-200">
              <thead className="bg-gray-50">
                <tr className="text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                  <th className="px-4 py-3">Software</th>
                  <th className="px-4 py-3">Version</th>
                  <th className="px-4 py-3">Publisher</th>
                  <th className="px-4 py-3 text-right">Sessions</th>
                  <th className="px-4 py-3 text-right">Last seen</th>
                  <th className="px-4 py-3 text-right">CPE</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200">
                {pageRows.map((row, idx) => (
                  <tr key={`${row.normalizedVendor}|${row.normalizedName}|${row.normalizedVersion}|${idx}`} className="text-sm">
                    <td className="px-4 py-3 text-gray-900 font-medium">{row.displayName || row.normalizedName}</td>
                    <td className="px-4 py-3 text-gray-700">{row.normalizedVersion || "—"}</td>
                    <td className="px-4 py-3 text-gray-700">{row.publisher || "—"}</td>
                    <td className="px-4 py-3 text-right text-gray-700">{row.sessionCount}</td>
                    <td className="px-4 py-3 text-right text-gray-500">{formatDate(row.lastSeenAt)}</td>
                    <td className="px-4 py-3 text-right">
                      {row.cpeUri ? (
                        <span className="inline-block px-1.5 py-0.5 rounded text-xs bg-emerald-100 text-emerald-800">mapped</span>
                      ) : (
                        <span className="inline-block px-1.5 py-0.5 rounded text-xs bg-amber-100 text-amber-800">unmatched</span>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {pageCount > 1 && (
              <div className="flex items-center justify-between px-4 py-3 text-sm text-gray-600 border-t border-gray-200">
                <span>{filtered.length} titles · page {page + 1} of {pageCount}</span>
                <div className="flex gap-2">
                  <button onClick={() => setPage((p) => Math.max(0, p - 1))} disabled={page === 0}
                    className="px-3 py-1 rounded-md bg-gray-100 hover:bg-gray-200 disabled:opacity-50 disabled:cursor-not-allowed">Previous</button>
                  <button onClick={() => setPage((p) => Math.min(pageCount - 1, p + 1))} disabled={page >= pageCount - 1}
                    className="px-3 py-1 rounded-md bg-gray-100 hover:bg-gray-200 disabled:opacity-50 disabled:cursor-not-allowed">Next</button>
                </div>
              </div>
            )}
          </>
        )}
      </div>
    </>
  );
}
