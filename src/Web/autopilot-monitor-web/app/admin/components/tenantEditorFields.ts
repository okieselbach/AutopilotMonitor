import { changedTenantConfigFields, type TenantConfigFieldName } from "@/lib/tenantConfigSave";
import type { TenantConfiguration } from "@/utils/wire-types.generated";

/**
 * The fields the Global Admin tenant editor saves with its generic Save. Plan, trial, delegated
 * slots, MCP plan and the paying flag have their own endpoint (PATCH config/{id}/plan), app homing
 * its own (POST app-homing), offboarding its own (DELETE offboard): those never travel here, and the
 * field PATCH would refuse them.
 */
export const TENANT_EDITOR_FIELDS: readonly TenantConfigFieldName[] = [
  "disabled",
  "disabledReason",
  "disabledUntil",
  "mcpDisabled",
  "mcpDisabledReason",
  "mcpClientRegistrationLimit",
  "bootstrapTokenEnabled",
  "unrestrictedModeEnabled",
  "entraAppRolesEnabled",
  "enableEspContinueAnywayObservation",
  "customRateLimitRequestsPerMinute",
  "customUserRateLimitRequestsPerMinute",
  "dataRetentionDays",
];

/**
 * The editor's PATCH payload: only the editor fields the Global Admin changed. Closing the
 * Unrestricted Mode gate also switches the mode itself off (a write-through), because the backend
 * refuses an enabled mode behind a closed gate.
 */
export function tenantEditorPatch(loaded: TenantConfiguration, edited: TenantConfiguration): Record<string, unknown> {
  const fields = changedTenantConfigFields(loaded, edited, TENANT_EDITOR_FIELDS);
  if (edited.unrestrictedModeEnabled === false && loaded.unrestrictedMode === true) {
    fields.unrestrictedMode = false;
  }
  return fields;
}
