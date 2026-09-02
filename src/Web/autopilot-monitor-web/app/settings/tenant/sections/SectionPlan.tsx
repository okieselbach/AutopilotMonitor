"use client";

import { useState } from "react";
import Link from "next/link";
import { useTenantConfig } from "../../TenantConfigContext";
import { missingContactProfileParts, trialDaysLeft } from "@/lib/edition";
import { PlanCards } from "@/components/plans/PlanCards";
import { SITE_URL } from "@/utils/config";
import { SectionCardHeader } from "@/components/SectionCardHeader";
import { DOCS_PATHS } from "@/lib/docsPaths";

/**
 * Self-service Pro trial switch. The Pro feature set is not finalized yet, so the
 * trial CTA is teased but not actionable. Flip this to `true` (a one-line change) to open the
 * self-service 30-day trial — the backend POST /trial endpoint and the startTrial() wiring are
 * already in place; only this gate keeps the button inert.
 */
const TRIAL_SELF_SERVICE_ENABLED = false;

/**
 * Plan section: the shared Community/Pro comparison cards (components/plans/PlanCards —
 * also rendered on the public /plans page) with the tenant's current plan highlighted,
 * plus the portal-only trial CTA and the purchase handoff.
 *
 * The purchase link is an ABSOLUTE www URL on purpose: /buy is a public path, and
 * HostRoutingGuard never bounces an authenticated user off the portal origin, so a
 * relative link would try (and fail) to serve the marketing page on the portal host.
 */
export function SectionPlan() {
  const { editionInfo, startTrial, startingTrial, user } = useTenantConfig();
  const [confirming, setConfirming] = useState(false);

  const isPro = editionInfo.edition === "pro";
  const daysLeft = editionInfo.isTrial ? trialDaysLeft(editionInfo.trialExpiresUtc) : 0;
  const trialConsumed = !isPro && !editionInfo.trialAvailable;
  const canStartTrial =
    editionInfo.trialAvailable && (user?.isTenantAdmin === true || user?.isGlobalAdmin === true);
  const missingProfile = missingContactProfileParts(editionInfo);

  const communityBadge = !isPro ? (
    <span className="inline-flex items-center px-2.5 py-1 rounded-full text-xs font-semibold bg-gray-900 text-white dark:bg-slate-200 dark:text-slate-900">
      Current plan
    </span>
  ) : undefined;

  const proBadge = isPro ? (
    <span className="inline-flex items-center px-2.5 py-1 rounded-full text-xs font-semibold bg-purple-600 text-white">
      {editionInfo.isTrial ? `Trial — ${daysLeft} day${daysLeft === 1 ? "" : "s"} left` : "Current plan"}
    </span>
  ) : (
    <span className="inline-flex items-center px-2.5 py-1 rounded-full text-xs font-medium bg-purple-100 text-purple-700 border border-purple-200">
      Coming soon
    </span>
  );

  // CTA — only meaningful while the tenant is on Community
  const proCta = !isPro ? (
    <>
      {trialConsumed ? (
        <p className="text-sm text-gray-600">
          This tenant has already used its Pro trial. To move to Pro,{" "}
          <a
            href="https://github.com/okieselbach/AutopilotMonitor/issues"
            target="_blank"
            rel="noopener noreferrer"
            className="text-purple-700 hover:underline"
          >
            get in touch
          </a>
          .
        </p>
      ) : !TRIAL_SELF_SERVICE_ENABLED ? (
        <button
          type="button"
          disabled
          title="Available soon — the Pro trial opens once the feature set is finalized."
          className="w-full text-sm font-medium text-white bg-purple-400 rounded-lg px-4 py-2.5 cursor-not-allowed opacity-70"
        >
          Start 30-day Pro trial — coming soon
        </button>
      ) : canStartTrial && missingProfile.length > 0 ? (
        // Pro-requires-contact-profile gate (backend enforces the same via 409
        // ContactProfileRequired — this branch just makes the path obvious).
        <div className="text-sm bg-amber-50 border border-amber-200 rounded-lg p-3 text-amber-900 dark:bg-amber-950/30 dark:border-amber-700/50 dark:text-amber-200">
          <span className="font-medium">Pro requires a contact address and company name.</span>{" "}
          Missing: {missingProfile.join(" and ")}. Set it under{" "}
          <Link href="/settings/tenant/contact" className="font-medium text-purple-700 hover:underline dark:text-purple-300">
            Contact
          </Link>{" "}
          so we can reach and identify you for service or security matters — then start your trial here.
        </div>
      ) : canStartTrial && !confirming ? (
        <button
          type="button"
          onClick={() => setConfirming(true)}
          className="w-full text-sm font-medium text-white bg-purple-600 rounded-lg px-4 py-2.5 hover:bg-purple-700 transition-colors"
        >
          Start 30-day Pro trial
        </button>
      ) : canStartTrial && confirming ? (
        <div className="flex items-center gap-2 text-sm">
          <span className="text-gray-600">One-time trial — start now?</span>
          <button
            type="button"
            onClick={async () => {
              const ok = await startTrial();
              if (ok) setConfirming(false);
            }}
            disabled={startingTrial}
            className="font-medium text-white bg-purple-600 rounded-lg px-3 py-1.5 hover:bg-purple-700 disabled:opacity-50 transition-colors"
          >
            {startingTrial ? "Starting…" : "Confirm"}
          </button>
          <button
            type="button"
            onClick={() => setConfirming(false)}
            disabled={startingTrial}
            className="text-gray-500 hover:text-gray-700"
          >
            Cancel
          </button>
        </div>
      ) : null}
      <p className="mt-3 text-xs text-gray-600">
        Curious how Pro will be sold?{" "}
        <a
          href={`${SITE_URL}/buy`}
          target="_blank"
          rel="noopener noreferrer"
          className="text-purple-700 hover:underline dark:text-purple-300"
        >
          View purchase options
        </a>
        .
      </p>
    </>
  ) : undefined;

  return (
    <div className="bg-white rounded-lg shadow">
      <SectionCardHeader
        tone="purple"
        iconPath="M5 3v4M3 5h4M6 17v4m-2-2h4m5-16l2.286 6.857L21 12l-5.714 2.143L13 21l-2.286-6.857L5 12l5.714-2.143L13 3z"
        title="Plan"
        subtitle="Your current plan and what Pro adds"
        docsPath={DOCS_PATHS.plan}
      />

      <div className="p-6">
        <PlanCards
          surface="portal"
          highlight={isPro ? "pro" : "community"}
          communityBadge={communityBadge}
          proBadge={proBadge}
          proPrice={isPro ? <span className="text-2xl font-bold text-purple-900">Active</span> : undefined}
          proCta={proCta}
        />

        <p className="text-xs text-gray-600 mt-5">
          {isPro && editionInfo.isTrial
            ? "When the trial ends, the tenant returns to Community automatically."
            : "Scope, pricing and timeline for Pro will be announced. Community stays free."}
        </p>
      </div>
    </div>
  );
}
