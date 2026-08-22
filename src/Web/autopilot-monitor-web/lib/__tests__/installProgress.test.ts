import { describe, it, expect } from "vitest";
import { buildInstallItems, type InstallEvent } from "@/lib/installProgress";

function evt(eventType: string, timestamp: string, data: Record<string, unknown>): InstallEvent {
  return { eventType, timestamp, data };
}

describe("buildInstallItems — source separation", () => {
  it("keeps the admin's Win32 app and the Office C2R lifecycle apart when both carry the same name", () => {
    // The agent emits the fixed name "Microsoft 365 Apps" (OfficeInstallDetector.AppName).
    // An admin packaging their own Office deployment commonly picks the same name — the two
    // lifecycles must not collapse into one row and overwrite each other's state.
    const items = buildInstallItems([
      evt("app_install_started", "2026-07-27T10:00:00Z", { appName: "Microsoft 365 Apps", appId: "win32-1" }),
      evt("office_install_started", "2026-07-27T10:01:00Z", { appName: "Microsoft 365 Apps" }),
      evt("app_install_completed", "2026-07-27T10:02:00Z", { appName: "Microsoft 365 Apps", appId: "win32-1" }),
      evt("office_install_completed", "2026-07-27T10:20:00Z", { appName: "Microsoft 365 Apps" }),
    ]);

    expect(items).toHaveLength(2);

    const ime = items.find(i => i.source === "ime")!;
    const office = items.find(i => i.source === "office-c2r")!;

    expect(ime.appId).toBe("win32-1");
    expect(ime.durationMs).toBe(2 * 60 * 1000);
    expect(office.durationMs).toBe(19 * 60 * 1000);
    expect(ime.key).not.toBe(office.key);
  });

  it("tags plain IME app events as the default source", () => {
    const items = buildInstallItems([
      evt("app_install_completed", "2026-07-27T10:00:00Z", { appName: "7-Zip", appId: "a1" }),
    ]);

    expect(items).toHaveLength(1);
    expect(items[0].source).toBe("ime");
    expect(items[0].key).toBe("ime|7-Zip");
  });

  it("renders RealmJoin packages under their plain display name (no 'RJ: ' prefix)", () => {
    const items = buildInstallItems([
      evt("realmjoin_package_started", "2026-07-27T10:00:00Z", { displayName: "Contoso Baseline", packageId: "pkg-1" }),
      evt("realmjoin_package_completed", "2026-07-27T10:05:00Z", { displayName: "Contoso Baseline", packageId: "pkg-1", success: "true" }),
    ]);

    expect(items).toHaveLength(1);
    expect(items[0].appName).toBe("Contoso Baseline");
    expect(items[0].source).toBe("realmjoin");
    expect(items[0].appId).toBe("pkg-1");
    expect(items[0].state).toBe("Installed");
  });

  it("routes a failed RealmJoin completion into the failed branch", () => {
    const items = buildInstallItems([
      evt("realmjoin_package_started", "2026-07-27T10:00:00Z", { displayName: "Contoso Baseline", packageId: "pkg-1" }),
      evt("realmjoin_package_completed", "2026-07-27T10:05:00Z", { displayName: "Contoso Baseline", packageId: "pkg-1", success: "false", lastExitCode: "1603" }),
    ]);

    expect(items[0].state).toBe("Failed");
    expect(items[0].isError).toBe(true);
    expect(items[0].exitCode).toBe("1603");
  });

  it("does not let a same-named IME app steal the preinstalled Office row", () => {
    const items = buildInstallItems([
      evt("office_preinstalled_detected", "2026-07-27T10:00:00Z", { appName: "Microsoft 365 Apps", reason: "office_already_resident" }),
      evt("app_install_completed", "2026-07-27T10:03:00Z", { appName: "Microsoft 365 Apps", appId: "win32-1" }),
    ]);

    expect(items).toHaveLength(2);
    expect(items.find(i => i.source === "office-c2r")!.state).toBe("Preinstalled");
    expect(items.find(i => i.source === "ime")!.state).toBe("Installed");
  });
});

describe("buildInstallItems — state folding", () => {
  it("keeps the first appearance order", () => {
    const items = buildInstallItems([
      evt("app_install_started", "2026-07-27T10:00:00Z", { appName: "B" }),
      evt("app_install_started", "2026-07-27T09:00:00Z", { appName: "A" }),
    ]);

    expect(items.map(i => i.appName)).toEqual(["A", "B"]);
  });

  it("backfills the start time when completed arrives before started", () => {
    const items = buildInstallItems([
      evt("office_install_completed", "2026-07-27T10:10:00Z", { appName: "Microsoft 365 Apps" }),
      evt("office_install_started", "2026-07-27T10:00:00Z", { appName: "Microsoft 365 Apps" }),
    ]);

    expect(items).toHaveLength(1);
    expect(items[0].state).toBe("Installed");
    expect(items[0].durationMs).toBe(10 * 60 * 1000);
  });

  it("does not downgrade an installed app to failed", () => {
    const items = buildInstallItems([
      evt("app_install_completed", "2026-07-27T10:00:00Z", { appName: "7-Zip" }),
      evt("app_install_failed", "2026-07-27T10:05:00Z", { appName: "7-Zip" }),
    ]);

    expect(items[0].state).toBe("Installed");
  });

  it("maps the ESP failure types onto their badge flags", () => {
    const items = buildInstallItems([
      evt("app_install_started", "2026-07-27T10:00:00Z", { appName: "Slow App" }),
      evt("app_install_failed", "2026-07-27T10:05:00Z", { appName: "Slow App", failureType: "esp_apps_timeout" }),
      evt("app_install_failed", "2026-07-27T10:05:00Z", { appName: "Undetected App", failureType: "esp_apps_detection_failure" }),
      evt("app_install_failed", "2026-07-27T10:05:00Z", { appName: "Broken App", failureType: "esp_apps_install_failure" }),
    ]);

    expect(items.find(i => i.appName === "Slow App")!.isLikelyStuck).toBe(true);
    expect(items.find(i => i.appName === "Undetected App")!.isDetectionFailure).toBe(true);
    expect(items.find(i => i.appName === "Broken App")!.isInstallFailure).toBe(true);
  });

  it("ignores events without a resolvable name", () => {
    const items = buildInstallItems([
      evt("app_install_started", "2026-07-27T10:00:00Z", {}),
      evt("app_install_started", "2026-07-27T10:01:00Z", { app_name: "Snake_Case App" }),
    ]);

    expect(items).toHaveLength(1);
    expect(items[0].appName).toBe("Snake_Case App");
  });
});

// IME reports skips and uninstall enforcements through the same app_install_completed event —
// the distinction lives only in the payload. Fixtures mirror session 502274b4 (2026-08-21).
describe("buildInstallItems — intent & skip-via-completed folding", () => {
  it("folds app_install_completed with state Skipped into the Skipped state (Autopatch Client Broker case)", () => {
    const items = buildInstallItems([
      evt("app_install_completed", "2026-08-21T20:48:15Z", { appName: "Windows Autopatch Client Broker", intent: "Install", state: "Skipped" }),
    ]);

    expect(items).toHaveLength(1);
    expect(items[0].state).toBe("Skipped");
    expect(items[0].isError).toBe(false);
  });

  it("routes a completed uninstall enforcement into the Uninstalled state (Xbox case)", () => {
    const items = buildInstallItems([
      evt("app_install_completed", "2026-08-21T20:48:22Z", { appName: "Xbox (System)", intent: "Uninstall", state: "Installed" }),
    ]);

    expect(items[0].state).toBe("Uninstalled");
    expect(items[0].intent).toBe("Uninstall");
    expect(items[0].isCompleted).toBe(true);
    expect(items[0].isError).toBe(false);
  });

  it("lets state Skipped win over an uninstall intent — app was never present (Ubuntu case)", () => {
    const items = buildInstallItems([
      evt("app_install_completed", "2026-08-21T20:48:20Z", { appName: "Ubuntu 20.04.5 LTS", intent: "Uninstall", state: "Skipped" }),
    ]);

    expect(items[0].state).toBe("Skipped");
  });

  it("keeps legacy events without intent/state folding to Installed", () => {
    const items = buildInstallItems([
      evt("app_install_completed", "2026-08-21T20:47:27Z", { appName: "RealmJoin Agent (Device)", intent: "Install", state: "Installed" }),
      evt("app_install_completed", "2026-08-21T20:47:27Z", { appName: "Legacy App" }),
    ]);

    expect(items.find(i => i.appName === "RealmJoin Agent (Device)")!.state).toBe("Installed");
    expect(items.find(i => i.appName === "Legacy App")!.state).toBe("Installed");
  });

  it("treats Uninstalled as terminal — no downgrade to failed and no restart reset", () => {
    const items = buildInstallItems([
      evt("app_install_started", "2026-08-21T20:48:00Z", { appName: "Xbox (System)", intent: "Uninstall" }),
      evt("app_install_completed", "2026-08-21T20:48:22Z", { appName: "Xbox (System)", intent: "Uninstall" }),
      evt("app_install_failed", "2026-08-21T20:48:30Z", { appName: "Xbox (System)", intent: "Uninstall" }),
      evt("app_install_started", "2026-08-21T20:48:40Z", { appName: "Xbox (System)", intent: "Uninstall" }),
    ]);

    expect(items).toHaveLength(1);
    expect(items[0].state).toBe("Uninstalled");
    expect(items[0].durationMs).toBe(22 * 1000);
  });

  it("carries the intent onto non-terminal rows so active uninstalls can be labelled", () => {
    const items = buildInstallItems([
      evt("app_install_started", "2026-08-21T20:48:00Z", { appName: "Some Win32 App", intent: "Uninstall" }),
    ]);

    expect(items[0].state).toBe("Installing");
    expect(items[0].intent).toBe("Uninstall");
  });
});
