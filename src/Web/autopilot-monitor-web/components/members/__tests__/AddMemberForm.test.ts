import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { AddMemberForm } from "../AddMemberForm";

const noop = () => {};

function render(stacked?: boolean): string {
  return renderToStaticMarkup(
    createElement(AddMemberForm, {
      value: "",
      onValueChange: noop,
      role: "Admin",
      onRoleChange: noop,
      kind: "user",
      onKindChange: noop,
      adding: false,
      onAdd: noop,
      stacked,
    }),
  );
}

function inputClasses(html: string): string[] {
  const input = html.match(/<input [^>]*name="new-member"[^>]*>/)?.[0] ?? "";
  return (input.match(/class="([^"]*)"/)?.[1] ?? "").split(" ");
}

describe("AddMemberForm", () => {
  it("keeps a minimum width for the input and wraps instead of squeezing it, whatever the viewport", () => {
    const classes = inputClasses(render());
    expect(classes).toEqual(expect.arrayContaining(["grow", "basis-72", "min-w-0"]));
    // No viewport breakpoint decides the layout: a wide window can still host a narrow column.
    expect(classes.filter((c) => /^(sm|md|lg|xl):/.test(c))).toEqual([]);
  });

  it("gives the input a line of its own when stacked (the GA tenant editor modal)", () => {
    const classes = inputClasses(render(true));
    expect(classes).toContain("basis-full");
    expect(classes).not.toContain("basis-72");
  });

  it("keeps role and Add together, so they wrap as one unit", () => {
    const html = render();
    expect(html).toMatch(/<div class="flex gap-2"><select [^>]*aria-label="Role"[\s\S]*?<\/select><button [^>]*>[\s\S]*?Add<\/button><\/div>/);
  });
});
