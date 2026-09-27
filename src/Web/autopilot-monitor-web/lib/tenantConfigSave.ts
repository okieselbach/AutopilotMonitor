import { fetchOk, jsonBody, type GetAccessToken } from "@/lib/apiClient";
import { SHARED_MANIFEST } from "@/utils/shared-manifests.generated";
import type { PatchTenantConfigurationFieldsRequest } from "@/utils/wire-types.generated";

/**
 * Wire (camelCase) name of a TenantConfiguration field, typed against the generated shared
 * manifest — a typo or a field the backend model no longer has fails tsc.
 */
export type TenantConfigFieldName = (typeof SHARED_MANIFEST.tenantConfiguration.fields)[number];

/**
 * The listed fields whose value differs between the loaded and the edited configuration: the
 * payload of the field PATCH, the only write path of a tenant configuration (D-290 — there is no
 * full-model PUT). Unchanged fields never travel, so a page loaded before someone else's write
 * cannot revert it. undefined and null both mean "cleared"; the PATCH expresses a clear as an
 * explicit JSON null (an omitted key would leave the stored value untouched).
 */
export function changedTenantConfigFields(
  loaded: object,
  edited: object,
  fields: readonly TenantConfigFieldName[],
): Record<string, unknown> {
  const before = loaded as Record<string, unknown>;
  const after = edited as Record<string, unknown>;
  const changed: Record<string, unknown> = {};
  for (const field of fields) {
    const next = after[field] ?? null;
    const prev = before[field] ?? null;
    if (JSON.stringify(next) !== JSON.stringify(prev)) changed[field] = next;
  }
  return changed;
}

/**
 * PATCHes the given fields to `url` (an `api.config.fields…` URL). Sends nothing and returns false
 * when there are no fields. The backend verifies that exactly these fields changed, so a caller may
 * merge them into its local copy on success.
 */
export async function patchTenantConfigFields(
  url: string,
  fields: Record<string, unknown>,
  reason: string,
  getAccessToken: GetAccessToken,
): Promise<boolean> {
  if (Object.keys(fields).length === 0) return false;
  await fetchOk(url, getAccessToken, {
    method: "PATCH",
    body: jsonBody<PatchTenantConfigurationFieldsRequest>({ fields, reason }),
  });
  return true;
}
