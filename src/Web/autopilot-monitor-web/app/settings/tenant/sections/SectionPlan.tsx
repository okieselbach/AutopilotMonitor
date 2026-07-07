"use client";

import { useState } from "react";
import { useTenantConfig } from "../../TenantConfigContext";
import { editionLabel, trialDaysLeft } from "@/lib/edition";

/**
 * Self-service Enterprise trial switch. The Enterprise feature set is not finalized yet, so the
 * trial CTA is teased but not actionable. Flip this to `true` (a one-line change) to open the
 * self-service 30-day trial — the backend POST /trial endpoint and the startTrial() wiring are
 * already in place; only this gate keeps the button inert.
 */
const TRIAL_SELF_SERVICE_ENABLED = false;

/**
 * Plan section: shows the tenant's current plan (Community / Enterprise / Enterprise Trial)
 * and teases what the Enterprise plan adds.
 */
export function SectionPlan() {
  const { editionInfo, startTrial, startingTrial, user } = useTenantConfig();
  const [confirming, setConfirming] = useState(false);

  const isEnterprise = editionInfo.edition === "enterprise";
  const label = editionLabel(editionInfo);
  const daysLeft = editionInfo.isTrial ? trialDaysLeft(editionInfo.trialExpiresUtc) : 0;
  const trialConsumed = !isEnterprise && !editionInfo.trialAvailable;
  const canStartTrial =
    editionInfo.trialAvailable && (user?.isTenantAdmin === true || user?.isGlobalAdmin === true);

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

      <div className="p-6 space-y-8">
        {/* Current plan */}
        <div>
          <h3 className="text-sm font-medium text-gray-700 mb-2">Current plan</h3>
          <div className="flex items-center gap-3">
            <span
              className={`inline-flex items-center px-3 py-1 rounded-full text-sm font-semibold ${
                isEnterprise
                  ? "bg-purple-100 text-purple-800 border border-purple-300"
                  : "bg-gray-100 text-gray-700 border border-gray-300"
              }`}
            >
              {label}
            </span>
            {editionInfo.isTrial && editionInfo.trialExpiresUtc && (
              <span className="text-sm text-gray-500">
                Trial ends {new Date(editionInfo.trialExpiresUtc).toLocaleDateString()}
                {daysLeft > 0 && ` (${daysLeft} day${daysLeft === 1 ? "" : "s"} left)`}
              </span>
            )}
          </div>
          <p className="text-sm text-gray-500 mt-3">
            {isEnterprise
              ? editionInfo.isTrial
                ? "Your Enterprise trial is active — all Enterprise capabilities are unlocked. When the trial ends, the tenant returns to Community automatically."
                : "This tenant is on the Enterprise plan — all Enterprise capabilities are unlocked."
              : `This tenant is on the Community plan — the full product, free, with a ${editionInfo.entitlements.retentionCapDays}-day data retention window.`}
          </p>
        </div>

        {/* Enterprise teaser — only for Community tenants */}
        {!isEnterprise && (
          <div className="rounded-lg border border-purple-200 bg-purple-50/50 p-5">
            <div className="flex items-center gap-2 mb-2">
              <h3 className="text-base font-semibold text-purple-900">Enterprise</h3>
              <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-purple-100 text-purple-700 border border-purple-200">
                Coming soon
              </span>
            </div>
            <p className="text-sm text-gray-700 leading-relaxed">
              A commercial plan for organizations that need more than the preview can promise —
              reliability commitments and support, plus higher operating limits: data retention
              extended from {editionInfo.entitlements.retentionCapDays} to 365 days, raised portal
              and agent API rate limits, a larger AI (MCP) usage quota, and delegated (MSP)
              administration for managing multiple tenants from one place. The full feature set and
              pricing will be announced here.
            </p>

            <div className="mt-4">
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
                  className="text-sm font-medium text-white bg-purple-400 rounded-full px-4 py-1.5 cursor-not-allowed opacity-70"
                >
                  Start 30-day Enterprise trial — coming soon
                </button>
              ) : canStartTrial && !confirming ? (
                <button
                  type="button"
                  onClick={() => setConfirming(true)}
                  className="text-sm font-medium text-white bg-purple-600 rounded-full px-4 py-1.5 hover:bg-purple-700 transition-colors"
                >
                  Start 30-day Enterprise trial
                </button>
              ) : canStartTrial && confirming ? (
                <span className="flex items-center gap-2 text-sm">
                  <span className="text-gray-600">One-time trial — start now?</span>
                  <button
                    type="button"
                    onClick={async () => {
                      const ok = await startTrial();
                      if (ok) setConfirming(false);
                    }}
                    disabled={startingTrial}
                    className="font-medium text-white bg-purple-600 rounded-full px-3 py-1 hover:bg-purple-700 disabled:opacity-50 transition-colors"
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
                </span>
              ) : null}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
