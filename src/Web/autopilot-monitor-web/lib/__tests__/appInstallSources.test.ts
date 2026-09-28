import { describe, expect, it } from "vitest";
import { APP_INSTALL_SOURCES, appInstallSourceParam, parseAppInstallSource } from "@/lib/appInstallSources";
import { appDetailUrl } from "@/lib/routes";

describe("app install sources", () => {
  it("comes from the shared manifest", () => {
    expect(APP_INSTALL_SOURCES).toEqual(["ime", "realmjoin"]);
  });

  it("parses ?source= and falls back to the Intune channel", () => {
    expect(parseAppInstallSource("realmjoin")).toBe("realmjoin");
    expect(parseAppInstallSource(null)).toBe("ime");
    expect(parseAppInstallSource("")).toBe("ime");
    expect(parseAppInstallSource("RealmJoin")).toBe("ime");
    expect(parseAppInstallSource("office-c2r")).toBe("ime");
  });

  it("leaves the Intune default out of query strings", () => {
    expect(appInstallSourceParam("ime")).toBeUndefined();
    expect(appInstallSourceParam(undefined)).toBeUndefined();
    expect(appInstallSourceParam("realmjoin")).toBe("realmjoin");
  });

  it("app detail links keep the Intune shape and add the channel otherwise", () => {
    expect(appDetailUrl("7-Zip", { days: "30", source: "ime" })).toBe("/apps/detail?name=7-Zip&days=30");
    expect(appDetailUrl("7-Zip", { days: "30", source: "realmjoin" })).toBe("/apps/detail?name=7-Zip&days=30&source=realmjoin");
  });
});
