import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import InstallProgress from "../InstallProgress";
import type { InstallEvent } from "@/lib/installProgress";

function evt(eventType: string, timestamp: string, data: Record<string, unknown>): InstallEvent {
  return { eventType, timestamp, data };
}

function renderWithCheckAfterInstall(postInstallDetection: string): string {
  return renderToStaticMarkup(
    createElement(InstallProgress, {
      events: [
        evt("app_install_started", "2026-10-21T16:40:00Z", { appName: "Contoso VPN" }),
        evt("app_install_completed", "2026-10-21T16:42:39Z", { appName: "Contoso VPN", state: "Installed" }),
        evt("app_post_install_detection", "2026-10-21T16:42:41Z", { appName: "Contoso VPN", postInstallDetection, afterReboot: "false" }),
      ],
    }),
  );
}

describe("InstallProgress — detection pill on narrow screens", () => {
  it("hides the green confirmation below sm so the app name keeps its room", () => {
    const html = renderWithCheckAfterInstall("Detected");
    expect(html).toContain("bg-green-100 text-green-700 hidden sm:inline");
    expect(html).toContain(">Detected after install</span>");
    expect(html).not.toContain('class="sm:hidden"');
  });

  it("keeps the red and amber verdicts on phones, with a short label", () => {
    const notDetected = renderWithCheckAfterInstall("NotDetected");
    expect(notDetected).toContain('<span class="sm:hidden">Not detected</span>');
    expect(notDetected).toContain('<span class="hidden sm:inline">Not detected after install</span>');

    const otherVersion = renderWithCheckAfterInstall("DetectedDifferentVersion");
    expect(otherVersion).toContain('<span class="sm:hidden">Other version</span>');
    expect(otherVersion).toContain('<span class="hidden sm:inline">Different version detected</span>');
  });
});
