/**
 * Delegated (MSP) tenant slots — pure helpers for the Global Admin "limit reached → raise it and retry"
 * flow, and for the MCP quota growth a purchased slot brings. The backend answers 409 with code
 * DelegatedSlotLimitReached (DelegatedSlotLimitReachedResponse) when a grant / group assignment / add-tenant
 * would push a managing tenant over its slot limit.
 */

export const DELEGATED_SLOT_LIMIT_REACHED = "DelegatedSlotLimitReached";

export interface SlotLimitError {
  homeTenantId: string;
  homeTenantDomain: string | null;
  used: number;
  limit: number;
  required: number;
  error: string;
}

/** The parsed 409 body when it is the slot-limit conflict; null for every other status/body. */
export function parseSlotLimitError(status: number, body: unknown): SlotLimitError | null {
  if (status !== 409 || !body || typeof body !== "object") return null;
  const b = body as Record<string, unknown>;
  if (b.code !== DELEGATED_SLOT_LIMIT_REACHED || typeof b.homeTenantId !== "string") return null;
  return {
    homeTenantId: b.homeTenantId,
    homeTenantDomain: typeof b.homeTenantDomain === "string" && b.homeTenantDomain ? b.homeTenantDomain : null,
    used: typeof b.used === "number" ? b.used : 0,
    limit: typeof b.limit === "number" ? b.limit : 0,
    required: typeof b.required === "number" ? b.required : 1,
    error: typeof b.error === "string" ? b.error : "Delegated tenant slot limit reached.",
  };
}

/** The smallest limit that lets the rejected mutation through. */
export function nextSlotLimit(e: Pick<SlotLimitError, "used" | "limit" | "required">): number {
  return Math.max(e.limit, e.used + e.required);
}

/** Display label of the managing tenant: its domain, else the id. */
export function slotTenantLabel(e: Pick<SlotLimitError, "homeTenantId" | "homeTenantDomain">): string {
  return e.homeTenantDomain || e.homeTenantId;
}

/** "base + slots × perSlot" behind an MCP quota window that grew with purchased delegation slots. */
export interface SlotBreakdown {
  base: number;
  slots: number;
  perSlot: number;
}

/**
 * Splits a quota limit into the plan's base and the growth from delegation slots bought beyond the ones the
 * edition includes. The wire limit already contains the growth (base = limit − slots × perSlot); null when
 * no slot was bought, a slot adds nothing, the window is unlimited (0), or the figures do not add up.
 */
export function slotBreakdown(limit: number, slots: number | undefined, perSlot: number | undefined): SlotBreakdown | null {
  if (!slots || slots <= 0 || !perSlot || perSlot <= 0 || limit <= 0) return null;
  const base = limit - slots * perSlot;
  return base < 0 ? null : { base, slots, perSlot };
}
