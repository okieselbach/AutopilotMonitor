import { SHARED_MANIFEST } from "@/utils/shared-manifests.generated";

/**
 * Machine-readable `code` values an error envelope can carry — derived from the generated shared
 * manifest (source of truth: Constants.ApiErrorCodes, DelegationCodes, DelegatedSlots and
 * AppHomingReasonCodes in AutopilotMonitor.Shared). A web literal that the backend no longer
 * emits fails tsc (`satisfies ApiErrorCode`) instead of silently never matching.
 */
export const API_ERROR_CODES = SHARED_MANIFEST.apiErrorCodes;

export type ApiErrorCode = (typeof API_ERROR_CODES)[number];
