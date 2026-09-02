import { describe, expect, it } from "vitest";
import {
  buildInviteLink,
  delegationAcceptPath,
  describeDelegationError,
  holdRemainingLabel,
  invitationStatusLabel,
} from "../delegations";

describe("invitation links", () => {
  it("builds a query-string link (static export: never a path segment) with the token encoded", () => {
    expect(delegationAcceptPath("ab+c/d=")).toBe("/delegations/accept?token=ab%2Bc%2Fd%3D");
    expect(buildInviteLink("https://portal.example/", "tok")).toBe("https://portal.example/delegations/accept?token=tok");
    expect(buildInviteLink("https://portal.example", "tok")).toBe("https://portal.example/delegations/accept?token=tok");
  });
});

describe("labels", () => {
  it("maps wire statuses to labels and keeps unknown values", () => {
    expect(invitationStatusLabel("Released")).toBe("Removed");
    expect(invitationStatusLabel("Expired")).toBe("Expired");
    expect(invitationStatusLabel("Weird")).toBe("Weird");
  });

  it("counts down a release hold in hours and goes silent once it lapsed", () => {
    const now = new Date("2026-09-02T12:00:00Z");
    expect(holdRemainingLabel("2026-09-03T11:30:00Z", now)).toBe("slot held for 24 h");
    expect(holdRemainingLabel("2026-09-02T12:30:00Z", now)).toBe("slot held for less than an hour");
    expect(holdRemainingLabel("2026-09-02T11:00:00Z", now)).toBe("");
    expect(holdRemainingLabel(null, now)).toBe("");
    expect(holdRemainingLabel("not-a-date", now)).toBe("");
  });
});

describe("describeDelegationError", () => {
  it("explains every accept-chain code and falls back to the server message", () => {
    for (const code of [
      "InvalidInvitation", "InvitationExpired", "InvitationAlreadyUsed", "InvitationCancelled",
      "CannotAcceptOwnInvitation", "AlreadyManaged", "ManagerNotEntitled", "DelegatedSlotLimitReached",
    ]) {
      expect(describeDelegationError(code, "fallback")).not.toBe("fallback");
    }
    expect(describeDelegationError("SomethingElse", "server said so")).toBe("server said so");
    expect(describeDelegationError(undefined, "server said so")).toBe("server said so");
  });
});
