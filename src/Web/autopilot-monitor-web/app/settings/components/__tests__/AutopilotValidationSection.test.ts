import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import AutopilotValidationSection from "../AutopilotValidationSection";

type Props = Parameters<typeof AutopilotValidationSection>[0];

const render = (props: Partial<Props> = {}) =>
  renderToStaticMarkup(createElement(AutopilotValidationSection, {
    validateAutopilotDevice: true,
    setValidateAutopilotDevice: () => {},
    validateCorporateIdentifier: false,
    setValidateCorporateIdentifier: () => {},
    validateDeviceAssociation: false,
    setValidateDeviceAssociation: () => {},
    onToggleIntuneDeviceBinding: () => {},
    onToggleCloudPc: () => {},
    autopilotConsentInProgress: false,
    saving: false,
    onBeginConsent: () => {},
    ...props,
  }));

const badge = (html: string) => /rounded-full text-xs font-medium[^"]*">([^<]+)<\/span>/.exec(html)?.[1];

describe("AutopilotValidationSection permission notice", () => {
  it("warns at a switched-on validation whose grant is missing, and the badge leaves green", () => {
    const html = render({ validateIntuneDeviceBinding: true, intuneEnrollmentPermission: false });
    expect(html).toContain('data-testid="addon-permission-notice-intuneEnrollment"');
    expect(badge(html)).toBe("Permission missing");
    // The notice replaces the short hint.
    expect(html).not.toContain("Permission not granted yet");
  });

  it("stays quiet when the grant is there or the switch is off", () => {
    const granted = render({ validateIntuneDeviceBinding: true, intuneEnrollmentPermission: true });
    expect(granted).not.toContain("addon-permission-notice");
    expect(granted).toContain("Permission granted.");
    expect(badge(granted)).toBe("Enabled");

    const off = render({ validateIntuneDeviceBinding: false, intuneEnrollmentPermission: false });
    expect(off).not.toContain("addon-permission-notice");
    expect(off).toContain("Permission not granted yet");
    expect(badge(off)).toBe("Enabled");
  });

  it("falls back to the confirmed backend state while the grant status is unknown", () => {
    const confirmed = render({ validateCloudPcDevice: true, cloudPcPermission: null, cloudPcAffectedSince: "2026-11-04T08:00:00Z" });
    expect(confirmed).toContain('data-testid="addon-permission-notice-cloudPc"');
    expect(confirmed).toContain("Devices affected since ");

    const unknown = render({ validateCloudPcDevice: true, cloudPcPermission: null });
    expect(unknown).not.toContain("addon-permission-notice");
    expect(badge(unknown)).toBe("Enabled");
  });

  it("trusts a granted status over a backend state the next token will clear", () => {
    const html = render({ validateCloudPcDevice: true, cloudPcPermission: true, cloudPcAffectedSince: "2026-11-04T08:00:00Z" });
    expect(html).not.toContain("addon-permission-notice");
  });

  it("keeps the rejected token first in the badge", () => {
    const html = render({ validateIntuneDeviceBinding: true, intuneEnrollmentPermission: false, accessRejected: true });
    expect(badge(html)).toBe("Access rejected");
  });

  it("gives both add-on switches an anchor for the dashboard link", () => {
    const html = render();
    expect(html).toContain('id="cloud-pc-validation"');
    expect(html).toContain('id="intune-enrollment-validation"');
  });
});
