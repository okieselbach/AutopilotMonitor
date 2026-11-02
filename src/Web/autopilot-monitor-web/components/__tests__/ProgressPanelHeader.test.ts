import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import ProgressPanelHeader from "../ProgressPanelHeader";

const noop = () => {};

function render(expanded: boolean) {
  return renderToStaticMarkup(
    createElement(
      ProgressPanelHeader,
      { title: "Install Progress", icon: createElement("svg"), expanded, onToggle: noop },
      createElement("button", null, "3 skipped"),
    ),
  );
}

// Walks the markup and reports whether a button ever opens while another one is still open.
function hasNestedButton(html: string): boolean {
  let depth = 0;
  for (const [tag] of html.matchAll(/<\/?button\b/g)) {
    depth += tag === "<button" ? 1 : -1;
    if (depth > 1) return true;
  }
  return false;
}

describe("ProgressPanelHeader", () => {
  it("keeps the pill buttons and the panel toggle as siblings, never nested", () => {
    const html = render(true);
    expect(html.match(/<button\b/g)?.length).toBe(2);
    expect(hasNestedButton(html)).toBe(false);
  });

  it("names the toggle after the panel and reports its state", () => {
    expect(render(true)).toContain('aria-expanded="true" aria-label="Install Progress"');
    expect(render(false)).toContain('aria-expanded="false" aria-label="Install Progress"');
  });

  it("detects a nested button", () => {
    expect(hasNestedButton("<button><span><button></button></span></button>")).toBe(true);
  });
});
