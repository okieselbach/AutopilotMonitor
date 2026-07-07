import { describe, expect, it } from "vitest";
import {
  COMMUNITY_DEFAULT,
  editionLabel,
  parseEditionInfo,
  trialDaysLeft,
} from "../edition";

const NOW = new Date("2026-07-07T12:00:00Z");

describe("parseEditionInfo", () => {
  it("parses a full enterprise-trial payload", () => {
    const info = parseEditionInfo({
      edition: "enterprise",
      isTrial: true,
      trialExpiresUtc: "2026-07-20T12:00:00Z",
      trialAvailable: false,
      entitlements: {
        retentionCapDays: 365,
        userRateLimitPerMinute: 150,
        delegatedAdminAllowed: true,
        mcpUsagePlan: "enterprise",
      },
    });

    expect(info.edition).toBe("enterprise");
    expect(info.isTrial).toBe(true);
    expect(info.trialExpiresUtc).toBe("2026-07-20T12:00:00Z");
    expect(info.entitlements.retentionCapDays).toBe(365);
    expect(info.entitlements.userRateLimitPerMinute).toBe(150);
    expect(info.entitlements.delegatedAdminAllowed).toBe(true);
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
    const info = parseEditionInfo({ edition: "enterprise" });
    expect(info.entitlements.retentionCapDays).toBe(90);
    expect(info.entitlements.userRateLimitPerMinute).toBeNull();
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

  it("labels permanent enterprise", () => {
    const info = parseEditionInfo({ edition: "enterprise", isTrial: false });
    expect(editionLabel(info, NOW)).toBe("Enterprise");
  });

  it("labels a trial with a day countdown (singular/plural)", () => {
    const plural = parseEditionInfo({
      edition: "enterprise",
      isTrial: true,
      trialExpiresUtc: "2026-07-10T12:00:00Z",
    });
    expect(editionLabel(plural, NOW)).toBe("Enterprise Trial — 3 days left");

    const singular = parseEditionInfo({
      edition: "enterprise",
      isTrial: true,
      trialExpiresUtc: "2026-07-07T18:00:00Z",
    });
    expect(editionLabel(singular, NOW)).toBe("Enterprise Trial — 1 day left");
  });
});
