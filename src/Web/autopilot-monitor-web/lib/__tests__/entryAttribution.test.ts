import { describe, expect, it } from "vitest";
import { entryAttribution, stampEntryAttribution } from "../entryAttribution";

const PAGE = "https://www.example.test/";

describe("entry referrer", () => {
  it.each([
    ["no referrer", "", "direct"],
    ["a search engine origin", "https://search.example.test/", "search.example.test"],
    ["a full URL, cut to its host", "https://blog.example.test/2026/10/post?ref=feed&id=42", "blog.example.test"],
    ["an Android app referrer", "android-app://com.example.reader/", "com.example.reader"],
    ["upper case", "https://Social.EXAMPLE.test/", "social.example.test"],
    ["a port", "http://intranet.example.test:8080/wiki/page", "intranet.example.test:8080"],
    ["an earlier page load on the own host", "https://www.example.test/plans/", "www.example.test"],
    ["no host", "about:blank", "unknown"],
    ["garbage", "not a url", "unknown"],
  ])("%s", (_name, referrer, expected) => {
    expect(entryAttribution(referrer, PAGE).entryReferrer).toBe(expected);
  });

  it("caps a long host", () => {
    const host = `${"a".repeat(120)}.example.test`;
    expect(entryAttribution(`https://${host}/`, PAGE).entryReferrer).toBe(host.slice(0, 100));
  });
});

describe("entry UTM tags", () => {
  it("reads source, medium and campaign of the document URL", () => {
    const entry = entryAttribution("", `${PAGE}?utm_source=social-net&utm_medium=social&utm_campaign=launch_2026.10`);
    expect(entry).toEqual({
      entryReferrer: "direct",
      entryUtmSource: "social-net",
      entryUtmMedium: "social",
      entryUtmCampaign: "launch_2026.10",
    });
  });

  it("leaves a missing tag out", () => {
    expect(entryAttribution("", `${PAGE}get-started/?utm_source=newsletter`)).toEqual({
      entryReferrer: "direct",
      entryUtmSource: "newsletter",
    });
    expect(entryAttribution("", PAGE)).toEqual({ entryReferrer: "direct" });
  });

  it("never reads utm_content, utm_term or other parameters", () => {
    const entry = entryAttribution("", `${PAGE}?utm_content=recipient-123&utm_term=abc&ref=x&utm_source=event`);
    expect(entry).toEqual({ entryReferrer: "direct", entryUtmSource: "event" });
  });

  it("lower-cases and trims a tag", () => {
    expect(entryAttribution("", `${PAGE}?utm_source=%20Social-Net%20`).entryUtmSource).toBe("social-net");
  });

  it.each([
    ["spaces inside", "summit+2026"],
    ["an address", "someone%40example.test"],
    ["an empty value", ""],
    ["a leading separator", "-launch"],
    ["too long", "a".repeat(65)],
  ])("reports a tag with %s as other", (_name, value) => {
    expect(entryAttribution("", `${PAGE}?utm_campaign=${value}`).entryUtmCampaign).toBe("other");
  });

  it("keeps the referrer when the document URL cannot be parsed", () => {
    expect(entryAttribution("https://search.example.test/", "not a url")).toEqual({ entryReferrer: "search.example.test" });
  });
});

describe("stampEntryAttribution", () => {
  const entry = { entryReferrer: "search.example.test", entryUtmSource: "newsletter" };

  it.each([["PageviewData"], ["EventData"]])("stamps a %s item and keeps its data", (baseType) => {
    const envelope = { baseType, data: { tenantId: "t", theme: "dark" } as Record<string, unknown> };
    expect(stampEntryAttribution(envelope, entry)).toBe(true);
    expect(envelope.data).toEqual({ tenantId: "t", theme: "dark", ...entry });
  });

  it("creates the data bag when the item has none", () => {
    const envelope: { baseType: string; data?: Record<string, unknown> } = { baseType: "EventData" };
    stampEntryAttribution(envelope, entry);
    expect(envelope.data).toEqual(entry);
  });

  it.each([["MetricData"], ["RemoteDependencyData"], ["PageviewPerformanceData"], ["ExceptionData"], [undefined]])(
    "leaves a %s item alone",
    (baseType) => {
      const envelope = { baseType, data: { theme: "dark" } as Record<string, unknown> };
      expect(stampEntryAttribution(envelope, entry)).toBe(false);
      expect(envelope.data).toEqual({ theme: "dark" });
    }
  );
});
