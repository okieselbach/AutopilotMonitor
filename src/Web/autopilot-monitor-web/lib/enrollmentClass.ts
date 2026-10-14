/**
 * Display names of the fleet enrollment classes (backend `TimeAttributionCalculator.GetEnrollmentClass`:
 * pre-provisioning → device preparation → self-deploying → user-driven).
 */
export const ENROLLMENT_CLASS_LABELS = {
  user_driven: "User-driven",
  whiteglove: "Pre-provisioning (WhiteGlove)",
  self_deploying: "Self-deploying",
  device_preparation: "Device Preparation (v2)",
} as const;

/** The display name of an enrollment class key; an unknown key shows as itself. */
export function enrollmentClassLabel(enrollmentClass: string): string {
  return (ENROLLMENT_CLASS_LABELS as Record<string, string>)[enrollmentClass] ?? enrollmentClass;
}
