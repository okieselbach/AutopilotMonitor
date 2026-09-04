import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { MapLegend } from "../MapLegend";
import { MAP_COLOR_MODES, MAP_LEGEND_NOTE, NO_BUCKET_FILTER, toggleBucketFilter } from "../mapColorModes";

const noop = () => {};

function render(mode: (typeof MAP_COLOR_MODES)[number], filter = NO_BUCKET_FILTER) {
  return renderToStaticMarkup(createElement(MapLegend, { mode, filter, onFilterChange: noop }));
}

describe("MapLegend", () => {
  it("renders every bucket label and hex of the active mode plus the size note", () => {
    for (const mode of MAP_COLOR_MODES) {
      const html = render(mode);
      expect(html).toContain(mode.label);
      for (const b of mode.buckets) {
        expect(html).toContain(b.label.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;"));
        expect(html).toContain(`background-color:${b.hex}`);
      }
      expect(html).toContain(MAP_LEGEND_NOTE);
    }
  });

  it("does not render buckets of another mode", () => {
    const [first, second] = MAP_COLOR_MODES;
    const html = render(first);
    for (const b of second.buckets) {
      if (!first.buckets.some((fb) => fb.label === b.label)) expect(html).not.toContain(b.label);
    }
  });

  it("renders every bucket as an unpressed toggle and no reset link without a filter", () => {
    const [mode] = MAP_COLOR_MODES;
    const html = render(mode);
    expect(html.match(/<button /g)?.length).toBe(mode.buckets.length);
    expect(html).not.toContain('aria-pressed="true"');
    expect(html).not.toContain("opacity-40");
    expect(html).not.toContain("Show all");
  });

  it("with a filter, presses the selected bucket, dims the others and offers Show all", () => {
    const [mode] = MAP_COLOR_MODES;
    const html = render(mode, toggleBucketFilter(NO_BUCKET_FILTER, mode.buckets[1]));
    expect(html.match(/aria-pressed="true"/g)?.length).toBe(1);
    expect(html.match(/opacity-40/g)?.length).toBe(mode.buckets.length - 1);
    expect(html).toContain("Show all");
    // The pressed button is the one carrying the selected bucket's label.
    const pressed = html.split("<button ").find((chunk) => chunk.includes('aria-pressed="true"'));
    expect(pressed).toContain(mode.buckets[1].label);
    expect(pressed).not.toContain("opacity-40");
  });
});
