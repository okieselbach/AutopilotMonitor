import { SHARED_MANIFEST } from "@/utils/shared-manifests.generated";

/**
 * SignalR message (target) names the backend can send — derived from the generated shared
 * manifest (source of truth: Constants.SignalRMessages in AutopilotMonitor.Shared).
 * SignalRContext types its on/off against this union, so subscribing to a name the backend
 * never sends fails tsc instead of silently never firing. Renaming a name backend-side
 * regenerates the manifest (SharedManifestParityTests) and breaks stale web literals here.
 */
export const SIGNALR_MESSAGES = SHARED_MANIFEST.signalRMessages;

export type SignalRMessageName = (typeof SIGNALR_MESSAGES)[number];
