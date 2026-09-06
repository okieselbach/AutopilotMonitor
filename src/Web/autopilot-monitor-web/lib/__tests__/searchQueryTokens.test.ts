import { describe, expect, it } from "vitest";
import { literalText, tokenizeSearchQuery } from "../searchQueryTokens";

describe("tokenizeSearchQuery", () => {
  it("splits on whitespace and keeps the original case", () => {
    expect(tokenizeSearchQuery("  ESP  Apps ")).toEqual([
      { text: "ESP", negated: false },
      { text: "Apps", negated: false },
    ]);
  });

  it("reads a leading minus as negation, a quoted minus as text", () => {
    expect(tokenizeSearchQuery('-perf "-1" -"exit code 1"')).toEqual([
      { text: "perf", negated: true },
      { text: "-1", negated: false },
      { text: "exit code 1", negated: true },
    ]);
  });

  it("splits key=value and key:value into qualifier and text", () => {
    expect(tokenizeSearchQuery('model=surface manufacturer:"Contoso Ltd"')).toEqual([
      { text: "surface", negated: false, qualifier: "model", separator: "=" },
      { text: "Contoso Ltd", negated: false, qualifier: "manufacturer", separator: ":" },
    ]);
  });

  it("takes only the first separator — the value may contain more", () => {
    expect(tokenizeSearchQuery("build=a=b:c")).toEqual([
      { text: "a=b:c", negated: false, qualifier: "build", separator: "=" },
    ]);
  });

  it("never reads a separator after or inside quotes as a qualifier", () => {
    expect(tokenizeSearchQuery('"a=b" "model"=x')).toEqual([
      { text: "a=b", negated: false },
      { text: "model=x", negated: false },
    ]);
  });

  it("does not open a qualifier on a leading separator", () => {
    expect(tokenizeSearchQuery("=x :y")).toEqual([
      { text: "=x", negated: false },
      { text: ":y", negated: false },
    ]);
  });

  it("combines negation with a qualifier", () => {
    expect(tokenizeSearchQuery("-model=surface")).toEqual([
      { text: "surface", negated: true, qualifier: "model", separator: "=" },
    ]);
  });

  it("drops empty tokens: lone minus, empty quotes, a qualifier without a value", () => {
    expect(tokenizeSearchQuery('- "" model=')).toEqual([]);
  });
});

describe("literalText", () => {
  it("restores an unknown qualifier with its separator", () => {
    const [token] = tokenizeSearchQuery("14:30");
    expect(token).toEqual({ text: "30", negated: false, qualifier: "14", separator: ":" });
    expect(literalText(token)).toBe("14:30");
  });

  it("is the text for a plain token", () => {
    expect(literalText({ text: "esp", negated: false })).toBe("esp");
  });
});
