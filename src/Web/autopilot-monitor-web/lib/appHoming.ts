/**
 * Shared helpers for the dual app-reg homing flip (POST /api/config/{tenantId}/app-homing).
 * Kept JSX-free so the reason-code mapping is unit-testable under vitest.
 */

/** Maps the backend's flip-denial reason codes to user-facing messages. */
export function appHomingErrorMessage(reason: string | undefined, fallback: string): string {
  switch (reason) {
    case "parallel-window-inactive":
      return "The dual app-registration window is not active on the backend.";
    case "self-service-disabled":
      return "Self-service app switching is currently disabled by the operator.";
    case "probe-failed":
      return "The new app registration is not fully consented in this tenant (missing, or lacking a Graph permission the previous app holds). Grant admin consent first, or force the switch as Global Admin.";
    case "probe-transient":
      return "Could not verify consent for the new app registration right now (transient error). Please try again.";
    case "revert-is-ga-only":
    case "force-is-ga-only":
      return "Only Global Admins can perform this action.";
    case "tenant-not-found":
      return "Tenant configuration not found.";
    case "invalid-target":
      return "Invalid switch target.";
    default:
      return `Failed to switch app registration: ${fallback}`;
  }
}
