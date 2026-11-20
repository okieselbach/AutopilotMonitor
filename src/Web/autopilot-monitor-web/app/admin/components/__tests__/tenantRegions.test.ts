import { describe, it, expect } from "vitest";
import { KNOWN_REGIONS, regionCode, regionFacetValue, regionOverview, regionTitle } from "../tenantRegions";

describe("regionCode / regionTitle", () => {
  it("shows Microsoft's codes on the pill and the plain words in its tooltip", () => {
    expect(regionCode({ tenantRegionScope: "EU" })).toBe("EU");
    expect(regionTitle({ tenantRegionScope: "EU" })).toBe("Entra tenant region: Europe");
  });

  it("adds the sub-scope of a government community tenant", () => {
    const gcc = { tenantRegionScope: "NA", tenantRegionSubScope: "GCC" };
    expect(regionCode(gcc)).toBe("NA · GCC");
    expect(regionTitle(gcc)).toBe("Entra tenant region: North America (US Government Community Cloud)");
  });

  it("shows a code it does not know as Microsoft sent it", () => {
    expect(regionCode({ tenantRegionScope: "JPR" })).toBe("JPR");
    expect(regionTitle({ tenantRegionScope: "JPR" })).toBe("Entra tenant region: JPR");
  });

  it("has nothing to show before the region is looked up", () => {
    expect(regionCode({})).toBeNull();
    expect(regionTitle({ tenantRegionScope: null })).toBeNull();
    expect(regionFacetValue({ tenantRegionScope: "" })).toBe("none");
  });
});

describe("KNOWN_REGIONS", () => {
  it("places every continent on the map and Worldwide nowhere", () => {
    const placed = KNOWN_REGIONS.filter((r) => r.anchor).map((r) => r.code);
    expect(placed).toEqual(["EU", "NA", "SA", "AS", "OC", "AF"]);
    for (const r of KNOWN_REGIONS) {
      if (!r.anchor) continue;
      const [lat, lon] = r.anchor;
      expect(Math.abs(lat), r.code).toBeLessThanOrEqual(85);
      expect(Math.abs(lon), r.code).toBeLessThanOrEqual(180);
    }
  });
});

describe("regionOverview", () => {
  it("counts per region, the largest first, a sub-scope inside its region and the unresolved apart", () => {
    const o = regionOverview([
      { tenantRegionScope: "NA" },
      { tenantRegionScope: "EU" },
      { tenantRegionScope: "eu" },
      { tenantRegionScope: "NA", tenantRegionSubScope: "GCC" },
      { tenantRegionScope: "EU" },
      { tenantRegionScope: "WW" },
      { tenantRegionScope: "JPR" },
      { tenantRegionScope: null },
      {},
    ]);
    expect(o.regions.map((r) => [r.code, r.label, r.count])).toEqual([
      ["EU", "Europe", 3],
      ["NA", "North America", 2],
      ["WW", "Worldwide", 1],
      ["JPR", "JPR", 1],
    ]);
    expect(o.regions.find((r) => r.code === "EU")?.anchor).toEqual([50, 12]);
    expect(o.regions.find((r) => r.code === "JPR")?.anchor).toBeUndefined();
    expect(o.unresolved).toBe(2);
  });

  it("is empty for no tenants", () => {
    expect(regionOverview([])).toEqual({ regions: [], unresolved: 0 });
  });
});
