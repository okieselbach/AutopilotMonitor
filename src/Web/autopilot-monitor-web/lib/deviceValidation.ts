import { SHARED_MANIFEST } from "@/utils/shared-manifests.generated";
import type { GetGraphPermissionsStatusResponse } from "@/utils/wire-types.generated";

/**
 * Every `validate*` flag of the tenant configuration, read from the shared manifest. The agent
 * gate accepts traffic when any of them is on (TenantConfiguration.HasAnyDeviceValidation in the
 * backend, pinned to "every Validate* flag" by its own test), so a new validation method is
 * picked up here without a code change.
 */
export const DEVICE_VALIDATION_FIELDS: readonly string[] =
  SHARED_MANIFEST.tenantConfiguration.fields.filter((field: string) => /^validate[A-Z]/.test(field));

/** True when at least one device-validation method is enabled on this configuration. */
export function hasAnyDeviceValidation(config: object): boolean {
  const values = config as Record<string, unknown>;
  return DEVICE_VALIDATION_FIELDS.some((field) => values[field] === true);
}

/** Grant-script feature backing Intune Enrollment Validation (GraphFeatureCatalog.FeatureIntuneDeviceBinding). */
export const INTUNE_ENROLLMENT_FEATURE = "IntuneDeviceBinding";

/** Granted verdict of one add-on feature; null while unknown (not loaded, failed, or transient snapshot). */
export function addOnGranted(status: GetGraphPermissionsStatusResponse | null, feature: string): boolean | null {
  if (!status || status.isTransient) return null;
  return status.features.find((f) => f.name === feature)?.granted ?? null;
}
