/** A consent flow that enables one validation gate. */
export type ValidationTrigger = "autopilot" | "corporate" | "device-preparation";

/** Adds "restore": repairs the Graph access of gates that are already on. */
export type ConsentTrigger = ValidationTrigger | "restore";

interface ValidationGateChange {
  validateAutopilotDevice?: boolean;
  validateCorporateIdentifier?: boolean;
  validateDeviceAssociation?: boolean;
}

/** Reads the trigger that survived the consent redirect; absent or unknown means the autopilot default. */
export function parseConsentTrigger(raw: string | null): ConsentTrigger {
  return raw === "corporate" || raw === "device-preparation" || raw === "restore" ? raw : "autopilot";
}

/**
 * The gate a verified consent opens. "restore" opens none: the gates are already on, and opening
 * the autopilot gate would give, say, a Cloud PC tenant a validation nobody asked for.
 */
export function gateChangeFor(trigger: ConsentTrigger): ValidationGateChange | null {
  if (trigger === "restore") return null;
  if (trigger === "corporate") return { validateCorporateIdentifier: true };
  if (trigger === "device-preparation") return { validateDeviceAssociation: true };
  return { validateAutopilotDevice: true };
}
