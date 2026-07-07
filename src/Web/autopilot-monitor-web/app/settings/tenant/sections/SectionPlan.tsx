"use client";

import { useState } from "react";
import { useTenantConfig } from "../../TenantConfigContext";
import { trialDaysLeft } from "@/lib/edition";

/**
 * Self-service Enterprise trial switch. The Enterprise feature set is not finalized yet, so the
 * trial CTA is teased but not actionable. Flip this to `true` (a one-line change) to open the
 * self-service 30-day trial — the backend POST /trial endpoint and the startTrial() wiring are
 * already in place; only this gate keeps the button inert.
 */
const TRIAL_SELF_SERVICE_ENABLED = false;

function CheckIcon({ className }: { className: string }) {
  return (
    <svg className={className} fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={2.5}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
    </svg>
  );
}

function FeatureList({ features, checkClass }: { features: string[]; checkClass: string }) {
  return (
    <ul className="space-y-2.5">
      {features.map((f) => (
        <li key={f} className="flex items-start gap-2 text-sm text-gray-700">
          <CheckIcon className={`w-4 h-4 mt-0.5 shrink-0 ${checkClass}`} />
          <span>{f}</span>
        </li>
      ))}
    </ul>
  );
}

/**
 * Plan section: two side-by-side plan cards (Community and Enterprise). The tenant's current plan
 * is highlighted so the edition is obvious at a glance; the Enterprise card teases what it adds.
 */
export function SectionPlan() {
  const { editionInfo, startTrial, startingTrial, user } = useTenantConfig();
  const [confirming, setConfirming] = useState(false);

  const isEnterprise = editionInfo.edition === "enterprise";
  const retention = editionInfo.entitlements.retentionCapDays;
  const daysLeft = editionInfo.isTrial ? trialDaysLeft(editionInfo.trialExpiresUtc) : 0;
  const trialConsumed = !isEnterprise && !editionInfo.trialAvailable;
  const canStartTrial =
    editionInfo.trialAvailable && (user?.isTenantAdmin === true || user?.isGlobalAdmin === true);

  const communityFeatures = [
    "Live session monitoring & progress portal",
    "Full rules engine, including custom rules",
    "Fleet analytics, notifications & diagnostics",
    "AI integration (MCP) within usage limits",
    `${retention}-day data retention`,
    "Community support (GitHub)",
  ];

  const enterpriseFeatures = [
    `Extended data retention — 365 days (vs ${retention})`,
    "Higher portal & agent API rate limits",
    "Larger AI (MCP) usage quota",
    "Delegated (MSP) administration across tenants",
    "Reliability commitments & priority support",
  ];

  return (
    <div className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-purple-50 to-indigo-50">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-purple-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 3v4M3 5h4M6 17v4m-2-2h4m5-16l2.286 6.857L21 12l-5.714 2.143L13 21l-2.286-6.857L5 12l5.714-2.143L13 3z" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-gray-900">Plan</h2>
            <p className="text-sm text-gray-500 mt-1">Your current plan and what Enterprise adds</p>
          </div>
        </div>
      </div>

      <div className="p-6">
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-5">
          {/* Community card */}
          <div
            className={`rounded-xl border p-6 flex flex-col ${
              !isEnterprise ? "border-gray-800 ring-1 ring-gray-800 bg-gray-50/60" : "border-gray-200"
            }`}
          >
            <div className="flex items-start justify-between gap-2">
              <div>
                <h3 className="text-lg font-semibold text-gray-900">Community</h3>
                <p className="text-sm text-gray-500 mt-0.5">The full product, free</p>
              </div>
              {!isEnterprise && (
                <span className="inline-flex items-center px-2.5 py-1 rounded-full text-xs font-semibold bg-gray-900 text-white">
                  Current plan
                </span>
              )}
            </div>

            <div className="mt-4 mb-5">
              <span className="text-2xl font-bold text-gray-900">Free</span>
              <span className="text-sm text-gray-500"> — and stays free</span>
            </div>

            <FeatureList features={communityFeatures} checkClass="text-emerald-500" />
          </div>

          {/* Enterprise card */}
          <div
            className={`rounded-xl border p-6 flex flex-col ${
              isEnterprise ? "border-purple-500 ring-1 ring-purple-500 bg-purple-50/40" : "border-purple-200 bg-purple-50/20"
            }`}
          >
            <div className="flex items-start justify-between gap-2">
              <div>
                <h3 className="text-lg font-semibold text-purple-900">Enterprise</h3>
                <p className="text-sm text-gray-500 mt-0.5">Higher limits, support & MSP</p>
              </div>
              {isEnterprise ? (
                <span className="inline-flex items-center px-2.5 py-1 rounded-full text-xs font-semibold bg-purple-600 text-white">
                  {editionInfo.isTrial ? `Trial — ${daysLeft} day${daysLeft === 1 ? "" : "s"} left` : "Current plan"}
                </span>
              ) : (
                <span className="inline-flex items-center px-2.5 py-1 rounded-full text-xs font-medium bg-purple-100 text-purple-700 border border-purple-200">
                  Coming soon
                </span>
              )}
            </div>

            <div className="mt-4 mb-5">
              <span className="text-2xl font-bold text-purple-900">
                {isEnterprise ? "Active" : "Pricing TBA"}
              </span>
              {!isEnterprise && <span className="text-sm text-gray-500"> — to be announced</span>}
            </div>

            <p className="text-xs font-medium uppercase tracking-wide text-gray-400 mb-2.5">
              Everything in Community, plus
            </p>
            <FeatureList features={enterpriseFeatures} checkClass="text-purple-500" />

            {/* CTA — only meaningful while the tenant is on Community */}
            {!isEnterprise && (
              <div className="mt-auto pt-5">
                {trialConsumed ? (
                  <p className="text-sm text-gray-500">
                    This tenant has already used its Enterprise trial. To move to Enterprise,{" "}
                    <a
                      href="https://github.com/okieselbach/Autopilot-Monitor/issues"
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
                    title="Available soon — the Enterprise trial opens once the feature set is finalized."
                    className="w-full text-sm font-medium text-white bg-purple-400 rounded-lg px-4 py-2.5 cursor-not-allowed opacity-70"
                  >
                    Start 30-day Enterprise trial — coming soon
                  </button>
                ) : canStartTrial && !confirming ? (
                  <button
                    type="button"
                    onClick={() => setConfirming(true)}
                    className="w-full text-sm font-medium text-white bg-purple-600 rounded-lg px-4 py-2.5 hover:bg-purple-700 transition-colors"
                  >
                    Start 30-day Enterprise trial
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
              </div>
            )}
          </div>
        </div>

        <p className="text-xs text-gray-400 mt-5">
          {isEnterprise && editionInfo.isTrial
            ? "When the trial ends, the tenant returns to Community automatically."
            : "Scope, pricing and timeline for Enterprise will be announced. Community stays free."}
        </p>
      </div>
    </div>
  );
}
