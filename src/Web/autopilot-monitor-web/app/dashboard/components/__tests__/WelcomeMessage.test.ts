import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { WelcomeMessage } from "../WelcomeMessage";
import { DOCS_URL } from "@/lib/config";
import { DOCS_PATHS } from "@/lib/docsPaths";
import { GITHUB_REPO_URL } from "@/lib/githubStars";

describe("WelcomeMessage", () => {
  it("links the setup guide, the deploy guide, the docs and the repository in a new tab", () => {
    const html = renderToStaticMarkup(createElement(WelcomeMessage));
    for (const href of [
      `${DOCS_URL}${DOCS_PATHS.portalSetup}`,
      `${DOCS_URL}${DOCS_PATHS.deployTheAgent}`,
      DOCS_URL,
      GITHUB_REPO_URL,
    ]) {
      expect(html).toContain(`href="${href}"`);
    }
    expect(html.match(/target="_blank"/g)).toHaveLength(4);
  });

  // The growth report reads welcome_link_clicked as the step between signup and the first
  // enrollment; a link without its own id would drop out of that funnel unnoticed.
  it("tracks every link with its own id", () => {
    const source = readFileSync(join(__dirname, "..", "WelcomeMessage.tsx"), "utf-8");
    const ids = [...source.matchAll(/trackLink\("([a-z_]+)"\)/g)].map(m => m[1]);
    expect(source.match(/<a\b/g)).toHaveLength(ids.length);
    expect(ids).toEqual(["setup_guide", "deploy_agent", "docs", "github"]);
  });
});
