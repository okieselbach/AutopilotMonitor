import { api } from "@/lib/api";
import { fetchOk, jsonBody, type GetAccessToken } from "@/lib/apiClient";
import { CONFIG_PATH_PREFIX, invalidateCachedAuthFetch } from "@/lib/cachedAuthFetch";
import type { SetupGuideStepRequest } from "@/lib/generated/wire-types.generated";
import { publishTenantConfigPatch } from "@/lib/tenantConfigPatches";
import { patchTenantConfigFields } from "@/lib/tenantConfigSave";

/** The steps the setup-guide endpoint records. */
export type SetupGuideMarker = "settings" | "hardware" | "dismiss" | "restore";

/** POST config/{tenantId}/setup-guide: the server stamps time and UPN. */
export async function recordSetupGuideStep(tenantId: string, step: SetupGuideMarker, getAccessToken: GetAccessToken): Promise<void> {
  await fetchOk(api.config.setupGuide(tenantId), getAccessToken, {
    method: "POST",
    body: jsonBody<SetupGuideStepRequest>({ step }),
  });
}

/**
 * Writes configuration fields from the guide through the field PATCH (the one write path of a
 * tenant configuration), then drops the cached flags and hands the fields to an open settings page.
 */
export async function writeGuideConfigFields(
  tenantId: string,
  fields: Record<string, unknown>,
  reason: string,
  getAccessToken: GetAccessToken,
): Promise<void> {
  if (!(await patchTenantConfigFields(api.config.fields(tenantId), fields, reason, getAccessToken))) return;
  invalidateCachedAuthFetch(CONFIG_PATH_PREFIX);
  publishTenantConfigPatch(tenantId, fields);
}
