import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { LocationTableNote, LOCATION_TABLE_NOTE } from "../LocationTableNote";
import { DOCS_PATHS } from "@/lib/docsPaths";
import { DOCS_URL } from "@/utils/config";

describe("LocationTableNote", () => {
  const html = renderToStaticMarkup(createElement(LocationTableNote));

  it("states the population behind the duration columns and the basis of vs Global", () => {
    expect(LOCATION_TABLE_NOTE.duration).toMatch(/succeeded/i);
    expect(LOCATION_TABLE_NOTE.duration).toMatch(/P95/);
    expect(LOCATION_TABLE_NOTE.vsGlobal).toMatch(/Avg Duration/);
    expect(LOCATION_TABLE_NOTE.vsGlobal).toMatch(/own fleet/i);
    expect(LOCATION_TABLE_NOTE.vsGlobal).toMatch(/positive = slower/i);
    expect(html).toContain(LOCATION_TABLE_NOTE.duration);
    expect(html).toContain(LOCATION_TABLE_NOTE.vsGlobal);
  });

  it("links the calculation section of the geo guide and the statistics concept page", () => {
    expect(html).toContain(`href="${DOCS_URL}${DOCS_PATHS.geographicPerformanceNumbers}"`);
    expect(html).toContain(`href="${DOCS_URL}${DOCS_PATHS.statistics}"`);
    expect(DOCS_PATHS.geographicPerformanceNumbers).toMatch(/#how-the-numbers-are-calculated$/);
    expect(DOCS_PATHS.statistics).toBe("/concepts/averages-medians-and-percentiles");
  });
});
