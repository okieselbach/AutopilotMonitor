"use client";

import { Suspense, useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useAuth } from "@/contexts/AuthContext";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { api } from "@/lib/api";
import { describeDelegationError, invitationStatusLabel } from "@/lib/delegations";
import type { AcceptDelegationInvitationResponse, DelegationAcceptPreviewResponse } from "@/utils/wire-types.generated";

export default function AcceptDelegationPage() {
  // useSearchParams needs a Suspense boundary for the static prerender (query-string route).
  return (
    <Suspense fallback={null}>
      <AcceptDelegationInner />
    </Suspense>
  );
}

function AcceptDelegationInner() {
  const searchParams = useSearchParams();
  const token = searchParams?.get("token") ?? "";
  const { getAccessToken, user } = useAuth();

  const [preview, setPreview] = useState<DelegationAcceptPreviewResponse | null>(null);
  const [loadingState, setLoadingState] = useState(true);
  // No token ⇒ nothing to load; derived, so the effect never sets state synchronously.
  const loading = loadingState && !!token;
  const [error, setError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [accepting, setAccepting] = useState(false);
  const [done, setDone] = useState<AcceptDelegationInvitationResponse | null>(null);

  const explain = useCallback(async (response: Response, fallback: string) => {
    const data = await response.json().catch(() => ({}));
    if (response.status === 403) {
      return "Only a tenant administrator of your tenant can accept an invitation. Ask an administrator to open this link.";
    }
    return describeDelegationError(data.code, data.error || fallback);
  }, []);

  useEffect(() => {
    if (!token) return;
    let cancelled = false;
    const run = async () => {
      try {
        const response = await authenticatedFetch(api.delegations.acceptPreview(token), getAccessToken);
        if (cancelled) return;
        if (!response.ok) {
          setError(await explain(response, `Could not read the invitation (${response.status}).`));
          return;
        }
        setPreview((await response.json()) as DelegationAcceptPreviewResponse);
      } catch (err) {
        if (cancelled) return;
        setError(err instanceof TokenExpiredError ? "Session expired. Please refresh the page." : err instanceof Error ? err.message : "Could not read the invitation.");
      } finally {
        if (!cancelled) setLoadingState(false);
      }
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, [token, getAccessToken, explain]);

  const accept = useCallback(async () => {
    setAccepting(true);
    setError(null);
    try {
      const response = await authenticatedFetch(api.delegations.accept(), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ token }),
      });
      if (!response.ok) {
        setError(await explain(response, `Could not accept the invitation (${response.status}).`));
        return;
      }
      setDone((await response.json()) as AcceptDelegationInvitationResponse);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not accept the invitation.");
    } finally {
      setAccepting(false);
      setConfirming(false);
    }
  }, [token, getAccessToken, explain]);

  const homeLabel = preview?.homeTenantDomain || preview?.homeTenantId || "the inviting organization";
  const targetLabel = preview?.targetTenantDomain || preview?.targetTenantId || user?.tenantId || "your tenant";
  const acceptable = preview?.status === "Pending";

  return (
    <div className="mx-auto max-w-2xl p-4 sm:p-6 lg:p-8">
      <div className="bg-white rounded-lg shadow p-6 space-y-4">
        <h1 className="text-lg font-semibold text-gray-900">Delegated access invitation</h1>

        {loading && <p className="text-sm text-gray-500">Checking the invitation…</p>}

        {!token && (
          <div className="bg-red-50 border border-red-200 rounded-lg p-4 text-sm text-red-800">This link carries no invitation.</div>
        )}
        {error && (
          <div className="bg-red-50 border border-red-200 rounded-lg p-4 text-sm text-red-800">{error}</div>
        )}

        {done ? (
          <div className="space-y-3">
            <div className="bg-green-50 border border-green-200 rounded-lg p-4 text-sm text-green-800">
              <span className="font-medium">{done.homeTenantDomain || done.homeTenantId}</span> now has read-only access to your tenant.
              The grant is recorded in your audit log; you can end it at any time.
            </div>
            <Link href="/settings/tenant/delegations" className="text-sm font-medium text-purple-700 hover:underline">
              Review who can read your tenant
            </Link>
          </div>
        ) : preview && !error ? (
          <div className="space-y-4">
            <p className="text-sm text-gray-700">
              <span className="font-medium">{homeLabel}</span> asks for <span className="font-medium">read-only</span> access to{" "}
              <span className="font-medium">{targetLabel}</span> through Autopilot Monitor&rsquo;s delegated (MSP) administration.
            </p>
            <ul className="text-sm text-gray-600 list-disc pl-5 space-y-1">
              <li>Their assigned users can view your enrollment sessions, events and analytics — no configuration changes, no device actions, configuration secrets redacted.</li>
              <li>Their AI (MCP) requests into your tenant count against your organization&rsquo;s MCP budget and are listed on your MCP Usage page.</li>
              <li>Every grant and revoke is written to your audit log. You can revoke the access at any time under Settings → Tenant → Delegated Access.</li>
            </ul>
            <p className="text-xs text-gray-500">
              Invitation status: {invitationStatusLabel(preview.status)}
              {preview.status === "Pending" && ` · valid until ${new Date(preview.expiresUtc).toLocaleString()}`}
            </p>
            {acceptable && !confirming && (
              <button
                type="button"
                onClick={() => setConfirming(true)}
                className="w-full text-sm font-medium text-white bg-purple-600 rounded-lg px-4 py-2.5 hover:bg-purple-700 transition-colors"
              >
                Grant {homeLabel} read-only access
              </button>
            )}
            {acceptable && confirming && (
              <div className="flex items-center gap-2 text-sm">
                <span className="text-gray-600">Confirm the delegation?</span>
                <button
                  type="button"
                  onClick={accept}
                  disabled={accepting}
                  className="font-medium text-white bg-purple-600 rounded-lg px-3 py-1.5 hover:bg-purple-700 disabled:opacity-50 transition-colors"
                >
                  {accepting ? "Granting…" : "Confirm"}
                </button>
                <button type="button" onClick={() => setConfirming(false)} disabled={accepting} className="text-gray-500 hover:text-gray-700">
                  Cancel
                </button>
              </div>
            )}
            {!acceptable && (
              <p className="text-sm text-gray-600">{describeDelegationError(`Invitation${preview.status}`, "This invitation can no longer be accepted. Ask the managing organization for a new link.")}</p>
            )}
          </div>
        ) : null}

        {!loading && !done && (
          <Link href="/dashboard" className="inline-block text-sm text-gray-500 hover:text-gray-700">
            Back to the portal
          </Link>
        )}
      </div>
    </div>
  );
}
