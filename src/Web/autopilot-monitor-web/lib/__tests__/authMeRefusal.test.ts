import { describe, expect, it, vi } from "vitest";
import { apiErrorFromResponse } from "../apiClient";
import { classifyAuthMeRefusal } from "../authMeRefusal";

vi.mock("../appInsights", () => ({ trackEvent: vi.fn() }));

/**
 * auth/me refuses in two body shapes; both must land on the right portal page. The envelope
 * case is the one that regressed: since the typed envelope (2026-09-05) the policy middleware's
 * suspension gate carries the code in `code`, and the old `error === "TenantSuspended"` check
 * never matched — suspended and offboarded tenants saw neither page.
 */
async function refusal(body: unknown) {
  const response = new Response(typeof body === "string" ? body : JSON.stringify(body), {
    status: 403,
    statusText: "Forbidden",
    headers: { "Content-Type": "application/json" },
  });
  return classifyAuthMeRefusal(await apiErrorFromResponse(response));
}

describe("classifyAuthMeRefusal", () => {
  it("reads the policy middleware's envelope for a suspended tenant", async () => {
    expect(
      await refusal({
        error: "Your tenant has been suspended. Please contact support for more information.",
        code: "TenantSuspended",
        correlationId: "3f2a9c1e-7b4d-4e0a-9d2c-1a2b3c4d5e6f",
      }),
    ).toEqual({
      kind: "suspended",
      message: "Your tenant has been suspended. Please contact support for more information.",
    });
  });

  it("keeps the offboarding tombstone text, which switches ProtectedRoute to the farewell page", async () => {
    expect(await refusal({ error: "Offboarding in progress", code: "TenantSuspended" })).toEqual({
      kind: "suspended",
      message: "Offboarding in progress",
    });
  });

  it("reads AuthFunction's pre-envelope body for a suspended tenant", async () => {
    expect(
      await refusal({ error: "TenantSuspended", message: "Maintenance window", disabledUntil: null, contactSupport: true }),
    ).toEqual({ kind: "suspended", message: "Maintenance window" });
  });

  it("reads the activation gate and its legacy PrivatePreview spelling", async () => {
    expect(await refusal({ error: "PendingActivation", message: "Your tenant is being activated." })).toEqual({
      kind: "activationPending",
      message: "Your tenant is being activated.",
    });
    expect(await refusal({ error: "PrivatePreview" })).toEqual({ kind: "activationPending", message: null });
  });

  it("returns null for any other refusal", async () => {
    expect(
      await refusal({
        error: "Access denied. You do not have permission to access this resource.",
        code: "InsufficientPermissions",
      }),
    ).toBeNull();
    // A suspension text in the envelope's `error` must not be mistaken for the code.
    expect(await refusal({ error: "TenantSuspended", code: "Forbidden" })).toBeNull();
    expect(await refusal("<html>Forbidden</html>")).toBeNull();
  });
});
