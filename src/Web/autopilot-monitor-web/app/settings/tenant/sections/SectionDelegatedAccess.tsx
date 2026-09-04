"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { useAuth } from "@/contexts/AuthContext";
import { useTenantConfig } from "../../TenantConfigContext";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { api } from "@/lib/api";
import { SectionCardHeader } from "@/components/SectionCardHeader";
import { DOCS_PATHS } from "@/lib/docsPaths";
import { isProViaMsp } from "@/lib/edition";
import { buildInviteLink, describeDelegationError, holdRemainingLabel, invitationStatusLabel } from "@/lib/delegations";
import type {
  DelegationAssigneeListResponse,
  DelegationInvitationListResponse,
  ManagedTenantListResponse,
  TenantManagerListResponse,
} from "@/utils/wire-types.generated";

type Confirm =
  | { kind: "revoke"; homeTenantId: string; label: string }
  | { kind: "remove"; tenantId: string; label: string }
  | { kind: "unassign"; upn: string }
  | { kind: "cancel"; invitationId: string }
  | null;

function formatDay(iso: string | undefined | null): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "—" : d.toLocaleDateString();
}

/**
 * Settings → Tenant → Delegated Access. Two cards: (1) who can read THIS tenant through a delegation (every
 * tenant, with a customer-side revoke for self-service delegations), (2) for a Pro tenant, the tenants it
 * manages as an MSP: slots, managed tenants with their MCP budget, invitation links (single-use, copy-only),
 * and which of its own users hold the delegated access. Every mutation is audited under both tenants by
 * the backend; removing a customer holds its slot for 24 hours.
 */
export function SectionDelegatedAccess() {
  const { getAccessToken, user } = useAuth();
  const { canEditConfig, editionInfo, admins } = useTenantConfig();
  const canManage = editionInfo.entitlements.delegatedAdminAllowed;

  const [managers, setManagers] = useState<TenantManagerListResponse | null>(null);
  const [managed, setManaged] = useState<ManagedTenantListResponse | null>(null);
  const [invitations, setInvitations] = useState<DelegationInvitationListResponse | null>(null);
  const [assignees, setAssignees] = useState<DelegationAssigneeListResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [flash, setFlash] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<Confirm>(null);
  const [assignUpn, setAssignUpn] = useState("");
  // The invitation token is shown ONCE (the backend never returns it again).
  const [createdLink, setCreatedLink] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const notify = (msg: string) => {
    setFlash(msg);
    setTimeout(() => setFlash(null), 4000);
  };

  const handleError = useCallback((err: unknown, fallback: string) => {
    setError(err instanceof TokenExpiredError ? "Session expired. Please refresh the page." : err instanceof Error ? err.message : fallback);
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const mgr = await authenticatedFetch(api.delegations.managers(), getAccessToken);
      if (!mgr.ok) throw new Error(`Failed to load delegated access: ${mgr.status}`);
      setManagers((await mgr.json()) as TenantManagerListResponse);

      if (canManage) {
        const [m, i, a] = await Promise.all([
          authenticatedFetch(api.delegations.managed(), getAccessToken),
          authenticatedFetch(api.delegations.invitations(), getAccessToken),
          authenticatedFetch(api.delegations.assignees(), getAccessToken),
        ]);
        setManaged(m.ok ? ((await m.json()) as ManagedTenantListResponse) : null);
        setInvitations(i.ok ? ((await i.json()) as DelegationInvitationListResponse) : null);
        setAssignees(a.ok ? ((await a.json()) as DelegationAssigneeListResponse) : null);
      }
    } catch (err) {
      handleError(err, "Failed to load delegated access");
    } finally {
      setLoading(false);
    }
  }, [getAccessToken, canManage, handleError]);

  useEffect(() => {
    if (!canEditConfig) return;
    const run = async () => {
      await load();
    };
    void run();
  }, [load, canEditConfig]);

  /** Runs a mutation; a structured error body ({ error, code }) is explained via describeDelegationError. */
  const mutate = useCallback(
    async (key: string, url: string, method: string, body: unknown, ok: string): Promise<Response | null> => {
      setBusy(key);
      setError(null);
      try {
        const init: RequestInit = { method };
        if (body !== undefined) {
          init.headers = { "Content-Type": "application/json" };
          init.body = JSON.stringify(body);
        }
        const response = await authenticatedFetch(url, getAccessToken, init);
        if (!response.ok) {
          const data = await response.json().catch(() => ({}));
          throw new Error(describeDelegationError(data.code, data.error || `Request failed: ${response.statusText}`));
        }
        notify(ok);
        setConfirm(null);
        await load();
        return response;
      } catch (err) {
        handleError(err, "Request failed");
        return null;
      } finally {
        setBusy(null);
      }
    },
    [getAccessToken, load, handleError],
  );

  const createInvitation = useCallback(async () => {
    setBusy("invite");
    setError(null);
    setCreatedLink(null);
    setCopied(false);
    try {
      const response = await authenticatedFetch(api.delegations.invitations(), getAccessToken, { method: "POST" });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(describeDelegationError(data.code, data.error || `Request failed: ${response.statusText}`));
      setCreatedLink(buildInviteLink(window.location.origin, data.token));
      notify("Invitation created — copy the link now; it is shown only once.");
      await load();
    } catch (err) {
      handleError(err, "Failed to create the invitation");
    } finally {
      setBusy(null);
    }
  }, [getAccessToken, load, handleError]);

  const copyLink = useCallback(async () => {
    if (!createdLink) return;
    try {
      await navigator.clipboard.writeText(createdLink);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }, [createdLink]);

  const assignableUpns = useMemo(() => {
    const assigned = new Set((assignees?.assignees ?? []).map((a) => a.upn.toLowerCase()));
    return admins.filter((a) => !assigned.has(a.upn.toLowerCase())).map((a) => a.upn);
  }, [admins, assignees]);

  if (!canEditConfig) {
    return (
      <div className="bg-amber-50 border border-amber-200 rounded-lg p-4 text-sm text-amber-800">
        This page is available to tenant administrators only.
      </div>
    );
  }

  const slots = managed?.slots;
  const now = new Date();

  return (
    <div className="space-y-6">
      {flash && (
        <div className="bg-green-50 border border-green-200 rounded-lg p-4 text-sm text-green-800">{flash}</div>
      )}
      {error && (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4 text-sm text-red-800">{error}</div>
      )}

      {/* Card 1 — who manages my tenant */}
      <div className="bg-white rounded-lg shadow">
        <SectionCardHeader
          tone="skyIndigo"
          iconPath="M16 7a4 4 0 11-8 0 4 4 0 018 0zM12 14a7 7 0 00-7 7h14a7 7 0 00-7-7z"
          title="Who can read this tenant"
          subtitle="Organizations with delegated (MSP) read access to your tenant. Every grant and revoke is written to your audit log."
          docsPath={DOCS_PATHS.delegatedAccess}
        />
        <div className="p-6 space-y-3">
          {isProViaMsp(editionInfo) && (
            <p className="text-sm text-purple-800 bg-purple-50 border border-purple-200 rounded-lg px-3 py-2">
              Your Pro plan is included through one of these organizations. It ends when that delegation ends.
            </p>
          )}
          {loading && !managers ? (
            <p className="text-sm text-gray-500">Loading…</p>
          ) : !managers || managers.managers.length === 0 ? (
            <p className="text-sm text-gray-500">No organization has delegated access to your tenant.</p>
          ) : (
            <ul className="divide-y divide-gray-100">
              {managers.managers.map((m) => {
                const key = m.groupId ?? "operators";
                const isRevoke = confirm?.kind === "revoke" && confirm.homeTenantId === m.ownerTenantId;
                return (
                  <li key={key} className="py-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm">
                    <span className="font-medium text-gray-900">{m.ownerDomain || m.name}</span>
                    <span className={`inline-flex items-center px-1.5 py-0.5 rounded text-xs font-medium ${m.source === "self-service" ? "bg-sky-100 text-sky-800" : "bg-gray-100 text-gray-700"}`}>
                      {m.source === "self-service" ? "Delegated by you" : "Platform operators"}
                    </span>
                    <span className="text-xs text-gray-500">
                      {m.assignees.length} reader{m.assignees.length === 1 ? "" : "s"}
                      {m.sinceUtc && ` · since ${formatDay(m.sinceUtc)}`}
                    </span>
                    <span className="ml-auto flex items-center gap-2">
                      {m.revocable && m.ownerTenantId && !isRevoke && (
                        <button
                          type="button"
                          onClick={() => setConfirm({ kind: "revoke", homeTenantId: m.ownerTenantId!, label: m.ownerDomain || m.name })}
                          className="text-xs text-red-600 hover:text-red-800"
                        >
                          Revoke
                        </button>
                      )}
                      {m.revocable && isRevoke && (
                        <>
                          <span className="text-xs text-gray-600">End {confirm.label}&rsquo;s access now?</span>
                          <button
                            type="button"
                            disabled={busy !== null}
                            onClick={() => mutate("revoke", api.delegations.revokeManager(), "POST", { homeTenantId: m.ownerTenantId }, `Access of ${confirm.label} revoked.`)}
                            className="text-xs font-medium text-white bg-red-600 rounded px-2 py-1 hover:bg-red-700 disabled:opacity-50"
                          >
                            {busy === "revoke" ? "Revoking…" : "Confirm"}
                          </button>
                          <button type="button" onClick={() => setConfirm(null)} className="text-xs text-gray-500 hover:text-gray-700">Cancel</button>
                        </>
                      )}
                      {!m.revocable && <span className="text-xs text-gray-400">managed by support</span>}
                    </span>
                    {m.assignees.length > 0 && (
                      <span className="w-full text-xs text-gray-500 break-all">{m.assignees.map((a) => a.upn).join(", ")}</span>
                    )}
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      </div>

      {/* Card 2 — tenants you manage (Pro) */}
      <div className="bg-white rounded-lg shadow">
        <SectionCardHeader
          tone="purple"
          iconPath="M19 11H5m14 0a2 2 0 012 2v6a2 2 0 01-2 2H5a2 2 0 01-2-2v-6a2 2 0 012-2m14 0V9a2 2 0 00-2-2M5 11V9a2 2 0 012-2m0 0V5a2 2 0 012-2h6a2 2 0 012 2v2M7 7h10"
          title="Tenants you manage"
          subtitle="Invite customer tenants with a single-use link; their admin accepts it and your assigned users get read-only access. Tenants you manage are on Pro for as long as you manage them, and each managed tenant's AI (MCP) usage draws on that tenant's own plan."
          docsPath={DOCS_PATHS.delegatedAccess}
          trailing={slots && (
            <span className="inline-flex items-center px-2.5 py-1 rounded-full text-xs font-medium bg-purple-100 text-purple-800">
              {slots.used} of {slots.limit} slot{slots.limit === 1 ? "" : "s"} in use
            </span>
          )}
        />
        <div className="p-6 space-y-5">
          {!canManage ? (
            <p className="text-sm text-gray-600">
              Managing other tenants is a Pro capability.{" "}
              <Link href="/settings/tenant/plan" className="font-medium text-purple-700 hover:underline">See the Plan page</Link>{" "}
              to start a trial or upgrade.
            </p>
          ) : loading && !managed ? (
            <p className="text-sm text-gray-500">Loading…</p>
          ) : (
            <>
              {slots && (
                <p className="text-xs text-gray-500">
                  {slots.managedTenantIds.length} managed
                  {slots.pendingInvitations > 0 && ` · ${slots.pendingInvitations} pending invitation${slots.pendingInvitations === 1 ? "" : "s"}`}
                  {slots.holds.length > 0 && ` · ${slots.holds.length} slot${slots.holds.length === 1 ? "" : "s"} held after a removal`}
                  {slots.used >= slots.limit && " · no free slot — remove a tenant or ask for a larger package"}
                </p>
              )}

              {/* Managed tenants */}
              <div className="space-y-2">
                <p className="text-xs font-semibold text-gray-700 uppercase tracking-wide">Managed tenants</p>
                {!managed || managed.tenants.length === 0 ? (
                  <p className="text-sm text-gray-400">No managed tenants yet — create an invitation below.</p>
                ) : (
                  <ul className="divide-y divide-gray-100">
                    {managed.tenants.map((t) => {
                      const isRemove = confirm?.kind === "remove" && confirm.tenantId === t.tenantId;
                      return (
                        <li key={t.tenantId} className="py-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm">
                          <span className="font-medium text-gray-900">{t.domain || t.tenantId}</span>
                          {t.source === "operator" && (
                            <span className="inline-flex items-center px-1.5 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-700">provisioned by operators</span>
                          )}
                          {t.usage && (
                            <span className="text-xs text-gray-500 font-mono" title={`MCP organization budget of this tenant (plan ${t.usage.tenantPlan})`}>
                              MCP {t.usage.tenantDailyUsed}/{t.usage.tenantDailyLimit || "∞"} today · {t.usage.tenantMonthlyUsed}/{t.usage.tenantMonthlyLimit || "∞"} month
                            </span>
                          )}
                          {t.sinceUtc && <span className="text-xs text-gray-500">since {formatDay(t.sinceUtc)}</span>}
                          <span className="ml-auto flex items-center gap-2">
                            {t.removable && !isRemove && (
                              <button type="button" onClick={() => setConfirm({ kind: "remove", tenantId: t.tenantId, label: t.domain || t.tenantId })} className="text-xs text-red-600 hover:text-red-800">
                                Remove
                              </button>
                            )}
                            {t.removable && isRemove && (
                              <>
                                <span className="text-xs text-gray-600">Remove {confirm.label}? Its slot stays held for 24 h.</span>
                                <button
                                  type="button"
                                  disabled={busy !== null}
                                  onClick={() => mutate("remove", api.delegations.removeManaged(), "POST", { tenantId: t.tenantId }, `${confirm.label} removed.`)}
                                  className="text-xs font-medium text-white bg-red-600 rounded px-2 py-1 hover:bg-red-700 disabled:opacity-50"
                                >
                                  {busy === "remove" ? "Removing…" : "Confirm"}
                                </button>
                                <button type="button" onClick={() => setConfirm(null)} className="text-xs text-gray-500 hover:text-gray-700">Cancel</button>
                              </>
                            )}
                          </span>
                        </li>
                      );
                    })}
                  </ul>
                )}
              </div>

              {/* Invitations */}
              <div className="space-y-2">
                <div className="flex items-center justify-between">
                  <p className="text-xs font-semibold text-gray-700 uppercase tracking-wide">Invitations</p>
                  <button
                    type="button"
                    onClick={createInvitation}
                    disabled={busy !== null || (slots !== undefined && slots.used >= slots.limit)}
                    className="px-3 py-1.5 text-sm bg-purple-600 text-white rounded-lg hover:bg-purple-700 disabled:opacity-50 transition-colors"
                    title={slots && slots.used >= slots.limit ? "No free slot" : "Create a single-use invitation link (valid 7 days)"}
                  >
                    {busy === "invite" ? "Creating…" : "Create invitation link"}
                  </button>
                </div>
                {createdLink && (
                  <div className="bg-purple-50 border border-purple-200 rounded-lg p-3 space-y-2">
                    <p className="text-xs text-purple-900">
                      Send this link to an administrator of the tenant you want to manage. It works once and expires in 7 days — it is not shown again.
                    </p>
                    <div className="flex flex-col gap-2 sm:flex-row">
                      <input readOnly value={createdLink} onFocus={(e) => e.currentTarget.select()} className="flex-1 min-w-0 px-3 py-1.5 border border-purple-200 rounded-lg text-xs font-mono text-gray-900 bg-white" />
                      <button type="button" onClick={copyLink} className="shrink-0 px-3 py-1.5 text-sm bg-purple-600 text-white rounded-lg hover:bg-purple-700">
                        {copied ? "Copied" : "Copy link"}
                      </button>
                    </div>
                  </div>
                )}
                {invitations && invitations.invitations.length > 0 && (
                  <ul className="divide-y divide-gray-100">
                    {invitations.invitations.map((inv) => {
                      const isCancel = confirm?.kind === "cancel" && confirm.invitationId === inv.invitationId;
                      const hold = inv.status === "Released" ? holdRemainingLabel(inv.holdUntilUtc, now) : "";
                      return (
                        <li key={inv.invitationId} className="py-1.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm">
                          <span className={`inline-flex items-center px-1.5 py-0.5 rounded text-xs font-medium ${inv.status === "Pending" ? "bg-amber-100 text-amber-800" : inv.status === "Accepted" ? "bg-green-100 text-green-800" : "bg-gray-100 text-gray-600"}`}>
                            {invitationStatusLabel(inv.status)}
                          </span>
                          <span className="text-gray-900">{inv.tenantDomain || inv.tenantId || "not yet accepted"}</span>
                          <span className="text-xs text-gray-500">
                            created {formatDay(inv.createdUtc)} by {inv.createdBy}
                            {inv.status === "Pending" && ` · expires ${formatDay(inv.expiresUtc)}`}
                            {inv.acceptedUtc && ` · accepted ${formatDay(inv.acceptedUtc)}`}
                            {hold && ` · ${hold}`}
                          </span>
                          {inv.status === "Pending" && (
                            <span className="ml-auto flex items-center gap-2">
                              {!isCancel ? (
                                <button type="button" onClick={() => setConfirm({ kind: "cancel", invitationId: inv.invitationId })} className="text-xs text-red-600 hover:text-red-800">Cancel</button>
                              ) : (
                                <>
                                  <span className="text-xs text-gray-600">Cancel this invitation?</span>
                                  <button
                                    type="button"
                                    disabled={busy !== null}
                                    onClick={() => mutate("cancel", api.delegations.cancelInvitation(inv.invitationId), "DELETE", undefined, "Invitation cancelled.")}
                                    className="text-xs font-medium text-white bg-red-600 rounded px-2 py-1 hover:bg-red-700 disabled:opacity-50"
                                  >
                                    Confirm
                                  </button>
                                  <button type="button" onClick={() => setConfirm(null)} className="text-xs text-gray-500 hover:text-gray-700">Keep</button>
                                </>
                              )}
                            </span>
                          )}
                        </li>
                      );
                    })}
                  </ul>
                )}
              </div>

              {/* Assignees */}
              <div className="space-y-2">
                <p className="text-xs font-semibold text-gray-700 uppercase tracking-wide">Your users with access</p>
                <p className="text-xs text-gray-500">
                  Members of your tenant (Access Management) who may read every managed tenant — read-only, configuration secrets redacted.
                </p>
                {assignees && assignees.assignees.length > 0 && (
                  <ul className="divide-y divide-gray-100">
                    {assignees.assignees.map((a) => {
                      const isUnassign = confirm?.kind === "unassign" && confirm.upn === a.upn;
                      return (
                        <li key={a.upn} className="py-1.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm">
                          <span className="text-gray-900 break-all">{a.upn}</span>
                          {!a.isEnabled && <span className="text-xs text-gray-500">(disabled)</span>}
                          <span className="ml-auto flex items-center gap-2">
                            {!isUnassign ? (
                              <button type="button" onClick={() => setConfirm({ kind: "unassign", upn: a.upn })} className="text-xs text-red-600 hover:text-red-800">Remove</button>
                            ) : (
                              <>
                                <span className="text-xs text-gray-600">Remove {a.upn}&rsquo;s access?</span>
                                <button
                                  type="button"
                                  disabled={busy !== null}
                                  onClick={() => mutate("unassign", api.delegations.unassign(a.upn), "DELETE", undefined, `${a.upn} removed.`)}
                                  className="text-xs font-medium text-white bg-red-600 rounded px-2 py-1 hover:bg-red-700 disabled:opacity-50"
                                >
                                  Confirm
                                </button>
                                <button type="button" onClick={() => setConfirm(null)} className="text-xs text-gray-500 hover:text-gray-700">Cancel</button>
                              </>
                            )}
                          </span>
                        </li>
                      );
                    })}
                  </ul>
                )}
                <div className="flex gap-2">
                  <select
                    value={assignUpn}
                    onChange={(e) => setAssignUpn(e.target.value)}
                    className="flex-1 min-w-0 px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-purple-500"
                  >
                    <option value="">Add a member…</option>
                    {assignableUpns.map((upn) => (
                      <option key={upn} value={upn}>{upn}</option>
                    ))}
                  </select>
                  <button
                    type="button"
                    disabled={!assignUpn || busy !== null}
                    onClick={async () => {
                      const ok = await mutate("assign", api.delegations.assignees(), "POST", { upn: assignUpn }, `${assignUpn} can now read your managed tenants.`);
                      if (ok) setAssignUpn("");
                    }}
                    className="shrink-0 px-3 py-1.5 text-sm bg-purple-600 text-white rounded-lg hover:bg-purple-700 disabled:opacity-50 transition-colors"
                  >
                    Assign
                  </button>
                </div>
                {user?.upn && assignableUpns.length === 0 && (assignees?.assignees.length ?? 0) === 0 && (
                  <p className="text-xs text-gray-400">Add members under Access Management first.</p>
                )}
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
