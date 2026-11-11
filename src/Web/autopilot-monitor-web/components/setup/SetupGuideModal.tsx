"use client";

import { useEffect, useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { ModalPortal } from "@/components/ModalPortal";
import { HostedUploadDisclosure } from "@/components/HostedUploadDisclosure";
import { useAuth } from "@/contexts/AuthContext";
import { useContinueSetup, useSetupChecklist } from "@/hooks/useSetupChecklist";
import { api } from "@/lib/api";
import { apiErrorText, fetchJson, type GetAccessToken } from "@/lib/apiClient";
import { trackEvent } from "@/lib/appInsights";
import { DOCS_URL } from "@/lib/config";
import { DOCS_PATHS } from "@/lib/docsPaths";
import type { TenantConfiguration } from "@/lib/generated/wire-types.generated";
import { addWhitelistEntry, joinList, parseList } from "@/lib/hardwareWhitelist";
import {
  AGENT_LOADER_SCRIPT_NAME,
  AGENT_LOADER_SCRIPT_URL,
  AUTOPILOT_DEVICE_GROUP_RULE,
  QUICK_MANUFACTURERS,
  RECOMMENDED_ITEMS,
  SETUP_STEPS,
  SETUP_TOUR_TARGETS,
  SHOW_ME_TOUR,
  recommendedPatch,
  recommendedStatus,
  selectionUsesHostedStorage,
  type DiagnosticsUploadMode,
  type RecommendedItem,
  type SetupStepId,
} from "@/lib/setupGuide";
import { closeSetupGuide, markSetup, startSetupTour } from "@/lib/setupGuideStore";
import { recordSetupGuideStep, writeGuideConfigFields } from "@/lib/setupGuideWrites";
import { subscribeTenantConfigPatches } from "@/lib/tenantConfigPatches";

const STEP_TITLES: Record<SetupStepId, string> = {
  settings: "Recommended settings",
  hardware: "Check your hardware",
  validation: "Turn on device validation",
  deploy: "Deploy the agent with Intune",
};

const REQUIRED: Record<SetupStepId, boolean> = { settings: false, hardware: false, validation: true, deploy: true };

/**
 * The setup guide dialog: recommended settings, hardware, the way into device validation and the
 * agent deployment. Settings are written through the field PATCH like any settings card; the guide
 * endpoint only records that a step was finished.
 */
export function SetupGuideModal({ initialStep }: { initialStep: SetupStepId | null }) {
  const { getAccessToken } = useAuth();
  const { checklist, tenantId, canEdit, readOnlyReason } = useSetupChecklist();
  const continueSetup = useContinueSetup();
  const [chosenStep, setChosenStep] = useState<SetupStepId | null>(initialStep);
  const step: SetupStepId | "complete" | null = chosenStep ?? (checklist ? checklist.next ?? "complete" : null);

  const [config, setConfig] = useState<TenantConfiguration | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  useEffect(() => {
    if (!tenantId) return;
    let cancelled = false;
    const run = async () => {
      try {
        const loaded = await fetchJson<TenantConfiguration>(api.config.tenant(tenantId), getAccessToken);
        if (!cancelled) setConfig(loaded);
      } catch (err) {
        if (!cancelled) setLoadError(apiErrorText(err, "The configuration could not be loaded."));
      }
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, [tenantId, getAccessToken]);

  // The guide's own writes go into the copy the steps read: after "Back" a step shows what was
  // just applied instead of offering it again.
  useEffect(() => subscribeTenantConfigPatches((patchedTenantId, fields) => {
    if (patchedTenantId !== tenantId) return;
    setConfig((prev) => (prev ? ({ ...prev, ...fields } as TenantConfiguration) : prev));
  }), [tenantId]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") closeSetupGuide();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const goTo = (next: SetupStepId | null) => {
    if (next === "validation") {
      continueSetup("validation", "guide");
      return;
    }
    setChosenStep(next ?? null);
  };

  const stepIndex = step && step !== "complete" ? SETUP_STEPS.indexOf(step) : -1;

  return (
    <ModalPortal>
      <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4" onMouseDown={(e) => { if (e.target === e.currentTarget) closeSetupGuide(); }}>
        <div
          role="dialog"
          aria-modal="true"
          aria-labelledby="setup-guide-title"
          className="bg-white dark:bg-gray-800 rounded-xl shadow-xl w-full max-w-2xl max-h-[calc(100vh-2rem)] flex flex-col"
        >
          <div className="px-6 pt-5 pb-4 border-b border-gray-200 dark:border-gray-700 space-y-2">
            <div className="flex items-center gap-3">
              <p className="text-[11px] font-semibold uppercase tracking-wider text-green-700 dark:text-green-400">Setup guide</p>
              {stepIndex >= 0 && <span className="text-xs text-gray-500 dark:text-gray-400 tabular-nums">Step {stepIndex + 1} of 4</span>}
              {step && step !== "complete" && REQUIRED[step] && (
                <span className="px-2 py-0.5 rounded-full text-[11px] font-medium bg-amber-50 text-amber-800 border border-amber-200 dark:bg-amber-900/30 dark:text-amber-200 dark:border-amber-800">Required</span>
              )}
              <button
                type="button"
                onClick={closeSetupGuide}
                className="ml-auto p-1.5 rounded-md text-gray-400 hover:text-gray-600 hover:bg-gray-100 dark:hover:text-gray-200 dark:hover:bg-gray-700"
                aria-label="Close setup guide"
              >
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" /></svg>
              </button>
            </div>
            <div className="grid grid-cols-4 gap-1" aria-hidden="true">
              {SETUP_STEPS.map((s) => (
                <span
                  key={s}
                  className={`h-1 rounded-full ${s === step ? "bg-green-600" : checklist?.done[s] ? "bg-green-300 dark:bg-green-700" : "bg-gray-200 dark:bg-gray-700"}`}
                />
              ))}
            </div>
            <h2 id="setup-guide-title" className="text-lg font-semibold text-gray-900 dark:text-gray-100">
              {step === "complete" ? "Setup is complete" : step ? STEP_TITLES[step] : "Setup guide"}
            </h2>
          </div>

          {readOnlyReason && (
            <p className="mx-6 mt-4 rounded-lg border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-600 dark:border-gray-700 dark:bg-gray-900/40 dark:text-gray-300">
              {readOnlyReason === "view-as"
                ? "View as is read-only: this shows what the tenant's admins see."
                : "A tenant admin completes these steps. You can follow along here."}
            </p>
          )}

          {step === "settings" && (
            <SettingsStep
              config={config}
              loadError={loadError}
              tenantId={tenantId}
              canEdit={canEdit}
              getAccessToken={getAccessToken}
              onDone={() => goTo("hardware")}
            />
          )}
          {step === "hardware" && (
            <HardwareStep
              config={config}
              loadError={loadError}
              tenantId={tenantId}
              canEdit={canEdit}
              getAccessToken={getAccessToken}
              onBack={() => goTo("settings")}
              onDone={() => goTo("validation")}
            />
          )}
          {step === "validation" && <ValidationStep onContinue={() => goTo("validation")} />}
          {step === "deploy" && <DeployStep config={config} />}
          {step === "complete" && (
            <CompleteStep
              onRestart={() => {
                trackEvent("setup_guide_opened", { source: "again", step: "settings" });
                goTo("settings");
              }}
            />
          )}

          {checklist?.dismissed && !checklist.done.deploy && canEdit && (
            <RestoreLine tenantId={tenantId} getAccessToken={getAccessToken} />
          )}
        </div>
      </div>
    </ModalPortal>
  );
}

interface StepProps {
  config: TenantConfiguration | null;
  loadError: string | null;
  tenantId: string;
  canEdit: boolean;
  getAccessToken: GetAccessToken;
}

function Footer({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-3 px-6 py-4 border-t border-gray-200 bg-gray-50 rounded-b-xl dark:border-gray-700 dark:bg-gray-900/40">
      {children}
    </div>
  );
}

function LoadingBody({ error }: { error: string | null }) {
  return (
    <div className="px-6 py-8 text-sm text-gray-500 dark:text-gray-400">
      {error ?? "Loading the configuration…"}
    </div>
  );
}

const linkButton = "text-sm font-medium text-gray-600 hover:text-gray-900 hover:underline underline-offset-2 disabled:opacity-50 disabled:no-underline dark:text-gray-300 dark:hover:text-gray-100";
const primaryButton = "inline-flex items-center gap-1.5 px-4 py-2 rounded-lg text-sm font-medium text-white bg-green-600 hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed";

// ── Step 1 ──

const ITEM_TEXT: Record<RecommendedItem, { title: string; crumb: string }> = {
  timezone: { title: "Set the device timezone automatically", crumb: "Agent › Agent Settings" },
  diagnostics: { title: "Upload diagnostics packages", crumb: "Agent › Diagnostics Package" },
  inventory: { title: "Software inventory and vulnerability analyzer", crumb: "Agent › Agent Analyzers" },
};

function SettingsStep({ config, loadError, tenantId, canEdit, getAccessToken, onDone }: StepProps & { onDone: () => void }) {
  const router = useRouter();
  const status = useMemo(() => (config ? recommendedStatus(config) : null), [config]);
  const [deselected, setDeselected] = useState<ReadonlySet<RecommendedItem>>(new Set());
  const [mode, setMode] = useState<DiagnosticsUploadMode>("OnFailure");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!config || !status) return <LoadingBody error={loadError} />;

  const selected = new Set(RECOMMENDED_ITEMS.filter((item) => !deselected.has(item) && !status.alreadyOn[item]));
  const patch = recommendedPatch(config, selected, mode);
  const changes = selected.size;

  const toggle = (item: RecommendedItem) => {
    setDeselected((prev) => {
      const next = new Set(prev);
      if (next.has(item)) next.delete(item);
      else next.add(item);
      return next;
    });
  };

  const finish = async (apply: boolean) => {
    setBusy(true);
    setError(null);
    try {
      if (apply) await writeGuideConfigFields(tenantId, patch, "setup-guide:recommended", getAccessToken);
      await recordSetupGuideStep(tenantId, "settings", getAccessToken);
      markSetup(tenantId, { settings: true });
      trackEvent(apply && changes > 0 ? "setup_step_completed" : "setup_step_skipped", {
        step: "settings",
        applied: apply ? Object.keys(patch).join(",") : "",
        diagnosticsMode: apply && selected.has("diagnostics") ? mode : "",
      });
      onDone();
    } catch (err) {
      setError(apiErrorText(err, "The settings could not be saved."));
    } finally {
      setBusy(false);
    }
  };

  const showMe = (item: RecommendedItem) => {
    trackEvent("setup_showme_opened", { item });
    const tour = SHOW_ME_TOUR[item];
    closeSetupGuide();
    startSetupTour(tour);
    router.push(SETUP_TOUR_TARGETS[tour].path);
  };

  return (
    <>
      <div className="px-6 py-4 overflow-y-auto space-y-3">
        <p className="text-sm text-gray-600 dark:text-gray-300">
          Three settings that get more out of every enrollment. Uncheck what you don&apos;t want. You can change all of it
          later under Configuration.
        </p>
        {RECOMMENDED_ITEMS.map((item) => {
          const on = status.alreadyOn[item];
          const checked = on || !deselected.has(item);
          return (
            <div
              key={item}
              className={`rounded-lg border p-3 space-y-2 ${checked && !on ? "border-green-200 bg-green-50/60 dark:border-green-800 dark:bg-green-900/10" : "border-gray-200 dark:border-gray-700"}`}
            >
              <label className="flex items-start gap-3 cursor-pointer">
                <input
                  type="checkbox"
                  className="mt-1 h-4 w-4 rounded border-gray-300 text-green-600 focus:ring-green-500 disabled:opacity-60"
                  checked={checked}
                  disabled={on || !canEdit || busy}
                  onChange={() => toggle(item)}
                />
                <span className="min-w-0 space-y-0.5">
                  <span className="flex flex-wrap items-center gap-2 text-sm font-semibold text-gray-900 dark:text-gray-100">
                    {ITEM_TEXT[item].title}
                    {on && <span className="px-2 py-0.5 rounded-full text-[11px] font-medium bg-gray-100 text-gray-600 border border-gray-300 dark:bg-gray-700 dark:text-gray-300 dark:border-gray-600">Already on</span>}
                  </span>
                  <span className="block text-xs text-gray-400">{ITEM_TEXT[item].crumb}</span>
                  <span className="block text-sm text-gray-600 dark:text-gray-300">
                    {item === "timezone" && "The agent sets the timezone from the device's IP location, so enrollment timelines and logs show local time."}
                    {item === "diagnostics" && (status.ownStorage
                      ? "The logs of the device go to the storage account you configured, ready next to the session."
                      : "The logs of the device are attached to the session, without a storage account of your own.")}
                    {item === "inventory" && "Lists the software installed at enrollment start and end and matches it against known vulnerabilities (NVD, CISA KEV, MSRC)."}
                  </span>
                </span>
              </label>
              <div className="ml-7 space-y-2">
                {item === "timezone" && !on && (
                  <p className="text-xs text-gray-500 dark:text-gray-400">
                    Leave it off if your devices go online through a proxy in another country.
                    {status.geoOff && " It also turns Geo-Location Detection back on: the timezone comes from it."}
                  </p>
                )}
                {item === "diagnostics" && !on && checked && (
                  <div className="flex flex-wrap items-center gap-2 text-xs text-gray-500 dark:text-gray-400">
                    <span>Upload</span>
                    <span className="inline-flex rounded-lg border border-gray-200 p-0.5 dark:border-gray-600" role="group" aria-label="Upload mode">
                      {(["OnFailure", "Always"] as const).map((m) => (
                        <button
                          key={m}
                          type="button"
                          aria-pressed={mode === m}
                          disabled={!canEdit || busy}
                          onClick={() => setMode(m)}
                          className={`px-2.5 py-1 rounded-md text-xs font-medium ${mode === m ? "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-200" : "text-gray-500 hover:text-gray-700 dark:text-gray-400"}`}
                        >
                          {m === "OnFailure" ? "When an enrollment fails" : "After every enrollment"}
                        </button>
                      ))}
                    </span>
                  </div>
                )}
                {item === "diagnostics" && selectionUsesHostedStorage(config, selected) && <HostedUploadDisclosure />}
                <button type="button" onClick={() => showMe(item)} className="text-xs font-medium text-green-700 hover:underline underline-offset-2 dark:text-green-400">
                  Show me where
                </button>
              </div>
            </div>
          );
        })}
        {!status.geoOff && (
          <p className="text-xs text-gray-500 dark:text-gray-400">Geo-location detection is already on.</p>
        )}
        {error && <p className="text-sm text-red-600 dark:text-red-400">{error}</p>}
      </div>
      <Footer>
        <button type="button" className={linkButton} disabled={!canEdit || busy} onClick={() => void finish(false)}>
          Skip this step
        </button>
        <button type="button" className={primaryButton} disabled={!canEdit || busy} onClick={() => void finish(true)}>
          {changes > 0 ? `Apply ${changes} setting${changes === 1 ? "" : "s"}` : "Continue"}
        </button>
      </Footer>
    </>
  );
}

// ── Step 2 ──

function HardwareStep({ config, loadError, tenantId, canEdit, getAccessToken, onBack, onDone }: StepProps & { onBack: () => void; onDone: () => void }) {
  const [edited, setEdited] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!config) return <LoadingBody error={loadError} />;

  const stored = config.manufacturerWhitelist ?? "";
  const csv = edited ?? stored;
  const entries = parseList(csv);
  const changed = csv !== stored;
  const lower = new Set(entries.map((e) => e.toLowerCase()));

  const add = (value: string) => setEdited(addWhitelistEntry(csv, value));
  const remove = (value: string) => setEdited(joinList(entries.filter((e) => e !== value)));

  const finish = async () => {
    setBusy(true);
    setError(null);
    try {
      if (changed) await writeGuideConfigFields(tenantId, { manufacturerWhitelist: csv }, "setup-guide:hardware", getAccessToken);
      await recordSetupGuideStep(tenantId, "hardware", getAccessToken);
      markSetup(tenantId, { hardware: true });
      trackEvent("setup_step_completed", { step: "hardware", added: entries.filter((e) => !parseList(stored).includes(e)).length });
      onDone();
    } catch (err) {
      setError(apiErrorText(err, "The hardware whitelist could not be saved."));
    } finally {
      setBusy(false);
    }
  };

  const quick = (list: readonly string[]) => list.filter((v) => !lower.has(v.toLowerCase()));
  const groups = [
    { label: "Virtual machines", values: quick(QUICK_MANUFACTURERS.virtualMachines) },
    { label: "Other manufacturers", values: quick(QUICK_MANUFACTURERS.hardware) },
  ];
  const chip = "inline-flex items-center gap-1 px-2.5 py-1 rounded-lg text-xs font-mono";

  return (
    <>
      <div className="px-6 py-4 overflow-y-auto space-y-3">
        <p className="text-sm text-gray-600 dark:text-gray-300">
          Monitoring data is only accepted from manufacturers on this list. Devices from other manufacturers are not
          monitored, and the agent removes itself from them. Testing with virtual machines? They report their own manufacturer.
        </p>
        <p className="text-[11px] font-semibold uppercase tracking-wider text-gray-500 dark:text-gray-400">Accepted now</p>
        <div className="flex flex-wrap gap-1.5">
          {entries.length === 0 && <span className="text-sm text-red-600 dark:text-red-400">Add at least one manufacturer.</span>}
          {entries.map((entry) => (
            <span key={entry} className={`${chip} pr-1 bg-blue-50 text-blue-900 border border-blue-200 dark:bg-blue-900/30 dark:text-blue-100 dark:border-blue-800`}>
              {entry}
              <button type="button" disabled={!canEdit || busy} onClick={() => remove(entry)} className="p-0.5 rounded text-blue-400 hover:text-red-600 disabled:opacity-50" aria-label={`Remove ${entry}`}>
                <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" /></svg>
              </button>
            </span>
          ))}
        </div>
        {groups.map(({ label, values }) => values.length > 0 && (
          <div key={label} className="space-y-1.5">
            <p className="text-[11px] font-semibold uppercase tracking-wider text-gray-500 dark:text-gray-400">{label}</p>
            <div className="flex flex-wrap gap-1.5">
              {values.map((value) => (
                <button
                  key={value}
                  type="button"
                  disabled={!canEdit || busy}
                  onClick={() => add(value)}
                  className={`${chip} border border-dashed border-gray-300 text-gray-700 hover:border-green-500 hover:text-green-700 disabled:opacity-50 dark:border-gray-600 dark:text-gray-200`}
                >
                  + {value}
                </button>
              ))}
            </div>
          </div>
        ))}
        {!lower.has("*") && (
          <button type="button" className="text-xs font-medium text-green-700 hover:underline underline-offset-2 disabled:opacity-50 dark:text-green-400" disabled={!canEdit || busy} onClick={() => add("*")}>
            Accept every manufacturer (*)
          </button>
        )}
        <p className="text-xs text-gray-500 dark:text-gray-400">
          Hyper-V virtual machines report{" "}<code className="font-mono">Microsoft Corporation</code>{" "}and are already covered. Models
          and the full list stay under Configuration › Tenant › Hardware Whitelist.
        </p>
        {error && <p className="text-sm text-red-600 dark:text-red-400">{error}</p>}
      </div>
      <Footer>
        <button type="button" className={linkButton} disabled={busy} onClick={onBack}>Back</button>
        <button type="button" className={primaryButton} disabled={!canEdit || busy || entries.length === 0} onClick={() => void finish()}>
          {changed ? "Save and continue" : "Looks right, continue"}
        </button>
      </Footer>
    </>
  );
}

// ── Step 3 (the spotlight runs on the settings page) ──

function ValidationStep({ onContinue }: { onContinue: () => void }) {
  return (
    <>
      <div className="px-6 py-4 space-y-3 text-sm text-gray-600 dark:text-gray-300">
        <p>
          Agent data is accepted only from devices a validation knows. Almost every tenant validates its Windows Autopilot
          devices: Microsoft asks for one read-only permission, and a Global Administrator or Privileged Role
          Administrator approves it.
        </p>
        <p>The guide takes you to the setting and shows you where to switch it on.</p>
      </div>
      <Footer>
        <span />
        <button type="button" className={primaryButton} onClick={onContinue}>Go to Autopilot Validation</button>
      </Footer>
    </>
  );
}

// ── Step 4 ──

function DeployStep({ config }: { config: TenantConfiguration | null }) {
  const [copied, setCopied] = useState(false);
  const preparationOnly = !!config
    && !config.validateAutopilotDevice
    && (config.validateCorporateIdentifier === true || config.validateDeviceAssociation === true);

  const copyRule = async () => {
    try {
      await navigator.clipboard.writeText(AUTOPILOT_DEVICE_GROUP_RULE);
      setCopied(true);
      trackEvent("setup_group_rule_copied");
    } catch {
      // The rule stays selectable in the code box.
    }
  };

  const number = "shrink-0 w-6 h-6 rounded-full bg-green-100 text-green-800 text-xs font-semibold flex items-center justify-center dark:bg-green-900/40 dark:text-green-200";

  return (
    <>
      <div className="px-6 py-4 overflow-y-auto space-y-4 text-sm text-gray-600 dark:text-gray-300">
        <p>One platform script installs the agent on enrolling devices. Devices already in use are skipped, so you can assign it broadly.</p>
        {preparationOnly && (
          <p className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-amber-900 dark:border-amber-800 dark:bg-amber-900/20 dark:text-amber-100">
            With Autopilot device preparation, platform scripts run only after the Microsoft 365 Apps. Deploy the agent as an MSI
            line-of-business app instead:{" "}
            <a href={`${DOCS_URL}${DOCS_PATHS.autopilotDevicePreparation}`} target="_blank" rel="noopener noreferrer" className="font-medium underline">
              Autopilot device preparation
            </a>
            .
          </p>
        )}
        <ol className="space-y-4">
          <li className="flex gap-3">
            <span className={number}>1</span>
            <div className="min-w-0 space-y-1.5">
              <p className="font-semibold text-gray-900 dark:text-gray-100">Download the loader script</p>
              <p>You upload it once. On each device it fetches the current, signed agent.</p>
              <a
                href={AGENT_LOADER_SCRIPT_URL}
                onClick={() => trackEvent("setup_loader_downloaded")}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg border border-gray-300 text-sm font-medium text-gray-700 hover:bg-gray-50 dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700"
              >
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" /></svg>
                {AGENT_LOADER_SCRIPT_NAME}
              </a>
            </div>
          </li>
          <li className="flex gap-3">
            <span className={number}>2</span>
            <div className="min-w-0 space-y-1.5 w-full">
              <p className="font-semibold text-gray-900 dark:text-gray-100">Add it as a platform script</p>
              <p className="text-xs text-gray-500 dark:text-gray-400">
                Intune admin center › Devices › Scripts and remediations › Platform scripts › Add › Windows 10 and later
              </p>
              <dl className="grid grid-cols-[minmax(0,1fr)_auto] gap-x-4 gap-y-1 rounded-lg border border-gray-200 px-3 py-2 text-xs dark:border-gray-700">
                <dt>Run this script using the logged on credentials</dt><dd className="font-semibold text-gray-900 dark:text-gray-100">No</dd>
                <dt>Enforce script signature check</dt><dd className="font-semibold text-gray-900 dark:text-gray-100">No</dd>
                <dt>Run script in 64 bit PowerShell Host</dt><dd className="font-semibold text-gray-900 dark:text-gray-100">Yes</dd>
              </dl>
            </div>
          </li>
          <li className="flex gap-3">
            <span className={number}>3</span>
            <div className="min-w-0 space-y-1.5 w-full">
              <p className="font-semibold text-gray-900 dark:text-gray-100">Assign it to your Autopilot devices</p>
              <p>Recommended: a dynamic device group with this membership rule.</p>
              <div className="flex items-center gap-2 rounded-lg border border-gray-200 bg-gray-50 py-1 pl-3 pr-1 dark:border-gray-700 dark:bg-gray-900/40">
                <code className="flex-1 min-w-0 overflow-x-auto whitespace-nowrap text-xs text-gray-800 dark:text-gray-200">{AUTOPILOT_DEVICE_GROUP_RULE}</code>
                <button type="button" onClick={() => void copyRule()} className="shrink-0 px-2.5 py-1 rounded-md border border-gray-300 text-xs font-medium text-gray-700 hover:bg-white dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700">
                  {copied ? "Copied" : "Copy"}
                </button>
              </div>
            </div>
          </li>
          <li className="flex gap-3">
            <span className={number}>4</span>
            <div className="min-w-0 space-y-1.5">
              <p className="font-semibold text-gray-900 dark:text-gray-100">Enroll a test device</p>
              <p>Reset a device or start a new one. Devices already in use, or switched on for more than 12 hours, are skipped on purpose.</p>
            </div>
          </li>
        </ol>
      </div>
      <Footer>
        <a
          href={`${DOCS_URL}${DOCS_PATHS.deployTheAgent}`}
          target="_blank"
          rel="noopener noreferrer"
          onClick={() => trackEvent("setup_deploy_docs_opened")}
          className={linkButton}
        >
          Full guide in the docs
        </a>
        <button type="button" className={primaryButton} onClick={closeSetupGuide}>Done, wait for my device</button>
      </Footer>
    </>
  );
}

function CompleteStep({ onRestart }: { onRestart: () => void }) {
  return (
    <>
      <div className="px-6 py-4 text-sm text-gray-600 dark:text-gray-300">
        Your tenant receives enrollments. Every setting the guide touched stays under Configuration.
      </div>
      <Footer>
        <button type="button" className={linkButton} onClick={onRestart}>Go through the steps again</button>
        <button type="button" className={primaryButton} onClick={closeSetupGuide}>Close</button>
      </Footer>
    </>
  );
}

function RestoreLine({ tenantId, getAccessToken }: { tenantId: string; getAccessToken: StepProps["getAccessToken"] }) {
  const [busy, setBusy] = useState(false);
  const restore = async () => {
    setBusy(true);
    try {
      await recordSetupGuideStep(tenantId, "restore", getAccessToken);
      markSetup(tenantId, { dismissed: false });
      trackEvent("setup_guide_restored");
    } catch {
      setBusy(false);
    }
  };
  return (
    <p className="px-6 pb-3 -mt-1 text-xs text-gray-500 dark:text-gray-400">
      Hidden from the dashboard.{" "}
      <button type="button" disabled={busy} onClick={() => void restore()} className="font-medium text-green-700 hover:underline disabled:opacity-50 dark:text-green-400">
        Show it there again
      </button>
    </p>
  );
}
