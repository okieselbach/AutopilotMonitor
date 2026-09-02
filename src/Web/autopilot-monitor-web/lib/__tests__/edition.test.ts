import { describe, expect, it } from "vitest";
import {
  COMMUNITY_DEFAULT,
  editionLabel,
  missingContactProfileParts,
  parseEditionInfo,
  trialDaysLeft,
} from "../edition";

const NOW = new Date("2026-07-07T12:00:00Z");

describe("parseEditionInfo", () => {
  it("parses a full pro-trial payload", () => {
    const info = parseEditionInfo({
      edition: "pro",
      isTrial: true,
      trialExpiresUtc: "2026-07-20T12:00:00Z",
      trialAvailable: false,
      entitlements: {
        retentionCapDays: 365,
        userRateLimitPerMinute: 150,
        delegatedAdminAllowed: true,
        maxDelegatedTenants: 2,
        mcpUsagePlan: "pro",
      },
    });

    expect(info.edition).toBe("pro");
    expect(info.isTrial).toBe(true);
    expect(info.trialExpiresUtc).toBe("2026-07-20T12:00:00Z");
    expect(info.entitlements.retentionCapDays).toBe(365);
    expect(info.entitlements.userRateLimitPerMinute).toBe(150);
    expect(info.entitlements.delegatedAdminAllowed).toBe(true);
    expect(info.entitlements.maxDelegatedTenants).toBe(2);
  });

  it("accepts the pre-rename edition value 'enterprise' as pro (deploy-order safety)", () => {
    const info = parseEditionInfo({ edition: "enterprise" });
    expect(info.edition).toBe("pro");
  });

  it("fails closed to Community for malformed payloads", () => {
    expect(parseEditionInfo(null)).toEqual(COMMUNITY_DEFAULT);
    expect(parseEditionInfo(undefined)).toEqual(COMMUNITY_DEFAULT);
    expect(parseEditionInfo("nope")).toEqual(COMMUNITY_DEFAULT);
    expect(parseEditionInfo({})).toEqual(COMMUNITY_DEFAULT);
  });

  it("treats unknown edition strings as community (fail-closed)", () => {
    const info = parseEditionInfo({ edition: "platinum", trialAvailable: true });
    expect(info.edition).toBe("community");
    expect(info.trialAvailable).toBe(true);
  });

  it("defaults missing entitlements to Community values", () => {
    const info = parseEditionInfo({ edition: "pro" });
    expect(info.entitlements.retentionCapDays).toBe(90);
    expect(info.entitlements.userRateLimitPerMinute).toBeNull();
    expect(info.entitlements.maxDelegatedTenants).toBe(0);
  });

  it("contactEmailSet: only an explicit false means missing (fail-safe against older backends)", () => {
    // Missing field (backend predates it) or malformed → true: never nag on unknown state.
    expect(parseEditionInfo({ edition: "pro" }).contactEmailSet).toBe(true);
    expect(parseEditionInfo({ edition: "pro", contactEmailSet: "no" }).contactEmailSet).toBe(true);
    expect(parseEditionInfo({ edition: "pro", contactEmailSet: true }).contactEmailSet).toBe(true);
    expect(parseEditionInfo({ edition: "pro", contactEmailSet: false }).contactEmailSet).toBe(false);
  });

  it("companyNameSet: same fail-safe contract as contactEmailSet", () => {
    expect(parseEditionInfo({ edition: "pro" }).companyNameSet).toBe(true);
    expect(parseEditionInfo({ edition: "pro", companyNameSet: "no" }).companyNameSet).toBe(true);
    expect(parseEditionInfo({ edition: "pro", companyNameSet: false }).companyNameSet).toBe(false);
  });
});

describe("missingContactProfileParts", () => {
  it("names only explicitly-false parts, in backend order", () => {
    expect(missingContactProfileParts({})).toEqual([]);
    expect(missingContactProfileParts({ contactEmailSet: true, companyNameSet: true })).toEqual([]);
    expect(missingContactProfileParts({ contactEmailSet: false })).toEqual(["contact address"]);
    expect(missingContactProfileParts({ companyNameSet: false })).toEqual(["company name"]);
    expect(missingContactProfileParts({ contactEmailSet: false, companyNameSet: false }))
      .toEqual(["contact address", "company name"]);
  });
});

describe("trialDaysLeft", () => {
  it("returns 0 for unset/expired/invalid values", () => {
    expect(trialDaysLeft(null, NOW)).toBe(0);
    expect(trialDaysLeft(undefined, NOW)).toBe(0);
    expect(trialDaysLeft("2026-07-07T11:59:59Z", NOW)).toBe(0);
    expect(trialDaysLeft("not-a-date", NOW)).toBe(0);
  });

  it("ceils partial days — expiring later today counts as 1", () => {
    expect(trialDaysLeft("2026-07-07T18:00:00Z", NOW)).toBe(1);
  });

  it("counts exact whole days", () => {
    expect(trialDaysLeft("2026-07-10T12:00:00Z", NOW)).toBe(3);
    expect(trialDaysLeft("2026-08-06T12:00:01Z", NOW)).toBe(31);
  });
});

describe("editionLabel", () => {
  it("labels community", () => {
    expect(editionLabel(COMMUNITY_DEFAULT, NOW)).toBe("Community");
  });

  it("labels permanent pro", () => {
    const info = parseEditionInfo({ edition: "pro", isTrial: false });
    expect(editionLabel(info, NOW)).toBe("Pro");
  });

  it("labels a trial with a day countdown (singular/plural)", () => {
    const plural = parseEditionInfo({
      edition: "pro",
      isTrial: true,
      trialExpiresUtc: "2026-07-10T12:00:00Z",
    });
    expect(editionLabel(plural, NOW)).toBe("Pro Trial — 3 days left");

    const singular = parseEditionInfo({
      edition: "pro",
      isTrial: true,
      trialExpiresUtc: "2026-07-07T18:00:00Z",
    });
    expect(editionLabel(singular, NOW)).toBe("Pro Trial — 1 day left");
  });
});
