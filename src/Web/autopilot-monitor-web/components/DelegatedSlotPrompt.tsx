"use client";

import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";
import { nextSlotLimit, slotTenantLabel, type SlotLimitError } from "@/lib/delegatedSlots";

/**
 * Raises a managing tenant's delegated slot override via PATCH config/{tenantId}/plan (GlobalAdminOnly).
 * Returns the error text on failure — a 404 means the home tenant has no config row yet (not onboarded),
 * which the bump flow cannot fix.
 */
export async function raiseDelegatedSlotLimit(
  getAccessToken: () => Promise<string | null>,
  homeTenantId: string,
  newLimit: number,
): Promise<string | null> {
  const response = await authenticatedFetch(api.config.plan(homeTenantId), getAccessToken, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ maxDelegatedTenants: newLimit }),
  });
  if (response.ok) return null;
  if (response.status === 404) {
    return "The managing tenant is not onboarded yet (no tenant configuration) — onboard it first, then raise its slot limit.";
  }
  const data = await response.json().catch(() => ({}));
  return data.error || `Failed to raise the slot limit: ${response.statusText}`;
}

interface DelegatedSlotPromptProps {
  prompt: SlotLimitError;
  busy: boolean;
  onRaise: (newLimit: number) => void;
  onCancel: () => void;
}

/**
 * Inline confirm (house pattern, no modal) shown when a delegated-admin mutation hit the managing tenant's
 * slot limit: names the tenant, its usage, and offers to raise the limit to the smallest value that fits,
 * then retry the original mutation.
 */
export function DelegatedSlotPrompt({ prompt, busy, onRaise, onCancel }: DelegatedSlotPromptProps) {
  const next = nextSlotLimit(prompt);
  return (
    <div className="bg-amber-50 border border-amber-200 rounded-lg p-4 space-y-2 text-sm">
      <p className="text-amber-900">
        <span className="font-medium">{slotTenantLabel(prompt)}</span> has {prompt.used} of {prompt.limit} delegated tenant
        slot{prompt.limit === 1 ? "" : "s"} in use; this change needs {prompt.required} more.
      </p>
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-gray-700">Raise the limit to {next} and retry?</span>
        <button
          type="button"
          onClick={() => onRaise(next)}
          disabled={busy}
          className="font-medium text-white bg-amber-600 rounded-lg px-3 py-1.5 hover:bg-amber-700 disabled:opacity-50 transition-colors"
        >
          {busy ? "Raising…" : `Raise to ${next} & retry`}
        </button>
        <button type="button" onClick={onCancel} disabled={busy} className="text-gray-500 hover:text-gray-700">
          Cancel
        </button>
      </div>
      <p className="text-xs text-gray-500">
        The override is saved on the managing tenant&rsquo;s plan (Tenant Management → Plan &amp; Trial) and is audited there.
      </p>
    </div>
  );
}
