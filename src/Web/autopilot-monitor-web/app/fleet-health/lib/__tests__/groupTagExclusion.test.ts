import { describe, expect, it } from "vitest";
import {
  MAX_EXCLUDED_GROUP_TAGS,
  NO_GROUP_TAG_EXCLUSION,
  describeGroupTagExclusion,
  isGroupTagExclusionActive,
  parseGroupTagExclusion,
  toggleExcludedGroupTag,
  writeGroupTagExclusion,
} from "../groupTagExclusion";

describe("parseGroupTagExclusion", () => {
  it("trims, drops empties and de-duplicates without case, keeping the first spelling", () => {
    expect(parseGroupTagExclusion(" TEST , test,,LAB ", "TRUE")).toEqual({ tags: ["TEST", "LAB"], untagged: true });
  });

  it("reads missing parameters as no filter", () => {
    expect(parseGroupTagExclusion(null, undefined)).toEqual(NO_GROUP_TAG_EXCLUSION);
    expect(isGroupTagExclusionActive(parseGroupTagExclusion(null, "false"))).toBe(false);
  });

  it("caps the list like the backend does", () => {
    const many = Array.from({ length: MAX_EXCLUDED_GROUP_TAGS + 5 }, (_, i) => `T${i}`).join(",");
    expect(parseGroupTagExclusion(many, null).tags).toHaveLength(MAX_EXCLUDED_GROUP_TAGS);
  });
});

describe("toggleExcludedGroupTag", () => {
  it("hides a tag, then shows it again regardless of case", () => {
    const hidden = toggleExcludedGroupTag(NO_GROUP_TAG_EXCLUSION, "TEST");
    expect(hidden.tags).toEqual(["TEST"]);
    expect(toggleExcludedGroupTag(hidden, "test").tags).toEqual([]);
  });

  it("does not hide beyond the cap", () => {
    const full = { tags: Array.from({ length: MAX_EXCLUDED_GROUP_TAGS }, (_, i) => `T${i}`), untagged: false };
    expect(toggleExcludedGroupTag(full, "ONE-MORE")).toBe(full);
  });
});

describe("writeGroupTagExclusion", () => {
  it("writes both parameters and removes them when the filter is empty, keeping the others", () => {
    const params = new URLSearchParams("days=30");
    writeGroupTagExclusion(params, { tags: ["TEST", "LAB"], untagged: true });
    expect(params.toString()).toBe("days=30&excludeGroupTags=TEST%2CLAB&excludeUntagged=true");
    writeGroupTagExclusion(params, NO_GROUP_TAG_EXCLUSION);
    expect(params.toString()).toBe("days=30");
  });
});

describe("describeGroupTagExclusion", () => {
  it.each([
    [{ tags: ["TEST"], untagged: false }, "group tag TEST"],
    [{ tags: ["TEST", "LAB"], untagged: false }, "group tags TEST, LAB"],
    [{ tags: [], untagged: true }, "sessions without a group tag"],
    [{ tags: ["TEST"], untagged: true }, "group tag TEST and sessions without one"],
  ])("%j → %s", (exclusion, expected) => {
    expect(describeGroupTagExclusion(exclusion)).toBe(expected);
  });
});
