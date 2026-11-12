import { describe, it, expect } from "vitest";
import { reseedNotice } from "../reseedResult";

const SUCCESS = "GitHub Reseed complete: Gather (1 deleted, 2 written)";
const inventory = { rows: 4210, set: 12, cleared: 3, failed: 0, productsNoted: 5 };

describe("reseedNotice", () => {
  it("shows the caller's success wording for an answer that succeeded", () => {
    expect(reseedNotice({ success: true, message: "Reseed from GitHub complete" }, SUCCESS)).toEqual({ ok: true, text: SUCCESS });
  });

  it("adds what the reseed changed on the inventory rows", () => {
    expect(reseedNotice({ success: true, message: "", cpeInventory: inventory }, SUCCESS).text).toBe(
      `${SUCCESS}\nInventory rows: 12 set, 3 cleared (4210 checked)`,
    );
    expect(reseedNotice({ success: true, message: "", cpeInventory: { ...inventory, failed: 2 } }, SUCCESS).text).toBe(
      `${SUCCESS}\nInventory rows: 12 set, 3 cleared (4210 checked), 2 failed`,
    );
  });

  it("never reads as a success when the answer says it did not succeed", () => {
    const notice = reseedNotice(
      {
        success: false,
        message: "Reseed from GitHub complete, except for the CPE mappings: seed list: not valid JSON",
        cpeProblems: ["community list: 3 entries without a CPE", "seed list: not valid JSON"],
      },
      SUCCESS,
    );
    expect(notice.ok).toBe(false);
    expect(notice.text.split("\n")).toEqual([
      "Reseed from GitHub complete, except for the CPE mappings: seed list: not valid JSON",
      "community list: 3 entries without a CPE",
      "seed list: not valid JSON",
    ]);
    expect(notice.text).not.toContain(SUCCESS);
  });

  it("names the inventory rows on a failed answer too", () => {
    const notice = reseedNotice(
      { success: false, message: "Refused", cpeProblems: ["seed list: lost more than half of its mappings"], cpeInventory: { ...inventory, failed: 1 } },
      SUCCESS,
    );
    expect(notice.text.split("\n")).toEqual([
      "Refused",
      "seed list: lost more than half of its mappings",
      "Inventory rows: 12 set, 3 cleared (4210 checked), 1 failed",
    ]);
  });

  it("still says that it failed when the answer carries no message and no problem", () => {
    expect(reseedNotice({ success: false, message: "" }, SUCCESS)).toEqual({ ok: false, text: "Reseed from GitHub failed" });
  });
});
