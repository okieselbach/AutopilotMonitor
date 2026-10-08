import { describe, expect, it } from "vitest";
import { HISTORY_MAX_ENTRIES, HISTORY_RETENTION_MAX_DAYS } from "../push/pushCore";
import { describeRetention, parseRetentionInput } from "../push/retentionInput";

/**
 * The status page's retention field: what the person types becomes an integer 0–365 or one of
 * two messages, and the stored value reads back as "30 days" / "1 day" / "Until the 200-entry cap".
 */
describe("retention input", () => {
  const rangeMessage = "Enter a whole number of days between 0 and 365.";

  it("accepts the boundaries 0 and 365", () => {
    expect(parseRetentionInput("0")).toEqual({ ok: true, days: 0 });
    expect(parseRetentionInput("365")).toEqual({ ok: true, days: 365 });
    expect(parseRetentionInput(String(HISTORY_RETENTION_MAX_DAYS))).toEqual({ ok: true, days: HISTORY_RETENTION_MAX_DAYS });
  });

  it("asks for a number when the field is blank", () => {
    expect(parseRetentionInput("")).toEqual({ ok: false, message: "Enter a number of days." });
    expect(parseRetentionInput("   ")).toEqual({ ok: false, message: "Enter a number of days." });
  });

  it("rejects anything but a whole number", () => {
    expect(parseRetentionInput("abc")).toEqual({ ok: false, message: rangeMessage });
    expect(parseRetentionInput("1.5")).toEqual({ ok: false, message: rangeMessage });
    expect(parseRetentionInput("1,5")).toEqual({ ok: false, message: rangeMessage });
    expect(parseRetentionInput("+5")).toEqual({ ok: false, message: rangeMessage });
    expect(parseRetentionInput("5 days")).toEqual({ ok: false, message: rangeMessage });
  });

  it("rejects values outside 0 to 365", () => {
    expect(parseRetentionInput("-1")).toEqual({ ok: false, message: rangeMessage });
    expect(parseRetentionInput("366")).toEqual({ ok: false, message: rangeMessage });
    expect(parseRetentionInput("99999999999999999999")).toEqual({ ok: false, message: rangeMessage });
  });

  it("ignores surrounding whitespace and leading zeros", () => {
    expect(parseRetentionInput("  30  ")).toEqual({ ok: true, days: 30 });
    expect(parseRetentionInput("\t7\n")).toEqual({ ok: true, days: 7 });
    expect(parseRetentionInput("007")).toEqual({ ok: true, days: 7 });
  });

  it("describes the stored value in words", () => {
    expect(describeRetention(30)).toBe("30 days");
    expect(describeRetention(1)).toBe("1 day");
    expect(describeRetention(0)).toBe("Until the 200-entry cap");
    expect(describeRetention(0)).toBe(`Until the ${HISTORY_MAX_ENTRIES}-entry cap`);
  });
});
