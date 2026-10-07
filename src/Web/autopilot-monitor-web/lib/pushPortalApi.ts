/**
 * The portal's calls to the Web Push routes (plan push-relay K5): pairing, the paired-device
 * list and the per-session watch. All authenticated through the apiClient layer like every
 * other portal call; non-2xx bodies are the `{error, code, correlationId}` envelope and surface
 * as ApiError, which the components render through apiErrorText/notifyError. The receiver app's
 * anonymous calls live in lib/push/pushApi.ts and are deliberately separate.
 */
import { api } from "@/lib/api";
import { ApiError, fetchJson, fetchOk, type GetAccessToken } from "@/lib/apiClient";
import type { ApiErrorCode } from "@/lib/apiErrorCodes";
import type { PushScope } from "@/lib/pushPortal";
import type {
  ConfirmPairingResponse,
  CreatePairingResponse,
  PairingStatusResponse,
  PushDeviceListResponse,
  SessionWatchResponse,
  TestWebhookNotificationResponse,
} from "@/utils/wire-types.generated";

/** 409: pairing or watching needs an enabled Push channel in this scope first (K4). */
const PUSH_CHANNEL_REQUIRED = "PushChannelRequired" satisfies ApiErrorCode;
/** 403: the caller has no table-backed Admin/Operator (or GlobalAdmin) row in this scope (K2). */
const PUSH_NOT_ELIGIBLE = "PushNotEligible" satisfies ApiErrorCode;

export function isPushChannelRequired(err: unknown): boolean {
  return err instanceof ApiError && err.code === PUSH_CHANNEL_REQUIRED;
}

export function isPushNotEligible(err: unknown): boolean {
  return err instanceof ApiError && err.code === PUSH_NOT_ELIGIBLE;
}

export function createPairing(scope: PushScope, getAccessToken: GetAccessToken): Promise<CreatePairingResponse> {
  return fetchJson<CreatePairingResponse>(api.push.pairings(scope), getAccessToken, { method: "POST" });
}

export function getPairingStatus(scope: PushScope, pairingId: string, getAccessToken: GetAccessToken, signal?: AbortSignal): Promise<PairingStatusResponse> {
  return fetchJson<PairingStatusResponse>(api.push.pairing(scope, pairingId), getAccessToken, { signal });
}

export function confirmPairing(scope: PushScope, pairingId: string, getAccessToken: GetAccessToken): Promise<ConfirmPairingResponse> {
  return fetchJson<ConfirmPairingResponse>(api.push.confirmPairing(scope, pairingId), getAccessToken, { method: "POST" });
}

export async function rejectPairing(scope: PushScope, pairingId: string, getAccessToken: GetAccessToken): Promise<void> {
  await fetchOk(api.push.rejectPairing(scope, pairingId), getAccessToken, { method: "POST" });
}

export function listPushDevices(scope: PushScope, getAccessToken: GetAccessToken): Promise<PushDeviceListResponse> {
  return fetchJson<PushDeviceListResponse>(api.push.devices(scope), getAccessToken);
}

export async function removePushDevice(scope: PushScope, deviceId: string, getAccessToken: GetAccessToken): Promise<void> {
  await fetchOk(api.push.device(scope, deviceId), getAccessToken, { method: "DELETE" });
}

export function testPushDevice(scope: PushScope, deviceId: string, getAccessToken: GetAccessToken): Promise<TestWebhookNotificationResponse> {
  return fetchJson<TestWebhookNotificationResponse>(api.push.testDevice(scope, deviceId), getAccessToken, { method: "POST" });
}

/** GET — never refuses: `{ watching: false }` when nothing is registered. */
export function getSessionWatch(sessionId: string, tenantId: string | undefined, getAccessToken: GetAccessToken): Promise<SessionWatchResponse> {
  return fetchJson<SessionWatchResponse>(api.sessions.watch(sessionId, tenantId), getAccessToken);
}

/** PUT to start watching (409 PushChannelRequired / "Pair a device first", 403 PushNotEligible), DELETE to stop. */
export function setSessionWatch(sessionId: string, tenantId: string | undefined, watching: boolean, getAccessToken: GetAccessToken): Promise<SessionWatchResponse> {
  return fetchJson<SessionWatchResponse>(api.sessions.watch(sessionId, tenantId), getAccessToken, { method: watching ? "PUT" : "DELETE" });
}
