import { describe, expect, it } from "vitest";
import { effectiveGlobalAdminMode, readDemoParam, stripDemoParam } from "../demoMode";

describe("readDemoParam", () => {
  it("arms on the documented forms", () => {
    expect(readDemoParam("?demo=1")).toBe(true);
    expect(readDemoParam("demo=1")).toBe(true);
    expect(readDemoParam("?demo=true")).toBe(true);
    expect(readDemoParam("?demo=TRUE")).toBe(true);
    expect(readDemoParam("?demo=on")).toBe(true);
  });

  it("treats a bare ?demo as arming — that is what the shorthand means", () => {
    expect(readDemoParam("?demo")).toBe(true);
    expect(readDemoParam("?demo=")).toBe(true);
  });

  it("clears on the documented forms", () => {
    expect(readDemoParam("?demo=0")).toBe(false);
    expect(readDemoParam("?demo=false")).toBe(false);
    expect(readDemoParam("?demo=off")).toBe(false);
  });

  it("returns null when absent, so the stored value is left alone", () => {
    expect(readDemoParam("")).toBeNull();
    expect(readDemoParam(null)).toBeNull();
    expect(readDemoParam(undefined)).toBeNull();
    expect(readDemoParam("?tenant=abc&tab=notifications")).toBeNull();
  });

  it("returns null on an unrecognised value — a typo must never drop the operator out mid-demo", () => {
    expect(readDemoParam("?demo=yes")).toBeNull();
    expect(readDemoParam("?demo=nope")).toBeNull();
  });

  it("reads the parameter regardless of position", () => {
    expect(readDemoParam("?tenant=abc&demo=1&tab=x")).toBe(true);
  });
});

describe("stripDemoParam", () => {
  it("drops the query entirely when demo was the only parameter", () => {
    expect(stripDemoParam("/dashboard?demo=1")).toBe("/dashboard");
  });

  it("keeps the other parameters", () => {
    expect(stripDemoParam("/settings/tenant?demo=1&tab=notifications")).toBe(
      "/settings/tenant?tab=notifications"
    );
    expect(stripDemoParam("/settings/tenant?tab=notifications&demo=0")).toBe(
      "/settings/tenant?tab=notifications"
    );
  });

  it("preserves the hash", () => {
    expect(stripDemoParam("/dashboard?demo=1#top")).toBe("/dashboard#top");
    expect(stripDemoParam("/dashboard?demo=1&a=b#top")).toBe("/dashboard?a=b#top");
  });

  it("leaves a URL without the parameter untouched", () => {
    expect(stripDemoParam("/dashboard")).toBe("/dashboard");
    expect(stripDemoParam("/dashboard?tab=x")).toBe("/dashboard?tab=x");
    expect(stripDemoParam("/dashboard#top")).toBe("/dashboard#top");
  });

  it("handles a bare ?demo with no value", () => {
    expect(stripDemoParam("/dashboard?demo")).toBe("/dashboard");
  });
});

describe("effectiveGlobalAdminMode", () => {
  it("passes the stored value through when not presenting", () => {
    expect(effectiveGlobalAdminMode(true, false)).toBe(true);
    expect(effectiveGlobalAdminMode(false, false)).toBe(false);
  });

  it("forces the global view off while presenting", () => {
    expect(effectiveGlobalAdminMode(true, true)).toBe(false);
    expect(effectiveGlobalAdminMode(false, true)).toBe(false);
  });
});
