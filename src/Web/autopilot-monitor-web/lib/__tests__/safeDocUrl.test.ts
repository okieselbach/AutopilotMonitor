import { describe, it, expect } from "vitest";
import { safeHttpUrl } from "../safeDocUrl";

describe("safeHttpUrl", () => {
  it("passes absolute http(s) URLs through", () => {
    expect(safeHttpUrl("https://learn.microsoft.com/x")).toBe("https://learn.microsoft.com/x");
    expect(safeHttpUrl("http://example.com/")).toBe("http://example.com/");
    expect(safeHttpUrl("  https://example.com/a?b=c#d  ")).toBe("https://example.com/a?b=c#d");
  });

  it("rejects script-bearing and non-http schemes", () => {
    expect(safeHttpUrl("javascript:alert(1)")).toBeNull();
    expect(safeHttpUrl("JavaScript:alert(1)")).toBeNull();
    expect(safeHttpUrl(" \tjavascript:alert(1)")).toBeNull();
    expect(safeHttpUrl("data:text/html,<script>alert(1)</script>")).toBeNull();
    expect(safeHttpUrl("vbscript:msgbox(1)")).toBeNull();
    expect(safeHttpUrl("file:///etc/passwd")).toBeNull();
  });

  it("rejects relative, empty and non-string values", () => {
    expect(safeHttpUrl("/relative/path")).toBeNull();
    expect(safeHttpUrl("example.com")).toBeNull();
    expect(safeHttpUrl("")).toBeNull();
    expect(safeHttpUrl(null)).toBeNull();
    expect(safeHttpUrl(undefined)).toBeNull();
  });
});
