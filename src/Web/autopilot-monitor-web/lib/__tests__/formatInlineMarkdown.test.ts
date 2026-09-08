import { describe, expect, it } from "vitest";
import React from "react";
import { formatInlineMarkdown } from "../formatInlineMarkdown";

function tag(node: React.ReactNode): string | null {
  return React.isValidElement(node) ? String(node.type) : null;
}
function props(node: React.ReactNode): Record<string, unknown> {
  return React.isValidElement(node) ? (node.props as Record<string, unknown>) : {};
}

describe("formatInlineMarkdown", () => {
  it("renders bold and code, leaves links as text by default (rule explanations stay unchanged)", () => {
    const out = formatInlineMarkdown("A **b** `c` [d](https://e)");
    expect(out.map(tag)).toEqual([null, "strong", null, "code", null]);
    expect(out[4]).toBe(" [d](https://e)");
  });

  it("renders https links as new-tab anchors when opted in", () => {
    const out = formatInlineMarkdown("See [Docs](https://docs.example/p#a) now", { links: true, linkClassName: "x" });
    expect(out.map(tag)).toEqual([null, "a", null]);
    expect(props(out[1])).toMatchObject({ href: "https://docs.example/p#a", target: "_blank", rel: "noopener noreferrer", className: "x", children: "Docs" });
  });

  it("degrades non-http link targets to their label", () => {
    const out = formatInlineMarkdown("[x](javascript:void) and [y](mailto:a@b)", { links: true });
    expect(out.map(tag)).toEqual([null, null, null, null, null]);
    expect(out.join("")).toBe("x and y");
  });

  it("mixes links with bold and code", () => {
    const out = formatInlineMarkdown("**T** — `--flag` then [L](https://l)", { links: true });
    expect(out.map(tag)).toEqual([null, "strong", null, "code", null, "a", null]);
  });

  it("renders single-asterisk emphasis only when opted in, never inside bold", () => {
    expect(formatInlineMarkdown("mapped to *Soft reboot* or **Hard**").map(tag)).toEqual([null, "strong", null]);
    const out = formatInlineMarkdown("mapped to *Soft reboot* or **Hard**", { emphasis: true });
    expect(out.map(tag)).toEqual([null, "em", null, "strong", null]);
    expect(props(out[1]).children).toBe("Soft reboot");
    expect(formatInlineMarkdown("a * b * c", { emphasis: true }).map(tag)).toEqual([null]);
  });
});
