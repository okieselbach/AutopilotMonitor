import { describe, expect, it } from "vitest";
import { decideReconcileAction, type ReconcileInput } from "../push/pushReconcile";

/**
 * Pins the reconcile-on-open decision (K14): a Stale row is re-armed by replacing the dead
 * subscription, never by comparing endpoints; Paused behaves like Active; a rotated platform
 * key (B-y4z) is a re-registration with the server's active key that wins over everything else.
 */

const HELD = "https://push.example.test/sub/held";
const STORED = "https://push.example.test/sub/stored";

/** Defaults describe a healthy device without a key rotation (stored kid = active kid). */
function input(overrides: Partial<ReconcileInput>): ReconcileInput {
  return {
    serverStatus: "Active",
    heldEndpoint: HELD,
    metaEndpoint: HELD,
    permission: "granted",
    canSubscribe: true,
    metaKid: "k1",
    activeKid: "k1",
    canRekey: true,
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

  describe("key rotation (B-y4z: a new platform key is a re-registration, never a re-pair)", () => {
    it("rekeys when the active kid differs from the stored one, whatever the endpoints say", () => {
      expect(decideReconcileAction(input({ activeKid: "k2" }))).toBe("rekey"); // would be none
      expect(decideReconcileAction(input({ activeKid: "k2", metaEndpoint: STORED }))).toBe("rekey"); // would be put
      expect(decideReconcileAction(input({ activeKid: "k2", metaEndpoint: null }))).toBe("rekey"); // would be put
      expect(decideReconcileAction(input({ activeKid: "k2", heldEndpoint: null }))).toBe("rekey"); // would be resubscribe
    });

    it.each(["Active", "Pending", "Paused", "Whatever"])("%s: rekeys regardless of the status", (serverStatus) => {
      expect(decideReconcileAction(input({ serverStatus, activeKid: "k2" }))).toBe("rekey");
    });

    it("rekeys a Stale row too, with or without a held subscription or a stored key", () => {
      expect(decideReconcileAction(input({ serverStatus: "Stale", activeKid: "k2" }))).toBe("rekey");
      expect(decideReconcileAction(input({ serverStatus: "Stale", activeKid: "k2", heldEndpoint: null }))).toBe("rekey");
      expect(decideReconcileAction(input({ serverStatus: "Stale", activeKid: "k2", canSubscribe: false }))).toBe("rekey");
      expect(decideReconcileAction(input({ serverStatus: "Stale", activeKid: "k2", heldEndpoint: null, canSubscribe: false }))).toBe("rekey");
    });

    it("rekeys when no kid is stored (the held subscription's key is unknown)", () => {
      expect(decideReconcileAction(input({ metaKid: null }))).toBe("rekey");
      expect(decideReconcileAction(input({ metaKid: null, heldEndpoint: null }))).toBe("rekey");
    });

    it("leaves every decision unchanged when the kids match", () => {
      expect(decideReconcileAction(input({}))).toBe("none");
      expect(decideReconcileAction(input({ metaEndpoint: STORED }))).toBe("put");
      expect(decideReconcileAction(input({ heldEndpoint: null }))).toBe("resubscribe");
      expect(decideReconcileAction(input({ heldEndpoint: null, permission: "denied" }))).toBe("repair-required");
      expect(decideReconcileAction(input({ serverStatus: "Stale" }))).toBe("resubscribe");
    });

    it("never rekeys when the server names no active key (unconfigured channel, older server)", () => {
      expect(decideReconcileAction(input({ activeKid: null }))).toBe("none");
      expect(decideReconcileAction(input({ activeKid: "" }))).toBe("none");
      expect(decideReconcileAction(input({ activeKid: null, metaKid: null }))).toBe("none");
      expect(decideReconcileAction(input({ activeKid: "", metaEndpoint: STORED }))).toBe("put");
      expect(decideReconcileAction(input({ activeKid: null, heldEndpoint: null }))).toBe("resubscribe");
      expect(decideReconcileAction(input({ activeKid: "", serverStatus: "Stale" }))).toBe("resubscribe");
    });

    it("falls back to the old decision when it cannot rekey (permission or key missing)", () => {
      // The caller folds "active key sent", "PushManager exists" and "permission granted" into canRekey.
      expect(decideReconcileAction(input({ activeKid: "k2", canRekey: false }))).toBe("none");
      expect(decideReconcileAction(input({ activeKid: "k2", canRekey: false, metaEndpoint: STORED }))).toBe("put");
      expect(decideReconcileAction(input({ activeKid: "k2", canRekey: false, heldEndpoint: null }))).toBe("resubscribe");
      expect(decideReconcileAction(input({ activeKid: "k2", canRekey: false, serverStatus: "Stale" }))).toBe("resubscribe");
      expect(decideReconcileAction(input({ activeKid: "k2", canRekey: false, serverStatus: "Stale", canSubscribe: false }))).toBe("put");
      expect(decideReconcileAction(input({ activeKid: "k2", canRekey: false, permission: "denied", heldEndpoint: null }))).toBe("repair-required");
    });
  });
});
