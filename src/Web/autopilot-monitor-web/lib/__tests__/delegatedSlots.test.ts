import { describe, expect, it } from "vitest";
import { DELEGATED_SLOT_LIMIT_REACHED, nextSlotLimit, parseSlotLimitError, slotTenantLabel } from "../delegatedSlots";

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
