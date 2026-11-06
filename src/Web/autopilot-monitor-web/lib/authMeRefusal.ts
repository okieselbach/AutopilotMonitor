import type { ApiError } from "@/lib/apiClient";
import type { ApiErrorCode } from "@/lib/apiErrorCodes";

/**
 * What a refusal of auth/me means for the portal shell: ProtectedRoute renders the page of a suspended or
 * not yet activated tenant, and a refused view-as (lib/viewAs.ts) ends the mode.
 */
export type AuthMeRefusal =
  | { kind: "suspended"; message: string | null }
  | { kind: "activationPending"; message: string | null }
  | { kind: "viewAsRefused"; message: string | null };

const TENANT_SUSPENDED = "TenantSuspended" satisfies ApiErrorCode;
/** AuthFunction's activation gate; "PrivatePreview" is the legacy spelling, kept so web and backend deploy in any order. */
const ACTIVATION_PENDING_CODES: ReadonlySet<string> = new Set(["PendingActivation", "PrivatePreview"]);
/** The view-as admission (400 / 403 / 404): not a Global Admin, no such tenant, or unusable headers. */
const VIEW_AS_REFUSAL_CODES: ReadonlySet<string> = new Set([
  "ViewAsInvalid",
  "ViewAsNotAllowed",
  "ViewAsTenantNotFound",
] satisfies ApiErrorCode[]);

/**
 * Classify auth/me's refusal. Two body shapes reach the SPA:
 * - the error envelope `{ error: <text>, code, correlationId }` — written by the policy middleware,
 *   whose tenant-suspension gate answers BEFORE AuthFunction runs (every caller without platform
 *   scope of a suspended or offboarded tenant);
 * - AuthFunction's own pre-envelope body `{ error: <code>, message: <text> }` — the activation gate,
 *   and the suspension gate for platform callers.
 * apiErrorFromResponse maps `error` to ApiError.message and `code` to ApiError.code, so an empty
 * code marks the pre-envelope body, whose code then sits in the message.
 */
export function classifyAuthMeRefusal(err: ApiError): AuthMeRefusal | null {
  const envelope = err.code !== "";
  const code = envelope ? err.code : err.message;
  const bodyMessage = err.body?.message;
  const message = envelope ? err.message : typeof bodyMessage === "string" && bodyMessage.length > 0 ? bodyMessage : null;

  if (code === TENANT_SUSPENDED) return { kind: "suspended", message };
  if (ACTIVATION_PENDING_CODES.has(code)) return { kind: "activationPending", message };
  if (VIEW_AS_REFUSAL_CODES.has(code)) return { kind: "viewAsRefused", message };
  return null;
}
