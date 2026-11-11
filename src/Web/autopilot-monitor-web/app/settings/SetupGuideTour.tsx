"use client";

import { useCallback, useEffect, useState } from "react";
import { usePathname } from "next/navigation";
import { TourSpotlight } from "@/components/setup/TourSpotlight";
import { useAuth } from "@/contexts/AuthContext";
import { useTenant } from "@/contexts/TenantContext";
import { api } from "@/lib/api";
import { fetchJson } from "@/lib/apiClient";
import { trackEvent } from "@/lib/appInsights";
import type { AutopilotConsentUrlResponse } from "@/lib/generated/wire-types.generated";
import { SETUP_TOUR_TARGETS, type SetupTour } from "@/lib/setupGuide";
import { endSetupTour, markSetup, openSetupGuide, useSetupTour } from "@/lib/setupGuideStore";
import { useTenantConfig } from "./TenantConfigContext";

const trimSlash = (path: string) => (path.length > 1 && path.endsWith("/") ? path.slice(0, -1) : path);

/**
 * The setup guide's spotlight on a settings page. The tour sits in sessionStorage, so it survives
 * the device-validation consent, which leaves the portal and comes back to the validation page.
 */
export function SetupGuideTour() {
  const tour = useSetupTour();
  const pathname = usePathname();
  if (!tour || trimSlash(pathname) !== trimSlash(SETUP_TOUR_TARGETS[tour].path)) return null;
  return tour === "validation" ? <ValidationTour /> : <ShowMeTour tour={tour} />;
}

const eyebrow = "flex items-center gap-2 text-[11px] font-semibold uppercase tracking-wider text-green-700 dark:text-green-400";
const primaryButton = "inline-flex items-center gap-1.5 px-3.5 py-2 rounded-lg text-sm font-medium text-white bg-green-600 hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed";
const secondaryButton = "inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg border border-gray-300 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700";
const linkButton = "text-sm font-medium text-green-700 hover:underline underline-offset-2 disabled:opacity-50 dark:text-green-400";

function CloseButton() {
  return (
    <button
      type="button"
      onClick={endSetupTour}
      aria-label="Close setup guide"
      className="ml-auto -my-1 -mr-1 p-1 rounded-md text-gray-400 hover:text-gray-600 hover:bg-gray-100 dark:hover:text-gray-200 dark:hover:bg-gray-700"
    >
      <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" /></svg>
    </button>
  );
}

const SHOW_ME: Record<Exclude<SetupTour, "validation">, { anchors: readonly string[]; title: string; crumb: string; text: string }> = {
  "showme:timezone": {
    anchors: ["timezone", "geo-location"],
    title: "Set Timezone Automatically",
    crumb: "Agent › Agent Settings › Agent Parameters",
    text: "The guide turns it on if you keep it checked. You can switch it here at any time. It appears once Geo-Location Detection is on.",
  },
  "showme:diagnostics": {
    anchors: ["diagnostics-upload"],
    title: "Upload destination",
    crumb: "Agent › Diagnostics Package",
    text: "The guide picks hosted storage here, or keeps your own storage account. Upload Mode, further down on this page, sets when a package is uploaded. You can change both at any time.",
  },
  "showme:inventory": {
    anchors: ["software-inventory"],
    title: "Software Inventory & Vulnerability Analyzer",
    crumb: "Agent › Agent Analyzers",
    text: "The guide turns it on. Its findings show up in each session and under Software.",
  },
};

function ShowMeTour({ tour }: { tour: Exclude<SetupTour, "validation"> }) {
  const text = SHOW_ME[tour];
  const back = () => {
    endSetupTour();
    openSetupGuide("showme", "settings");
  };
  return (
    <TourSpotlight anchors={text.anchors} label="Setup guide hint" onClose={endSetupTour}>
      <div className="space-y-2">
        <p className={eyebrow}>Setup guide · Where to find it<CloseButton /></p>
        <h3 className="text-base font-semibold text-gray-900 dark:text-gray-100">{text.title}</h3>
        <p className="text-xs text-gray-400">{text.crumb}</p>
        <p className="text-sm text-gray-600 dark:text-gray-300">{text.text}</p>
        <div className="pt-1">
          <button type="button" className={primaryButton} onClick={back}>Back to the guide</button>
        </div>
      </div>
    </TourSpotlight>
  );
}

function ValidationTour() {
  const { user, getAccessToken } = useAuth();
  const { tenantId } = useTenant();
  const {
    config, canEditConfig, error, autopilotConsentInProgress, beginDeviceValidationConsentFlow, detectExistingAccess,
    validateAutopilotDevice, validateCorporateIdentifier, validateDeviceAssociation, validateCloudPcDevice, validateIntuneDeviceBinding,
  } = useTenantConfig();
  const [target, setTarget] = useState<"autopilot" | "preparation">("autopilot");
  const [planB, setPlanB] = useState(false);
  const [copied, setCopied] = useState(false);
  const [copyFailed, setCopyFailed] = useState(false);

  const anyGate = validateAutopilotDevice || validateCorporateIdentifier || validateDeviceAssociation
    || validateCloudPcDevice || validateIntuneDeviceBinding;
  const readOnly = !!user?.viewAs || !canEditConfig;
  // Only with a loaded configuration: a failed load sets the same error and says nothing about the approval.
  const failed = !!config && !!error && !anyGate && !autopilotConsentInProgress;

  useEffect(() => {
    if (anyGate && tenantId) markSetup(tenantId, { validation: true });
  }, [anyGate, tenantId]);

  const close = useCallback(() => endSetupTour(), []);

  // Only the Windows Autopilot bubble approves from here; with device preparation the admin picks
  // one of the two options in the highlighted block, each with its own switch and access check.
  const approve = () => {
    trackEvent("setup_consent_started");
    void beginDeviceValidationConsentFlow("autopilot");
  };

  const copyLink = async () => {
    setCopyFailed(false);
    try {
      const data = await fetchJson<AutopilotConsentUrlResponse>(
        api.config.autopilotConsentUrl(tenantId, `${window.location.origin}${SETUP_TOUR_TARGETS.validation.path}`), getAccessToken);
      await navigator.clipboard.writeText(data.consentUrl);
      setCopied(true);
      trackEvent("setup_approval_link_copied");
    } catch {
      setCopyFailed(true);
    }
  };

  if (anyGate) {
    return (
      <TourSpotlight anchors={["autopilot-validation"]} label="Setup guide: validation is on" done onClose={close}>
        <div className="space-y-2">
          <p className={eyebrow}>Setup guide · Step 3 done<CloseButton /></p>
          <h3 className="text-base font-semibold text-gray-900 dark:text-gray-100">Validation is on</h3>
          <p className="text-sm text-gray-600 dark:text-gray-300">
            Your tenant now accepts data from its validated devices. One step is left: put the agent on them with one Intune script.
          </p>
          <div className="flex flex-wrap items-center gap-3 pt-1">
            <button type="button" className={primaryButton} onClick={() => { endSetupTour(); openSetupGuide("tour", "deploy"); }}>
              Next: deploy the agent
            </button>
            <button type="button" className="text-sm font-medium text-gray-500 hover:text-gray-700 dark:text-gray-400" onClick={close}>Later</button>
          </div>
        </div>
      </TourSpotlight>
    );
  }

  if (autopilotConsentInProgress) {
    return (
      <TourSpotlight anchors={["autopilot-validation"]} label="Setup guide: checking the approval" onClose={close}>
        <div className="space-y-2">
          <p className={eyebrow}>Setup guide · Step 3 of 4<CloseButton /></p>
          <h3 className="text-base font-semibold text-gray-900 dark:text-gray-100">Checking the approval…</h3>
          <p className="text-sm text-gray-600 dark:text-gray-300">Microsoft can take up to a minute to confirm it. This page updates on its own.</p>
        </div>
      </TourSpotlight>
    );
  }

  const anchors = target === "autopilot" ? ["autopilot-validation"] : ["device-preparation-validation"];
  return (
    <TourSpotlight anchors={anchors} label="Setup guide step 3" onClose={close}>
      <div className="space-y-2.5">
        <p className={eyebrow}>
          Setup guide · Step 3 of 4
          <span className="px-2 py-0.5 rounded-full text-[11px] font-medium normal-case tracking-normal bg-amber-50 text-amber-800 border border-amber-200 dark:bg-amber-900/30 dark:text-amber-200 dark:border-amber-800">Required</span>
          <CloseButton />
        </p>
        {failed && (
          <p className="rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-900 dark:bg-amber-900/30 dark:text-amber-100">
            The approval was not completed. If you can&apos;t approve it yourself, send the link below to someone who can.
          </p>
        )}
        {target === "autopilot" ? (
          <>
            <h3 className="text-base font-semibold text-gray-900 dark:text-gray-100">Turn on Autopilot device validation</h3>
            <p className="text-sm text-gray-600 dark:text-gray-300">
              The backend only accepts data from devices registered as Windows Autopilot devices in your tenant. Almost every tenant uses this check.
            </p>
            <ul className="space-y-1 text-sm text-gray-600 dark:text-gray-300">
              <li><span className="font-semibold text-gray-800 dark:text-gray-100">What happens:</span> Microsoft asks you to approve one read-only permission. Then you are back on this page and the setting is saved.</li>
              <li><span className="font-semibold text-gray-800 dark:text-gray-100">Who can approve:</span> a Global Administrator or Privileged Role Administrator.</li>
            </ul>
            <div className="flex flex-wrap items-center gap-3 pt-1">
              <button type="button" className={primaryButton} disabled={readOnly} onClick={approve}>Turn on and approve</button>
              <button type="button" className={linkButton} onClick={() => setTarget("preparation")}>I use device preparation</button>
            </div>
          </>
        ) : (
          <>
            <h3 className="text-base font-semibold text-gray-900 dark:text-gray-100">Using Autopilot device preparation?</h3>
            <p className="text-sm text-gray-600 dark:text-gray-300">
              Turn on Corporate Identifier Validation or Device Association Validation, whichever list you keep in Intune. Both use the same
              read-only permission and the same approval.
            </p>
            <button type="button" className={linkButton} onClick={() => setTarget("autopilot")}>Back to Windows Autopilot</button>
          </>
        )}
        <div className="border-t border-gray-200 pt-2 dark:border-gray-700">
          <button
            type="button"
            aria-expanded={planB || failed}
            onClick={() => setPlanB(!planB)}
            className="text-sm font-medium text-gray-700 hover:text-gray-900 dark:text-gray-200"
          >
            Can&apos;t approve it yourself?
          </button>
          {(planB || failed) && (
            <div className="mt-2 space-y-2">
              <p className="text-sm text-gray-600 dark:text-gray-300">
                Send the approval link to your Global Administrator. Once they approved it, click{" "}
                <span className="font-semibold">Detect existing access</span>
                {target === "autopilot" ? "." : " under the validation you use."}
              </p>
              <div className="flex flex-wrap gap-2">
                <button type="button" className={secondaryButton} disabled={readOnly} onClick={() => void copyLink()}>
                  {copied ? "Link copied" : "Copy approval link"}
                </button>
                {target === "autopilot" && (
                  <button type="button" className={secondaryButton} disabled={readOnly} onClick={() => void detectExistingAccess("autopilot")}>
                    Detect existing access
                  </button>
                )}
              </div>
              {copyFailed && <p className="text-xs text-red-600 dark:text-red-400">The link could not be copied. Try again.</p>}
            </div>
          )}
        </div>
        {readOnly && (
          <p className="text-xs text-gray-500 dark:text-gray-400">
            {user?.viewAs ? "View as is read-only." : "Only a tenant admin can turn this on."}
          </p>
        )}
      </div>
    </TourSpotlight>
  );
}
