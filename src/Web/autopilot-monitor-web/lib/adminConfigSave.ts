import { api } from "@/lib/api";
import { fetchJson, jsonBody, type GetAccessToken } from "@/lib/apiClient";
import { changedAdminConfigFields, fromWireAdminConfiguration, type AdminConfiguration } from "@/types/adminConfig";
import type {
  AdminConfiguration as WireAdminConfiguration,
  PatchAdminConfigurationRequest,
  UpdateAdminConfigurationResponse,
} from "@/utils/wire-types.generated";

/**
 * Saves the admin configuration by sending only what changed (PATCH global/config, D-285).
 * `loaded` is the configuration as the page received it, `edited` the same object with the
 * section's edits applied. Returns the stored configuration after the write, or null when nothing
 * changed and no request was sent.
 */
export async function saveAdminConfigChanges(
  loaded: AdminConfiguration,
  edited: AdminConfiguration,
  getAccessToken: GetAccessToken,
): Promise<AdminConfiguration | null> {
  const fields = changedAdminConfigFields(loaded, edited);
  if (Object.keys(fields).length === 0) return null;
  return patchAdminConfigFields(fields, getAccessToken);
}

/** Sends the given fields as they are — for a caller that changes one known field without a loaded page state. */
export async function patchAdminConfigFields(
  fields: Partial<WireAdminConfiguration>,
  getAccessToken: GetAccessToken,
): Promise<AdminConfiguration> {
  const result = await fetchJson<UpdateAdminConfigurationResponse>(api.globalConfig.update(), getAccessToken, {
    method: "PATCH",
    body: jsonBody<PatchAdminConfigurationRequest>({ fields }),
  });
  return fromWireAdminConfiguration(result.config);
}
