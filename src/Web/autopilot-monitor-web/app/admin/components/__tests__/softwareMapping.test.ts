import { describe, it, expect } from "vitest";
import type { CpeMappingItem } from "@/lib/generated/wire-types.generated";
import {
  autoResolveReasonText,
  cpeProductKey,
  customMappingEntries,
  failedByReasonLines,
  mappingSavedNotice,
  withProductFlags,
} from "../softwareMapping";

const mapping = (overrides: Partial<CpeMappingItem>): CpeMappingItem => ({
  normalizedVendor: "google",
  normalizedProduct: "chrome",
  cpeVendor: "google",
  cpeProduct: "chrome",
  cpeUri: "cpe:2.3:a:google:chrome",
  category: "browser",
  displayNamePatterns: [],
  publisherPatterns: [],
  excludePatterns: [],
  source: "seed",
  createdAt: "",
  rowKey: "google_chrome",
  selfUpdating: true,
  selfUpdatingDefault: true,
  ...overrides,
});

describe("autoResolveReasonText", () => {
  it("gives every reason of the auto-map endpoint its text", () => {
    expect(autoResolveReasonText("no_exact_product")).toBe("Not an NVD product name");
    expect(autoResolveReasonText("no_cves")).toBe("NVD lists no CVE for this product");
    expect(autoResolveReasonText("unversioned_cves")).toBe(
      "NVD lists CVEs for this product without version information; map it by hand if it should be checked",
    );
    expect(autoResolveReasonText("store_package")).toBe(
      "Store package: its version is the package's, not the product's. Map it by hand if the versions can be compared",
    );
    expect(autoResolveReasonText("nvd_unavailable")).toBe("NVD did not answer; try again later");
    expect(autoResolveReasonText("already_mapped")).toBe("Already mapped");
    expect(autoResolveReasonText("ignored")).toBe("On the ignore list");
    expect(autoResolveReasonText("no_dictionary")).toBe("The NVD product dictionary has not been loaded yet (nightly sync)");
    expect(autoResolveReasonText("empty_name")).toBe("No name");
  });

  it("shows a code it has no text for", () => {
    expect(autoResolveReasonText("not_mapped")).toBe("not_mapped");
    expect(autoResolveReasonText("toString")).toBe("toString");
  });
});

describe("failedByReasonLines", () => {
  it("writes one line per reason, the reasons in the order they came up", () => {
    expect(
      failedByReasonLines([
        { softwareName: "Contoso Reader", reason: "no_exact_product" },
        { softwareName: "Tailspin Photos", reason: "store_package" },
        { softwareName: "Fabrikam Viewer", reason: "no_exact_product" },
        { softwareName: "Northwind Tool", reason: "not_mapped" },
      ]),
    ).toEqual([
      "Not an NVD product name: Contoso Reader, Fabrikam Viewer",
      "Store package: its version is the package's, not the product's. Map it by hand if the versions can be compared: Tailspin Photos",
      "not_mapped: Northwind Tool",
    ]);
  });

  it("leaves the names out where an entry has none, and is empty without a failure", () => {
    expect(failedByReasonLines([{ softwareName: "", reason: "empty_name" }])).toEqual(["No name"]);
    expect(failedByReasonLines([])).toEqual([]);
  });
});

describe("customMappingEntries", () => {
  const rows = [
    mapping({ source: "seed" }),
    mapping({
      source: "custom",
      normalizedVendor: "contoso",
      normalizedProduct: "reader",
      cpeVendor: "contoso",
      cpeProduct: "reader",
      cpeUri: "cpe:2.3:a:contoso:reader",
      category: "custom",
      displayNamePatterns: ["contoso reader"],
      publisherPatterns: ["contoso"],
      excludePatterns: ["reader updater"],
      rowKey: "contoso_reader",
      createdAt: "2026-11-01T10:00:00Z",
      selfUpdating: false,
      selfUpdatingDefault: false,
    }),
    mapping({ source: "auto", normalizedProduct: "auto mapped" }),
    mapping({ source: "community", normalizedProduct: "community mapped" }),
  ];

  it("exports the operator's own mappings only, as plain list entries", () => {
    expect(customMappingEntries(rows)).toEqual([
      {
        normalizedVendor: "contoso",
        normalizedProduct: "reader",
        displayNamePatterns: ["contoso reader"],
        publisherPatterns: ["contoso"],
        excludePatterns: ["reader updater"],
        cpeUri: "cpe:2.3:a:contoso:reader",
        category: "custom",
      },
    ]);
  });

  it("writes nothing that would make the file look like a maintained list", () => {
    const [entry] = customMappingEntries(rows);
    expect(Object.keys(entry)).toEqual([
      "normalizedVendor",
      "normalizedProduct",
      "displayNamePatterns",
      "publisherPatterns",
      "excludePatterns",
      "cpeUri",
      "category",
    ]);
    expect(Array.isArray(customMappingEntries(rows))).toBe(true);
    expect(customMappingEntries(rows.filter((m) => m.source !== "custom"))).toEqual([]);
  });
});

describe("mappingSavedNotice", () => {
  it("adds the inventory rows the mapping changed", () => {
    expect(mappingSavedNotice(12)).toBe("Mapping saved. 12 inventory rows updated.");
    expect(mappingSavedNotice(0)).toBe("Mapping saved. 0 inventory rows updated.");
    expect(mappingSavedNotice(1)).toBe("Mapping saved. 1 inventory row updated.");
  });

  it("says only that it was saved when the response carries no count", () => {
    expect(mappingSavedNotice(undefined)).toBe("Mapping saved.");
    expect(mappingSavedNotice(null)).toBe("Mapping saved.");
  });
});

describe("cpeProductKey", () => {
  it("names the product as the flags endpoint does", () => {
    expect(cpeProductKey({ cpeVendor: "google", cpeProduct: "chrome" })).toBe("google:chrome");
    expect(cpeProductKey({ cpeVendor: "Mozilla", cpeProduct: "Firefox" })).toBe("mozilla:firefox");
  });

  it("is null for a mapping without a CPE product", () => {
    expect(cpeProductKey({ cpeVendor: "", cpeProduct: "chrome" })).toBeNull();
    expect(cpeProductKey({ cpeVendor: "google", cpeProduct: "" })).toBeNull();
  });
});

describe("withProductFlags", () => {
  const rows = [
    mapping({ source: "seed", rowKey: "google_chrome" }),
    mapping({ source: "custom", normalizedProduct: "chrome enterprise", rowKey: "google_chrome_enterprise" }),
    mapping({
      normalizedVendor: "mozilla",
      normalizedProduct: "firefox",
      cpeVendor: "mozilla",
      cpeProduct: "firefox",
      cpeUri: "cpe:2.3:a:mozilla:firefox",
      rowKey: "mozilla_firefox",
    }),
  ];

  it("changes every mapping of the product together and no other", () => {
    const updated = withProductFlags(rows, "google:chrome", { selfUpdating: false, selfUpdatingDefault: true });
    expect(updated.map((m) => m.selfUpdating)).toEqual([false, false, true]);
    expect(updated.map((m) => m.selfUpdatingDefault)).toEqual([true, true, true]);
    // The mapping of the other product is the same object; the input is left as it was.
    expect(updated[2]).toBe(rows[2]);
    expect(rows[0].selfUpdating).toBe(true);
  });

  it("takes the default the backend reports along", () => {
    const updated = withProductFlags(rows, "mozilla:firefox", { selfUpdating: true, selfUpdatingDefault: false });
    expect(updated[2]).toMatchObject({ selfUpdating: true, selfUpdatingDefault: false });
    expect(updated[0]).toBe(rows[0]);
  });
});
