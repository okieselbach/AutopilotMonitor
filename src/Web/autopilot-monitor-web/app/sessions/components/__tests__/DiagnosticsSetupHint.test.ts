import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { DiagnosticsSetupHint } from "../DiagnosticsSetupHint";

const render = (canConfigure: boolean) => renderToStaticMarkup(createElement(DiagnosticsSetupHint, { canConfigure }));

describe("DiagnosticsSetupHint", () => {
  it("says why the package is missing and what On Failure Only would keep", () => {
    const html = render(true);
    expect(html).toContain("No diagnostics package for this failed enrollment");
    expect(html).toContain("On Failure Only");
    expect(html).toContain("IME and agent logs");
  });

  it("links an admin to the diagnostics settings, never to a one-click switch", () => {
    const html = render(true);
    expect(html).toContain('href="/settings/agent/diagnostics"');
    expect(html).toContain("Open diagnostics settings");
  });

  it("tells everyone else who can turn it on", () => {
    const html = render(false);
    expect(html).not.toContain("href=");
    expect(html).toContain("A Tenant Admin can turn it on under Settings → Agent → Diagnostics Package.");
  });

  it("uses the sky info family of the session view's hints", () => {
    const html = render(true);
    expect(html).toContain("bg-sky-50");
    expect(html).not.toMatch(/bg-(amber|red|green)-/);
  });
});
