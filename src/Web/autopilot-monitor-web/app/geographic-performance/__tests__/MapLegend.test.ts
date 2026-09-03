import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { MapLegend } from "../MapLegend";
import { MAP_COLOR_MODES, MAP_LEGEND_NOTE } from "../mapColorModes";

describe("MapLegend", () => {
  it("renders every bucket label and hex of the active mode plus the size note", () => {
    for (const mode of MAP_COLOR_MODES) {
      const html = renderToStaticMarkup(createElement(MapLegend, { mode }));
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
    const html = renderToStaticMarkup(createElement(MapLegend, { mode: first }));
    for (const b of second.buckets) {
      if (!first.buckets.some((fb) => fb.label === b.label)) expect(html).not.toContain(b.label);
    }
  });
});
