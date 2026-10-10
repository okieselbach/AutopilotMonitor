/**
 * The receiver's calls to the push API. They are anonymous (code-gated) or device-token-gated,
 * so they do not go through the authenticated apiClient layer — plain fetch, like
 * lib/platformStats.ts. Every non-2xx body is the standard `{error, code, correlationId,
 * retryAfterSeconds?}` envelope and surfaces as PushApiError. Request and response shapes are
 * the generated wire types (shared manifest parity).
 */
import { API_BASE_URL } from "@/lib/config";
import type {
  BeginPairRequest,
  BeginPairResponse,
  PushDeviceStatusResponse,
  RedeemPairRequest,
  RedeemPairResponse,
  ResubscribeRequest,
} from "@/lib/generated/wire-types.generated";
import { pushApiRequest } from "./pushCore";

/** The wire carries `status` as a string; these are its documented values. */
export type DeviceStatus = "Pending" | "Active" | "Paused" | "Stale";

export type PairBeginResponse = BeginPairResponse;
export type PairRequest = RedeemPairRequest;
export type PairResponse = RedeemPairResponse;
export type PushDeviceResponse = PushDeviceStatusResponse;
export type UpdateSubscriptionRequest = ResubscribeRequest;

export const PUSH_ERROR_CODES = {
  pairingCodeInvalid: "PairingCodeInvalid",
  pairingCodeUsed: "PairingCodeUsed",
  invalidSubscription: "InvalidSubscription",
  rateLimited: "RateLimited",
  deviceNotFound: "DeviceNotFound",
  invalidDeviceToken: "InvalidDeviceToken",
} as const;

export class PushApiError extends Error {
  readonly status: number;
  readonly code: string | null;
  readonly correlationId: string | null;
  readonly retryAfterSeconds: number | null;

  constructor(status: number, message: string, code: string | null, correlationId: string | null, retryAfterSeconds: number | null) {
    super(message);
    this.name = "PushApiError";
    this.status = status;
    this.code = code;
    this.correlationId = correlationId;
    this.retryAfterSeconds = retryAfterSeconds;
  }

  is(code: string): boolean {
    return this.code === code;
  }
}

interface ErrorEnvelope {
  error?: unknown;
  code?: unknown;
  correlationId?: unknown;
  retryAfterSeconds?: unknown;
}

async function errorFromResponse(response: Response): Promise<PushApiError> {
  let envelope: ErrorEnvelope = {};
  try {
    envelope = (await response.json()) as ErrorEnvelope;
  } catch {
    // Non-JSON body (gateway page) — the status alone has to do.
  }
  return new PushApiError(
    response.status,
    typeof envelope.error === "string" && envelope.error ? envelope.error : `HTTP ${response.status}`,
    typeof envelope.code === "string" ? envelope.code : null,
    typeof envelope.correlationId === "string" ? envelope.correlationId : null,
    typeof envelope.retryAfterSeconds === "number" ? envelope.retryAfterSeconds : null,
  );
}

async function send<T>(path: string, options: { method?: string; token?: string | null; body?: unknown }): Promise<T> {
  const { url, init } = pushApiRequest(API_BASE_URL, path, options);
  const response = await fetch(url, { ...init, signal: AbortSignal.timeout(20_000) });
  if (!response.ok) throw await errorFromResponse(response);
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export function pairBegin(code: string): Promise<PairBeginResponse> {
  const body: BeginPairRequest = { code };
  return send<PairBeginResponse>("/api/push/pair/begin", { method: "POST", body });
}

export function pairRedeem(request: PairRequest): Promise<PairResponse> {
  return send<PairResponse>("/api/push/pair", { method: "POST", body: request });
}

export function getDevice(token: string): Promise<PushDeviceResponse> {
  return send<PushDeviceResponse>("/api/push/device", { token });
}

export function updateDeviceSubscription(token: string, request: UpdateSubscriptionRequest): Promise<PushDeviceResponse> {
  return send<PushDeviceResponse>("/api/push/device", { method: "PUT", token, body: request });
}

export function deleteDevice(token: string): Promise<void> {
  return send<void>("/api/push/device", { method: "DELETE", token });
}

/** A human sentence for the pairing and device errors the pages show. */
export function describePushError(error: unknown): string {
  if (error instanceof PushApiError) {
    switch (error.code) {
      case PUSH_ERROR_CODES.pairingCodeInvalid:
        return "This code is unknown, expired or already used. Generate a new one in the portal.";
      case PUSH_ERROR_CODES.pairingCodeUsed:
        return "This code was already redeemed by another device. Generate a new one in the portal.";
      case PUSH_ERROR_CODES.invalidSubscription:
        return "The push service of this browser was refused. Try another browser or device.";
      case PUSH_ERROR_CODES.rateLimited:
        return error.retryAfterSeconds
          ? `Too many attempts. Try again in ${error.retryAfterSeconds} seconds.`
          : "Too many attempts. Try again in a few minutes.";
      case PUSH_ERROR_CODES.deviceNotFound:
        return "This device is no longer paired.";
      case PUSH_ERROR_CODES.invalidDeviceToken:
        return "The stored device credentials are no longer valid. Pair the device again.";
      default:
        return error.correlationId ? `${error.message} (ref ${error.correlationId.slice(0, 8)})` : error.message;
    }
  }
  if (error instanceof Error && error.name === "TimeoutError") return "The server did not answer in time. Check the connection and try again.";
  if (error instanceof TypeError) return "The server could not be reached. Check the connection and try again.";
  return "Something went wrong. Try again.";
}
