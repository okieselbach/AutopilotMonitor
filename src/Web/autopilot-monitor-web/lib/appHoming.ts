/**
 * Shared helpers for the dual app-reg homing flip (POST /api/config/{tenantId}/app-homing) and
 * the self-service funnel. Kept JSX-free so the reason-code mapping and the grant-command
 * builder are unit-testable under vitest.
 */

import { AGENT_DOWNLOAD_URL } from "@/utils/config";

/** Maps the backend's flip-denial reason codes to user-facing messages. */
export function appHomingErrorMessage(
  reason: string | undefined,
  fallback: string,
  missingRoles?: readonly string[] | null,
): string {
  switch (reason) {
    case "parallel-window-inactive":
      return "The dual app-registration window is not active on the backend.";
    case "self-service-disabled":
      return "Self-service app switching is currently disabled by the operator.";
    case "probe-failed":
      return missingRoles && missingRoles.length > 0
        ? `The new app registration is consented, but it lacks optional Graph add-on permission(s) the previous app holds in this tenant: ${missingRoles.join(", ")}. Grant them on the new app (Settings → Autopilot Validation shows the command), or force the switch as Global Admin.`
        : "The new app registration is not fully consented in this tenant (missing, or lacking a Graph permission the previous app holds). Grant admin consent first, or force the switch as Global Admin.";
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

export const ADD_ON_GRANT_SCRIPT_NAME = "Grant-AutopilotMonitorAddOn.ps1";

/** Published location of the customer-side add-on grant script (next to the agent artifacts). */
export const ADD_ON_GRANT_SCRIPT_URL = `${AGENT_DOWNLOAD_URL}/${ADD_ON_GRANT_SCRIPT_NAME}`;

/**
 * What to grant: a feature name for the script's `-Features` switch (the Optional Graph
 * capabilities page), or raw Graph application permission strings for `-Permissions` (the
 * app-homing funnel, which knows the exact roles the new app still lacks).
 */
export type AddOnGrantSelection =
  | { features: string }
  | { permissions: readonly string[] };

/**
 * Copy-paste-ready PowerShell: download the grant script, then run it against the service
 * principal of `clientId` in `tenantId`. The client id is the caller's choice on purpose — the
 * funnel must target the NEW app while the tenant is still homed on the previous one.
 */
export function buildAddOnGrantCommand(
  clientId: string,
  tenantId: string | undefined,
  selection: AddOnGrantSelection,
): string {
  const selector = "permissions" in selection
    ? `-Permissions ${selection.permissions.map((p) => `"${p}"`).join(",")}`
    : `-Features ${selection.features}`;
  return [
    `irm '${ADD_ON_GRANT_SCRIPT_URL}' -OutFile .\\${ADD_ON_GRANT_SCRIPT_NAME}`,
    `.\\${ADD_ON_GRANT_SCRIPT_NAME} \``,
    `    -ClientId "${clientId}" \``,
    `    ${selector} \``,
    `    -TenantId "${tenantId ?? "<your-tenant-id>"}"`,
  ].join("\n");
}
