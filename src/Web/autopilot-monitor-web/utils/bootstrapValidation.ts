/**
 * Bootstrap response validator.
 *
 * SECURITY-CRITICAL: All validated values are interpolated into a PowerShell
 * script that runs as SYSTEM during Windows OOBE (see app/go/[code]/route.ts).
 * Every character class below was chosen to block PS metacharacters
 * ($, backticks, ", ', #>, newline) so that even if the second-line defense
 * (single-quoted PS string literals) is accidentally loosened, the validator
 * still prevents injection. Do NOT relax any rule without re-reviewing
 * route.ts end-to-end.
 */

import { isGuid } from "./inputValidation";
import { AGENT_DOWNLOAD_HOSTNAMES } from "./config";

export interface ValidatedBootstrapResponse {
  tenantId: string;
  token: string;
  agentDownloadUrl: string;
  expiresAt: string;
}

export type BootstrapValidationFailure =
  | "shape"
  | "success"
  | "tenantId"
  | "token"
  | "agentDownloadUrl"
  | "expiresAt";

export type BootstrapValidationResult =
  | { ok: true; value: ValidatedBootstrapResponse }
  | { ok: false; reason: BootstrapValidationFailure };

const PRINTABLE_ASCII_RE = /^[\x20-\x7E]+$/;

const ISO_8601_RE =
  /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/;

const AGENT_PATH_RE = /^\/agent\/[A-Za-z0-9_-][A-Za-z0-9._-]{0,79}\.zip$/;

// Both the download alias (what the backend serves going forward) and the
// legacy blob host (transition; bootstrap scripts already deployed in customer
// tenants). Single registry in utils/config.ts — mirrored on the C# side by
// Constants.AgentDownloadBaseUrl / Constants.AgentBlobBaseUrl.
const AGENT_HOSTNAMES: readonly string[] = AGENT_DOWNLOAD_HOSTNAMES;

const FOURTEEN_DAYS_MS = 14 * 24 * 60 * 60 * 1000;

function fail(reason: BootstrapValidationFailure): BootstrapValidationResult {
  return { ok: false, reason };
}

function isPrintableAsciiString(value: unknown, maxLength: number): value is string {
  return (
    typeof value === "string" &&
    value.length > 0 &&
    value.length <= maxLength &&
    PRINTABLE_ASCII_RE.test(value)
  );
}

function readField(data: Record<string, unknown>, key: string): unknown {
  return Object.prototype.hasOwnProperty.call(data, key) ? data[key] : undefined;
}

function isValidAgentDownloadUrl(value: string): boolean {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return false;
  }
  if (url.protocol !== "https:") return false;
  if (!AGENT_HOSTNAMES.includes(url.hostname)) return false;
  if (url.username !== "" || url.password !== "") return false;
  if (url.port !== "") return false;
  if (url.search !== "" || url.hash !== "") return false;
  if (!AGENT_PATH_RE.test(url.pathname)) return false;
  return true;
}

function isValidExpiresAt(value: string): boolean {
  if (!ISO_8601_RE.test(value)) return false;
  const parsed = new Date(value).getTime();
  if (!Number.isFinite(parsed)) return false;
  const now = Date.now();
  if (parsed <= now) return false;
  if (parsed > now + FOURTEEN_DAYS_MS) return false;
  return true;
}

export function validateBootstrapResponse(
  data: unknown,
): BootstrapValidationResult {
  // Shape: must be a plain object (not null, not array)
  if (typeof data !== "object" || data === null || Array.isArray(data)) {
    return fail("shape");
  }
  const obj = data as Record<string, unknown>;

  // success === true (strict — no truthy coercion)
  if (readField(obj, "success") !== true) {
    return fail("success");
  }

  // Field presence + ASCII + length bounds
  const tenantId = readField(obj, "tenantId");
  if (!isPrintableAsciiString(tenantId, 36) || tenantId.length !== 36) {
    return fail("tenantId");
  }
  if (!isGuid(tenantId)) {
    return fail("tenantId");
  }

  const token = readField(obj, "token");
  if (!isPrintableAsciiString(token, 36) || token.length !== 36) {
    return fail("token");
  }
  if (!isGuid(token)) {
    return fail("token");
  }

  const agentDownloadUrl = readField(obj, "agentDownloadUrl");
  if (!isPrintableAsciiString(agentDownloadUrl, 256)) {
    return fail("agentDownloadUrl");
  }
  if (!isValidAgentDownloadUrl(agentDownloadUrl)) {
    return fail("agentDownloadUrl");
  }

  const expiresAt = readField(obj, "expiresAt");
  if (!isPrintableAsciiString(expiresAt, 40)) {
    return fail("expiresAt");
  }
  if (!isValidExpiresAt(expiresAt)) {
    return fail("expiresAt");
  }

  return {
    ok: true,
    value: {
      tenantId,
      token,
      agentDownloadUrl,
      expiresAt,
    },
  };
}
