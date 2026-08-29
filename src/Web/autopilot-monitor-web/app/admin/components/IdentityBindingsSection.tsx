"use client";

import { useCallback, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { useCanMutatePlatform } from "@/hooks/useCanMutatePlatform";
import { isGuid, type IdentityBinding } from "@/lib/identityBinding";

interface IdentityBindingsSectionProps {
  tenantId: string;
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}

/**
 * Collapsed-by-default view of the identity bindings HOMED in this tenant: every UPN that holds a
 * cross-tenant role (platform GlobalAdmin/GlobalReader, delegated MSP grants) and signs in from here,
 * with the Entra tenant + object id the role is bound to. Maintained automatically (resolved from sign-in
 * history at grant time, object id pinned on the first sign-in); this is the inspection / correction
 * surface so a re-created account or a re-homed UPN never needs a table edit.
 */
export function IdentityBindingsSection({
  tenantId,
  getAccessToken,
  setError,
  setSuccessMessage,
}: IdentityBindingsSectionProps) {
  const canMutate = useCanMutatePlatform();
  const [open, setOpen] = useState(false);
  const [loaded, setLoaded] = useState(false);
  const [bindings, setBindings] = useState<IdentityBinding[]>([]);
  const [editing, setEditing] = useState<string | null>(null);
  const [editTenantId, setEditTenantId] = useState("");
  const [editObjectId, setEditObjectId] = useState("");
  const [busy, setBusy] = useState<string | null>(null);

  const fail = useCallback(
    (err: unknown, fallback: string) => {
      if (err instanceof TokenExpiredError) setError(err.message);
      else setError(err instanceof Error ? err.message : fallback);
    },
    [setError],
  );

  const load = useCallback(async () => {
    try {
      const response = await authenticatedFetch(api.identityBindings.list(), getAccessToken);
      if (!response.ok) throw new Error(`Failed to load identity bindings: ${response.statusText}`);
      const data = await response.json();
      const all: IdentityBinding[] = data.bindings ?? [];
      setBindings(
        all
          .filter((b) => b.tenantId.toLowerCase() === tenantId.toLowerCase())
          .sort((a, b) => a.upn.localeCompare(b.upn)),
      );
      setLoaded(true);
    } catch (err) {
      fail(err, "Failed to load identity bindings");
    }
  }, [tenantId, getAccessToken, fail]);

  // Lazy: the list is fetched the first time the panel is opened (event-driven, no effect).
  const toggle = () => {
    const next = !open;
    setOpen(next);
    if (next && !loaded) void load();
  };

  const startEdit = (b: IdentityBinding) => {
    setEditing(b.upn);
    setEditTenantId(b.tenantId);
    setEditObjectId(b.objectId);
  };

  const save = async (upn: string) => {
    const tid = editTenantId.trim();
    const oid = editObjectId.trim();
    if (!isGuid(tid) || (oid !== "" && !isGuid(oid))) return;
    try {
      setBusy(upn);
      setError(null);
      const response = await authenticatedFetch(api.identityBindings.put(upn), getAccessToken, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ homeTenantId: tid, objectId: oid || undefined }),
      });
      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error(data.error || `Failed to update binding: ${response.statusText}`);
      }
      setSuccessMessage(`Identity binding for ${upn} updated.`);
      setEditing(null);
      await load();
    } catch (err) {
      fail(err, "Failed to update binding");
    } finally {
      setBusy(null);
    }
  };

  const remove = async (upn: string) => {
    if (!confirm(`Remove the identity binding for ${upn}? Every platform/delegated role of this UPN stops working until it is granted again.`)) return;
    try {
      setBusy(upn);
      setError(null);
      const response = await authenticatedFetch(api.identityBindings.remove(upn), getAccessToken, { method: "DELETE" });
      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error(data.error || `Failed to remove binding: ${response.statusText}`);
      }
      setSuccessMessage(`Identity binding for ${upn} removed.`);
      await load();
    } catch (err) {
      fail(err, "Failed to remove binding");
    } finally {
      setBusy(null);
    }
  };

  const editValid = isGuid(editTenantId) && (editObjectId.trim() === "" || isGuid(editObjectId));

  return (
    <div className="bg-gray-50 border border-gray-200 rounded-lg">
      <button
        type="button"
        onClick={toggle}
        className="w-full flex items-center justify-between px-4 py-3 text-left"
        aria-expanded={open}
      >
        <span className="font-semibold text-gray-900">
          Identity bindings
          {loaded && <span className="ml-2 text-xs font-normal text-gray-500">{bindings.length}</span>}
        </span>
        <svg
          className={`w-4 h-4 text-gray-500 transition-transform ${open ? "rotate-180" : ""}`}
          fill="none" stroke="currentColor" viewBox="0 0 24 24"
        >
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
        </svg>
      </button>
      {open && (
        <div className="px-4 pb-4 space-y-2">
          <p className="text-xs text-gray-500">
            Cross-tenant roles (platform admins, delegated MSP admins) are bound to the Entra tenant and account they
            were granted for. Bindings are maintained automatically; correct one here only if an account was re-created.
          </p>
          {!loaded ? (
            <p className="text-sm text-gray-400">Loading…</p>
          ) : bindings.length === 0 ? (
            <p className="text-sm text-gray-400">No cross-tenant role holders are homed in this tenant.</p>
          ) : (
            <ul className="divide-y divide-gray-200 border border-gray-200 rounded-lg bg-white">
              {bindings.map((b) => (
                <li key={b.upn} className="px-3 py-2 text-sm">
                  {editing === b.upn ? (
                    <div className="space-y-2">
                      <div className="font-medium text-gray-900 break-all">{b.upn}</div>
                      <input
                        type="text"
                        value={editTenantId}
                        onChange={(e) => setEditTenantId(e.target.value)}
                        placeholder="Home tenant ID (GUID)"
                        spellCheck={false}
                        className="w-full px-3 py-1.5 border border-gray-300 rounded-lg text-sm font-mono text-gray-900 focus:outline-none focus:ring-2 focus:ring-sky-500"
                      />
                      <input
                        type="text"
                        value={editObjectId}
                        onChange={(e) => setEditObjectId(e.target.value)}
                        placeholder="Object ID (GUID) — leave empty to re-pin on the next sign-in"
                        spellCheck={false}
                        className="w-full px-3 py-1.5 border border-gray-300 rounded-lg text-sm font-mono text-gray-900 focus:outline-none focus:ring-2 focus:ring-sky-500"
                      />
                      <div className="flex gap-2">
                        <button
                          onClick={() => save(b.upn)}
                          disabled={!editValid || busy === b.upn}
                          className="px-3 py-1 text-sm bg-sky-600 text-white rounded-lg hover:bg-sky-700 disabled:opacity-50 transition-colors"
                        >
                          {busy === b.upn ? "Saving…" : "Save"}
                        </button>
                        <button
                          onClick={() => setEditing(null)}
                          className="px-3 py-1 text-sm border border-gray-300 rounded-lg hover:bg-gray-100 transition-colors"
                        >
                          Cancel
                        </button>
                      </div>
                    </div>
                  ) : (
                    <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                      <span className="font-medium text-gray-900 break-all">{b.upn}</span>
                      <span className="text-xs font-mono text-gray-500">
                        {b.isObjectIdPinned ? `oid ${b.objectId}` : "object id pinned on next sign-in"}
                      </span>
                      {canMutate && (
                        <span className="ml-auto flex gap-2 shrink-0">
                          <button
                            onClick={() => startEdit(b)}
                            disabled={busy === b.upn}
                            className="text-xs text-sky-700 hover:text-sky-900 disabled:opacity-50"
                          >
                            Edit
                          </button>
                          <button
                            onClick={() => remove(b.upn)}
                            disabled={busy === b.upn}
                            className="text-xs text-red-600 hover:text-red-800 disabled:opacity-50"
                          >
                            Remove
                          </button>
                        </span>
                      )}
                    </div>
                  )}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}
