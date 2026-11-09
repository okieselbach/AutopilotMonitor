"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import type { Route } from "next";
import { useAuth } from "@/contexts/AuthContext";
import { useContinueSetup, type SetupChecklistState } from "@/hooks/useSetupChecklist";
import { api } from "@/lib/api";
import { apiErrorText, fetchJson } from "@/lib/apiClient";
import { trackEvent } from "@/lib/appInsights";
import { DOCS_URL } from "@/lib/config";
import { DOCS_PATHS } from "@/lib/docsPaths";
import type {
  DeviceNotRegisteredItem,
  DeviceNotRegisteredResponse,
  HardwareRejectedItem,
  HardwareRejectedResponse,
  TenantConfiguration,
} from "@/lib/generated/wire-types.generated";
import { addWhitelistEntry, reportedValueEntry } from "@/lib/hardwareWhitelist";
import { route } from "@/lib/routes";
import { SETUP_STEPS, type SetupStepId } from "@/lib/setupGuide";
import { markSetup, openSetupGuide } from "@/lib/setupGuideStore";
import { recordSetupGuideStep, writeGuideConfigFields } from "@/lib/setupGuideWrites";

const ROWS: Record<SetupStepId, { title: string; required: boolean; open: string; done: string; cta: string }> = {
  settings: {
    title: "Apply recommended settings",
    required: false,
    open: "Timezone, diagnostics package, software inventory.",
    done: "Recommended settings reviewed.",
    cta: "Review",
  },
  hardware: {
    title: "Check your hardware",
    required: false,
    open: "Dell, HP, Lenovo and Microsoft are accepted. Using other brands or virtual machines?",
    done: "Hardware whitelist checked.",
    cta: "Check",
  },
  validation: {
    title: "Turn on device validation",
    required: true,
    open: "Agent data is rejected until one validation method is on.",
    done: "Device validation is on.",
    cta: "Enable",
  },
  deploy: {
    title: "Deploy the agent with Intune",
    required: true,
    open: "One platform script for your Autopilot devices. The agent installs on first boot.",
    done: "First enrollment received.",
    cta: "Deploy",
  },
};

const pillRecommended = "px-2 py-0.5 rounded-full text-[11px] font-medium bg-gray-100 text-gray-600 border border-gray-300 dark:bg-gray-700 dark:text-gray-300 dark:border-gray-600";
const pillRequired = "px-2 py-0.5 rounded-full text-[11px] font-medium bg-amber-50 text-amber-800 border border-amber-200 dark:bg-amber-900/30 dark:text-amber-200 dark:border-amber-800";
const ctaClass = "inline-flex items-center gap-1 px-2 py-1.5 rounded-md text-sm font-medium text-green-700 hover:bg-green-50 dark:text-green-400 dark:hover:bg-green-900/20";

/**
 * The dashboard's "Get started" checklist for a tenant that never enrolled a device. It replaces
 * the welcome card and, while it shows, the red "no device validation" banner: the required step
 * says the same thing without alarming a tenant on its first day. Done states come from the flags.
 */
export function SetupChecklistCard({ setup }: { setup: SetupChecklistState }) {
  const { getAccessToken } = useAuth();
  const continueSetup = useContinueSetup();
  const [hiding, setHiding] = useState(false);
  const { checklist, canEdit, readOnlyReason, tenantId } = setup;
  if (!checklist) return null;

  const waiting = checklist.next === "deploy";
  // Agents can connect as soon as a validation is on, whatever the recommended steps say: from
  // then on the card shows which devices the backend turned away.
  const devicesCanConnect = checklist.done.validation && !checklist.done.deploy;

  const hide = async () => {
    setHiding(true);
    try {
      await recordSetupGuideStep(tenantId, "dismiss", getAccessToken);
      markSetup(tenantId, { dismissed: true });
      trackEvent("setup_guide_dismissed", { done: checklist.doneCount });
    } catch {
      setHiding(false);
    }
  };

  return (
    <section aria-labelledby="setup-checklist-title" className="mb-6 bg-white shadow rounded-lg overflow-hidden dark:bg-gray-800">
      <div className="flex flex-wrap items-end justify-between gap-4 px-6 pt-5 pb-4 border-b border-gray-200 dark:border-gray-700">
        <div className="min-w-0 flex-1 basis-72">
          <p className="text-[11px] font-semibold uppercase tracking-wider text-green-700 dark:text-green-400">Get started</p>
          <h2 id="setup-checklist-title" className="mt-1 text-xl font-semibold text-gray-900 dark:text-gray-100">Set up Autopilot Monitor</h2>
          <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">
            Four steps until your first enrollment shows up here. The guide walks you through each one; the last one happens in Intune.
          </p>
        </div>
        <div className="flex items-center gap-3 sm:flex-col sm:items-end sm:gap-1.5">
          <span className="text-xs text-gray-500 tabular-nums dark:text-gray-400">{checklist.doneCount} of 4 done</span>
          <span className="block w-36 h-1.5 rounded-full bg-gray-100 overflow-hidden dark:bg-gray-700" aria-hidden="true">
            <span className="block h-full rounded-full bg-green-600 transition-[width] duration-500" style={{ width: `${checklist.doneCount * 25}%` }} />
          </span>
        </div>
      </div>

      <ol className="px-3 py-2">
        {SETUP_STEPS.map((step) => {
          const row = ROWS[step];
          const done = checklist.done[step];
          const isNext = checklist.next === step;
          return (
            <li key={step} className={`grid grid-cols-[1.5rem_minmax(0,1fr)] sm:grid-cols-[1.5rem_minmax(0,1fr)_auto] items-center gap-x-3 gap-y-1 px-3 py-2.5 rounded-md ${isNext ? "bg-green-50/70 dark:bg-green-900/10" : ""}`}>
              <span
                aria-hidden="true"
                className={`w-5 h-5 rounded-full flex items-center justify-center ${done ? "bg-green-600 text-white" : isNext ? "border-2 border-green-600" : "border-2 border-gray-300 dark:border-gray-600"}`}
              >
                {done && <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={3}><path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" /></svg>}
              </span>
              <div className="min-w-0">
                <p className={`flex flex-wrap items-center gap-2 text-sm font-medium ${done ? "text-gray-500 dark:text-gray-400" : "text-gray-900 dark:text-gray-100"}`}>
                  {row.title}
                  <span className={row.required ? pillRequired : pillRecommended}>{row.required ? "Required" : "Recommended"}</span>
                  <span className="sr-only">{done ? "(done)" : "(open)"}</span>
                </p>
                <p className="mt-0.5 text-sm text-gray-500 dark:text-gray-400">{done ? row.done : row.open}</p>
              </div>
              <div className="col-start-2 sm:col-start-auto">
                {done ? (
                  <span className="inline-flex items-center gap-1 pr-2 text-xs font-medium text-green-700 dark:text-green-400">Done</span>
                ) : canEdit || readOnlyReason === "view-as" ? (
                  <button type="button" className={ctaClass} onClick={() => continueSetup(step, "card")}>
                    {row.cta}
                    <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" /></svg>
                  </button>
                ) : null}
              </div>
            </li>
          );
        })}
      </ol>

      {devicesCanConnect && <WaitingDetails canEdit={canEdit} tenantId={tenantId} />}

      <div className="flex flex-wrap items-center gap-x-4 gap-y-2 px-6 py-3.5 border-t border-gray-200 bg-gray-50 dark:border-gray-700 dark:bg-gray-900/40">
        {waiting ? (
          <>
            <span className="relative flex h-2.5 w-2.5" aria-hidden="true">
              <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-green-500 opacity-60 motion-reduce:animate-none" />
              <span className="relative inline-flex h-2.5 w-2.5 rounded-full bg-green-600" />
            </span>
            <p className="flex-1 basis-64 text-sm text-gray-700 dark:text-gray-300">
              <span className="font-semibold">Waiting for your first enrollment.</span>{" "}
              This page updates as soon as a device starts enrolling.
            </p>
            <button type="button" className={ctaClass} onClick={() => continueSetup("deploy", "card")}>Deployment steps</button>
          </>
        ) : readOnlyReason === "role" ? (
          <p className="text-sm text-gray-500 dark:text-gray-400">A tenant admin completes these steps.</p>
        ) : (
          <>
            <button
              type="button"
              onClick={() => continueSetup(checklist.next, "card")}
              className={`inline-flex items-center gap-2 px-4 py-2 rounded-lg text-sm font-medium text-white bg-green-600 hover:bg-green-700 ${checklist.doneCount === 0 ? "motion-safe:animate-pulse" : ""}`}
            >
              {checklist.doneCount === 0 ? "Start guided setup" : "Continue setup"}
            </button>
            {canEdit && (
              <button
                type="button"
                disabled={hiding}
                onClick={() => void hide()}
                title="You can open the setup guide again from the help menu"
                className="text-sm font-medium text-gray-500 hover:text-gray-700 hover:underline underline-offset-2 disabled:opacity-50 dark:text-gray-400 dark:hover:text-gray-200"
              >
                Hide
              </button>
            )}
            {readOnlyReason === "view-as" && <span className="text-xs text-gray-500 dark:text-gray-400">View as is read-only.</span>}
            <span className="ml-auto text-xs text-gray-400">About 10 minutes, Intune included</span>
          </>
        )}
      </div>
    </section>
  );
}

const REFRESH_MS = 60_000;

/** What the portal can see while it waits: why a device may not show up, and devices the backend turned away. */
function WaitingDetails({ canEdit, tenantId }: { canEdit: boolean; tenantId: string }) {
  const { getAccessToken } = useAuth();
  const [hardware, setHardware] = useState<HardwareRejectedItem[]>([]);
  const [unregistered, setUnregistered] = useState<DeviceNotRegisteredItem[]>([]);
  const [accepted, setAccepted] = useState<ReadonlySet<string>>(new Set());
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      try {
        const [hw, nr] = await Promise.all([
          fetchJson<HardwareRejectedResponse>(api.distress.hardwareRejected(), getAccessToken),
          fetchJson<DeviceNotRegisteredResponse>(api.distress.deviceNotRegistered(), getAccessToken),
        ]);
        if (cancelled) return;
        setHardware(hw.aggregated ?? []);
        setUnregistered(nr.aggregated ?? []);
      } catch {
        // The list is a hint; without it the checklist above still says what to check.
      }
    };
    void load();
    const timer = window.setInterval(() => void load(), REFRESH_MS);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [getAccessToken]);

  // `entry` comes from reportedValueEntry: the reported manufacturer as one literal pattern.
  const accept = async (entry: string) => {
    setError(null);
    try {
      const config = await fetchJson<TenantConfiguration>(api.config.tenant(tenantId), getAccessToken);
      const next = addWhitelistEntry(config.manufacturerWhitelist ?? "", entry);
      await writeGuideConfigFields(tenantId, { manufacturerWhitelist: next }, "setup-guide:accept-hardware", getAccessToken);
      markSetup(tenantId, { hardware: true });
      setAccepted((prev) => new Set(prev).add(entry));
      trackEvent("setup_hardware_accepted");
    } catch (err) {
      setError(apiErrorText(err, "The manufacturer could not be added."));
    }
  };

  const autopilotPage: Route = route("/settings/tenant/autopilot");
  const shownHardware = hardware.slice(0, 3);
  const shownUnregistered = unregistered.slice(0, 3);

  return (
    <div className="px-6 pb-3 space-y-3">
      {(shownHardware.length > 0 || shownUnregistered.length > 0) && (
        <div className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 space-y-2 dark:border-amber-800 dark:bg-amber-900/20">
          <p className="text-sm font-semibold text-amber-900 dark:text-amber-100">Devices we turned away</p>
          <ul className="space-y-1.5 text-sm text-gray-700 dark:text-gray-200">
            {shownHardware.map((item) => {
              // Reported values arrive unauthenticated: a wildcard in them never becomes an entry.
              const entry = reportedValueEntry(item.manufacturer);
              return (
                <li key={`hw-${item.manufacturer}-${item.model}`} className="flex flex-wrap items-center gap-x-3 gap-y-1">
                  <span className="min-w-0 flex-1 basis-60">
                    <span className="font-mono text-xs">{item.manufacturer} · {item.model}</span>
                    <span className="text-gray-500 dark:text-gray-400"> — manufacturer not on your hardware whitelist</span>
                  </span>
                  {entry && accepted.has(entry) ? (
                    <span className="text-xs font-medium text-green-700 dark:text-green-400">
                      Added <span className="font-mono">{entry}</span>: its next enrollments are accepted
                    </span>
                  ) : entry && canEdit ? (
                    <button
                      type="button"
                      onClick={() => void accept(entry)}
                      title={`Adds "${entry}" to the manufacturer whitelist`}
                      className="px-2.5 py-1 rounded-md border border-amber-300 text-xs font-medium text-amber-900 hover:bg-amber-100 dark:border-amber-700 dark:text-amber-100 dark:hover:bg-amber-900/40"
                    >
                      Accept this manufacturer
                    </button>
                  ) : null}
                </li>
              );
            })}
            {shownUnregistered.map((item) => (
              <li key={`nr-${item.serialNumber}`} className="flex flex-wrap items-center gap-x-3 gap-y-1">
                <span className="min-w-0 flex-1 basis-60">
                  <span className="font-mono text-xs">{item.serialNumber}</span>
                  <span className="text-gray-500 dark:text-gray-400"> — not registered as an Autopilot device in this tenant</span>
                </span>
                <Link href={autopilotPage} className="text-xs font-medium text-amber-900 hover:underline dark:text-amber-100">Autopilot Validation</Link>
              </li>
            ))}
          </ul>
          {error && <p className="text-xs text-red-600 dark:text-red-400">{error}</p>}
        </div>
      )}
      <details className="group rounded-lg border border-gray-200 px-4 py-2.5 text-sm dark:border-gray-700">
        <summary className="cursor-pointer font-medium text-gray-700 dark:text-gray-200">No device after an hour? Check these</summary>
        <ul className="mt-2 list-disc space-y-1 pl-5 text-gray-600 dark:text-gray-300">
          <li>The platform script is assigned to a group your test device is in.</li>
          <li>The test device is fresh: reset or new, and switched on for less than 12 hours.</li>
          <li>Virtual machines report their own manufacturer. It has to be on the hardware whitelist.</li>
          <li>
            The dry run on the device shows why the agent skipped it:{" "}
            <a href={`${DOCS_URL}${DOCS_PATHS.deployTheAgent}`} target="_blank" rel="noopener noreferrer" className="font-medium text-green-700 hover:underline dark:text-green-400">
              Deploy the agent
            </a>
            .
          </li>
        </ul>
      </details>
    </div>
  );
}

/** The empty dashboard of a tenant without the checklist: the guide is hidden, or the caller has no tenant role. */
export function NoSessionsNote({ canOpenGuide }: { canOpenGuide: boolean }) {
  return (
    <div className="bg-white shadow rounded-lg px-6 py-5 dark:bg-gray-800">
      <p className="text-base font-semibold text-gray-900 dark:text-gray-100">No enrollments yet</p>
      <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">
        Sessions appear here in real time once the agent runs on a device that enrolls.{" "}
        <a href={`${DOCS_URL}${DOCS_PATHS.deployTheAgent}`} target="_blank" rel="noopener noreferrer" className="font-medium text-green-700 hover:underline dark:text-green-400">
          Deploy the agent
        </a>
        {canOpenGuide && (
          <>
            {" "}or{" "}
            <button type="button" onClick={() => openSetupGuide("empty_note")} className="font-medium text-green-700 hover:underline dark:text-green-400">
              open the setup guide
            </button>
          </>
        )}
        .
      </p>
    </div>
  );
}

/** Shown once, when the first session of a tenant arrives while the checklist is on screen. */
export function FirstEnrollmentCard({ deviceLabel, href, onDismiss }: { deviceLabel: string; href: Route; onDismiss: () => void }) {
  return (
    <div className="mb-6 flex flex-wrap items-center gap-4 rounded-lg border border-green-200 bg-green-50 px-5 py-4 dark:border-green-800 dark:bg-green-900/20">
      <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-green-600 text-white" aria-hidden="true">
        <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}><path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" /></svg>
      </span>
      <div className="min-w-0 flex-1 basis-64">
        <p className="text-base font-semibold text-gray-900 dark:text-gray-100">Your first enrollment is here</p>
        <p className="text-sm text-gray-700 dark:text-gray-300">{deviceLabel} started enrolling. Setup is complete; the setup guide stays in the help menu.</p>
      </div>
      <Link href={href} onClick={() => trackEvent("setup_first_session_opened")} className="px-4 py-2 rounded-lg text-sm font-medium text-white bg-green-600 hover:bg-green-700">
        Open session
      </Link>
      <button type="button" onClick={onDismiss} aria-label="Dismiss" className="p-1 rounded-md text-gray-400 hover:text-gray-600 dark:hover:text-gray-200">
        <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" /></svg>
      </button>
    </div>
  );
}
