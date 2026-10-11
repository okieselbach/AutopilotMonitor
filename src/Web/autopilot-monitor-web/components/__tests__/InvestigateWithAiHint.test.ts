import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { InvestigateWithAiHint } from "../InvestigateWithAiHint";

const SESSION = "3f9c2d71-5b8e-4c0a-9e14-6d2b7a815c40";

const render = (props: Partial<Parameters<typeof InvestigateWithAiHint>[0]> = {}) =>
  renderToStaticMarkup(
    createElement(InvestigateWithAiHint, {
      sessionId: SESSION,
      mcpAccess: true,
      canDryRun: true,
      canReport: true,
      source: "analysis",
      ...props,
    }),
  );

describe("InvestigateWithAiHint", () => {
  it("offers the debug-session prompt for this session, the full prompt and the client setup", () => {
    const html = render();
    expect(html).toContain("No analyze rule flagged this failure.");
    expect(html).toContain("debug-session");
    expect(html).toContain(SESSION);
    expect(html).toContain("Copy full prompt");
    expect(html).toContain('href="/settings/tenant/ai-integration"');
  });

  it("without MCP access it points to the timeline and names no AI step", () => {
    const html = render({ mcpAccess: false });
    expect(html).toContain("No analyze rule flagged this failure.");
    expect(html).toContain("Check the event timeline");
    expect(html).not.toContain("debug-session");
    expect(html).not.toContain("Copy full prompt");
  });

  it("names Report Session only for roles that have the button", () => {
    expect(render({ canReport: true })).toContain("Report Session");
    expect(render({ canReport: false })).not.toContain("Report Session");
  });

  it("stays in the neutral gray/sky family of the session view", () => {
    expect(render()).not.toMatch(/(bg|text|border)-(amber|red|green|violet|indigo)-/);
  });
});
