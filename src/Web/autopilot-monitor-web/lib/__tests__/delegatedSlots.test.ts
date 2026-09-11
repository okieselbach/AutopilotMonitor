import { describe, expect, it } from "vitest";
import { DELEGATED_SLOT_LIMIT_REACHED, nextSlotLimit, parseSlotLimitError, slotBreakdown, slotTenantLabel } from "../delegatedSlots";

describe("parseSlotLimitError", () => {
  const body = {
    error: "Delegated tenant slot limit reached for partner.example: 2 of 2 slot(s) in use, 1 more needed.",
    code: DELEGATED_SLOT_LIMIT_REACHED,
    homeTenantId: "99999999-9999-9999-9999-999999999999",
    homeTenantDomain: "partner.example",
    used: 2,
    limit: 2,
    required: 1,
  };

  it("parses the 409 conflict body", () => {
    const e = parseSlotLimitError(409, body);
    expect(e).not.toBeNull();
    expect(e!.homeTenantDomain).toBe("partner.example");
    expect(e!.used).toBe(2);
    expect(e!.limit).toBe(2);
    expect(e!.required).toBe(1);
  });

  it("ignores other statuses, other 409 codes and malformed bodies", () => {
    expect(parseSlotLimitError(422, body)).toBeNull();
    expect(parseSlotLimitError(409, { error: "UPN already bound elsewhere" })).toBeNull();
    expect(parseSlotLimitError(409, { code: DELEGATED_SLOT_LIMIT_REACHED })).toBeNull();
    expect(parseSlotLimitError(409, null)).toBeNull();
    expect(parseSlotLimitError(409, "nope")).toBeNull();
  });

  it("treats an absent domain as null and falls back to the id for the label", () => {
    const e = parseSlotLimitError(409, { ...body, homeTenantDomain: undefined })!;
    expect(e.homeTenantDomain).toBeNull();
    expect(slotTenantLabel(e)).toBe(body.homeTenantId);
    expect(slotTenantLabel(parseSlotLimitError(409, body)!)).toBe("partner.example");
  });
});

describe("nextSlotLimit", () => {
  it("is the smallest limit that fits the rejected mutation", () => {
    expect(nextSlotLimit({ used: 2, limit: 2, required: 1 })).toBe(3);
    expect(nextSlotLimit({ used: 2, limit: 2, required: 3 })).toBe(5);
    // A stale limit above used+required is never lowered.
    expect(nextSlotLimit({ used: 1, limit: 10, required: 1 })).toBe(10);
  });
});

describe("slotBreakdown", () => {
  it("splits a grown limit into the plan base and the purchased slots", () => {
    // Pro account window 1,000 + 3 slots × 300; Pro tenant month 60,000 + 1 slot × 18,000.
    expect(slotBreakdown(1900, 3, 300)).toEqual({ base: 1000, slots: 3, perSlot: 300 });
    expect(slotBreakdown(78000, 1, 18000)).toEqual({ base: 60000, slots: 1, perSlot: 18000 });
  });

  it("is null without a purchased slot, without growth per slot, for an unlimited window and when the figures do not add up", () => {
    expect(slotBreakdown(1000, 0, 300)).toBeNull();
    expect(slotBreakdown(1000, undefined, 300)).toBeNull();
    expect(slotBreakdown(1000, 3, 0)).toBeNull();
    expect(slotBreakdown(1000, 3, undefined)).toBeNull();
    expect(slotBreakdown(0, 3, 300)).toBeNull();
    expect(slotBreakdown(500, 3, 300)).toBeNull();
  });
});
