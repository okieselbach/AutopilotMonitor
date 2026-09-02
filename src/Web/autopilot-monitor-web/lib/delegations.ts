/**
 * Self-service delegation (MSP) — pure helpers for the "Delegated Access" section and the invitation
 * accept page. No React, unit-tested.
 */

export const DELEGATION_ACCEPT_ROUTE = "/delegations/accept";

/** In-app path of an invitation link (static export ⇒ query string, never a path segment). */
export function delegationAcceptPath(token: string): string {
  return `${DELEGATION_ACCEPT_ROUTE}?token=${encodeURIComponent(token)}`;
}

/** The link a managing tenant admin copies and hands to the customer. */
export function buildInviteLink(origin: string, token: string): string {
  return `${origin.replace(/\/+$/, "")}${delegationAcceptPath(token)}`;
}

/** Display label for an invitation's wire status (Pending | Accepted | Cancelled | Released | Expired). */
export function invitationStatusLabel(status: string): string {
  switch (status) {
    case "Pending": return "Pending";
    case "Accepted": return "Accepted";
    case "Cancelled": return "Cancelled";
    case "Released": return "Removed";
    case "Expired": return "Expired";
    default: return status;
  }
}

/** "Slot held for 23 h" style countdown for a release hold; empty once it lapsed. */
export function holdRemainingLabel(holdUntilUtc: string | null | undefined, now: Date = new Date()): string {
  if (!holdUntilUtc) return "";
  const until = new Date(holdUntilUtc).getTime();
  if (Number.isNaN(until)) return "";
  const ms = until - now.getTime();
  if (ms <= 0) return "";
  const hours = Math.ceil(ms / 3_600_000);
  return hours >= 2 ? `slot held for ${hours} h` : "slot held for less than an hour";
}

/**
 * Human explanation for the accept flow's error codes (DelegationCodes / DelegatedSlotLimitReached);
 * falls back to the backend message.
 */
export function describeDelegationError(code: string | undefined, fallback: string): string {
  switch (code) {
    case "InvalidInvitation": return "This invitation link is not valid. Ask the managing organization for a new link.";
    case "InvitationExpired": return "This invitation has expired (links are valid for 7 days). Ask the managing organization for a new link.";
    case "InvitationAlreadyUsed": return "This invitation has already been used. Ask the managing organization for a new link.";
    case "InvitationCancelled": return "The managing organization cancelled this invitation.";
    case "CannotAcceptOwnInvitation": return "An invitation is accepted by an administrator of the tenant to be managed — not by the inviting tenant.";
    case "AlreadyManaged": return "Your tenant is already managed by this organization.";
    case "ManagerNotEntitled": return "The inviting organization is no longer on a plan that includes delegated administration.";
    case "DelegatedSlotLimitReached": return "The managing organization has no free delegated tenant slot. Ask them to raise their limit, then try the link again.";
    case "DelegatedAdminNotAllowed": return "Delegated administration is a Pro capability.";
    default: return fallback;
  }
}
