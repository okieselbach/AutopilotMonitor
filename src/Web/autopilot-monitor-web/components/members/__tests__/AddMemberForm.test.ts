import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { AddMemberForm } from "../AddMemberForm";

const noop = () => {};

function inputClass(stacked?: boolean): string {
  const html = renderToStaticMarkup(
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
  const input = html.match(/<input [^>]*name="new-member"[^>]*>/)?.[0] ?? "";
  return input.match(/class="([^"]*)"/)?.[1] ?? "";
}

describe("AddMemberForm", () => {
  it("shares a row with the type switch, role and button from the sm breakpoint by default", () => {
    expect(inputClass().split(" ")).toEqual(expect.arrayContaining(["w-full", "sm:w-auto", "sm:flex-1"]));
  });

  it("gives the input a line of its own when stacked (narrow hosts like the GA tenant editor)", () => {
    const classes = inputClass(true).split(" ");
    expect(classes).toContain("w-full");
    expect(classes).not.toContain("sm:w-auto");
    expect(classes).not.toContain("sm:flex-1");
  });
});
