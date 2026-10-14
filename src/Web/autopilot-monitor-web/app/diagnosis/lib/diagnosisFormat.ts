import { formatDuration } from "@/lib/formatting";
import { getErrorCodeDescription } from "@/lib/errorCodeMap";
import { humanizeKey } from "@/lib/evidenceChips";
import { vocabularyEntry, type Prerequisite } from "./vocabulary";

/** ESP phases and steps as the portal names them; anything else reads as its humanized key. */
const PHASE_NAMES: Record<string, string> = {
  DevicePreparation: "Device preparation",
  DeviceSetup: "Device setup",
  AccountSetup: "Account setup",
};

const STEP_NAMES: Record<string, string> = {
  Apps: "Apps",
  Certificates: "Certificates",
  SecurityPolicies: "Security policies",
  NetworkConnections: "Network connections",
  WaitingForAadRegistration: "Entra registration",
  TpmAttestation: "TPM attestation",
  Aadj: "Entra join",
  MdmEnrollment: "MDM enrollment",
  EspProviderInstallation: "ESP provider",
};

/** What the engine still waits for, read after "Still waiting for". */
const PREREQUISITES: Record<Prerequisite, string> = {
  account_setup_provisioning_complete: "Account setup to finish",
  hello_resolution: "Windows Hello to finish",
  desktop_arrival: "Desktop to appear",
  realmjoin_resolution: "RealmJoin to finish",
};

/** The name of a wait item: apps and providers by name, prerequisites in words. */
export function waitName(item: { kind: string; name: string }): string {
  if (item.kind !== "prerequisite") return item.name;
  return vocabularyEntry(PREREQUISITES, item.name) ?? humanizeKey(item.name);
}

export function phaseName(category: string | null | undefined): string | null {
  if (!category) return null;
  return PHASE_NAMES[category] ?? humanizeKey(category);
}

export function stepName(subcategory: string | null | undefined): string | null {
  if (!subcategory) return null;
  return STEP_NAMES[subcategory] ?? humanizeKey(subcategory);
}

/** "Account setup › Apps", "Account setup", "Apps" or null. */
export function stepPath(category: string | null | undefined, subcategory: string | null | undefined): string | null {
  const parts = [phaseName(category), stepName(subcategory)].filter((p): p is string => !!p);
  return parts.length > 0 ? parts.join(" › ") : null;
}

export interface FactDisplay {
  text: string;
  mono: boolean;
  /** Secondary line: the catalog meaning of an error code, the full date of a time. */
  hint?: string;
}

/** A fact value in its declared format (rule presentation formats: text, code, errorCode, duration, number, timestamp). */
export function formatFact(value: string, format: string): FactDisplay {
  switch (format) {
    case "errorCode": {
      const meaning = getErrorCodeDescription(value);
      return { text: value, mono: true, hint: meaning ?? undefined };
    }
    case "duration": {
      const seconds = Number(value);
      return Number.isFinite(seconds) && seconds >= 0 ? { text: formatDuration(seconds), mono: false } : { text: value, mono: false };
    }
    case "timestamp": {
      const ms = Date.parse(value);
      return Number.isFinite(ms)
        ? { text: new Date(ms).toLocaleTimeString(), mono: false, hint: new Date(ms).toLocaleString() }
        : { text: value, mono: false };
    }
    case "number": {
      const n = Number(value);
      return { text: Number.isFinite(n) ? n.toLocaleString() : value, mono: false };
    }
    case "code":
      return { text: value, mono: true };
    default:
      return { text: value, mono: false };
  }
}

/** Local time of day for an ISO timestamp; empty when absent or unparsable. */
export function timeOfDay(iso: string | null | undefined): string {
  const ms = iso ? Date.parse(iso) : NaN;
  return Number.isFinite(ms) ? new Date(ms).toLocaleTimeString() : "";
}

/** Short local time ("14:19", "2:19 PM") for dense views. */
export function shortTime(iso: string | null | undefined): string {
  const ms = iso ? Date.parse(iso) : NaN;
  return Number.isFinite(ms) ? new Date(ms).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" }) : "";
}
