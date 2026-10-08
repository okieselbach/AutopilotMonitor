import { describe, expect, it } from "vitest";
import { decideReconcileAction, type ReconcileInput } from "../push/pushReconcile";

/**
 * Pins the reconcile-on-open decision (K14): a Stale row is re-armed by replacing the dead
 * subscription, never by comparing endpoints; Paused behaves like Active.
 */

const HELD = "https://push.example.test/sub/held";
const STORED = "https://push.example.test/sub/stored";

function input(overrides: Partial<ReconcileInput>): ReconcileInput {
  return {
    serverStatus: "Active",
    heldEndpoint: HELD,
    metaEndpoint: HELD,
    permission: "granted",
    canSubscribe: true,
    ...overrides,
  };
}

describe("decideReconcileAction", () => {
  describe("Stale (the server refused the last delivery; only a PUT re-arms the row)", () => {
    it("re-subscribes even when the held endpoint equals the stored one", () => {
      expect(decideReconcileAction(input({ serverStatus: "Stale" }))).toBe("resubscribe");
      expect(decideReconcileAction(input({ serverStatus: "Stale", heldEndpoint: null }))).toBe("resubscribe");
    });

    it("falls back to a plain PUT of the held keys when it cannot subscribe", () => {
      expect(decideReconcileAction(input({ serverStatus: "Stale", permission: "default" }))).toBe("put");
      expect(decideReconcileAction(input({ serverStatus: "Stale", canSubscribe: false }))).toBe("put");
    });

    it("needs repair when there is neither a subscription nor a way to create one", () => {
      expect(decideReconcileAction(input({ serverStatus: "Stale", heldEndpoint: null, permission: "denied" }))).toBe("repair-required");
      expect(decideReconcileAction(input({ serverStatus: "Stale", heldEndpoint: null, canSubscribe: false }))).toBe("repair-required");
    });
  });

  describe("Active, Pending and Paused (Paused is resumed by the server, nothing special here)", () => {
    it.each(["Active", "Pending", "Paused"])("%s: a matching endpoint needs nothing", (serverStatus) => {
      expect(decideReconcileAction(input({ serverStatus }))).toBe("none");
    });

    it.each(["Active", "Pending", "Paused"])("%s: a drifted endpoint is PUT as it is", (serverStatus) => {
      expect(decideReconcileAction(input({ serverStatus, metaEndpoint: STORED }))).toBe("put");
      expect(decideReconcileAction(input({ serverStatus, metaEndpoint: null }))).toBe("put");
    });

    it.each(["Active", "Paused"])("%s: a missing subscription is recreated when permission allows", (serverStatus) => {
      expect(decideReconcileAction(input({ serverStatus, heldEndpoint: null }))).toBe("resubscribe");
      expect(decideReconcileAction(input({ serverStatus, heldEndpoint: null, permission: "default" }))).toBe("repair-required");
      expect(decideReconcileAction(input({ serverStatus, heldEndpoint: null, permission: "unsupported" }))).toBe("repair-required");
      expect(decideReconcileAction(input({ serverStatus, heldEndpoint: null, canSubscribe: false }))).toBe("repair-required");
    });
  });

  it("treats an unknown status like Active", () => {
    expect(decideReconcileAction(input({ serverStatus: "Whatever" }))).toBe("none");
    expect(decideReconcileAction(input({ serverStatus: "Whatever", metaEndpoint: STORED }))).toBe("put");
  });
});
