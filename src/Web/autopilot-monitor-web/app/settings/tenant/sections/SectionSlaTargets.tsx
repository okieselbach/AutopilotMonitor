"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import SaveResetBar from "../../components/SaveResetBar";
import ReadOnlyFieldset from "../../components/ReadOnlyFieldset";
import { trackEvent } from "@/lib/appInsights";

export function SectionSlaTargets() {
  const {
    canEditConfig,
    slaTargetSuccessRate, setSlaTargetSuccessRate,
    slaTargetMaxDurationMinutes, setSlaTargetMaxDurationMinutes,
    slaTargetAppInstallSuccessRate, setSlaTargetAppInstallSuccessRate,
    slaNotifyOnSuccessRateBreach, setSlaNotifyOnSuccessRateBreach,
    slaSuccessRateNotifyThreshold, setSlaSuccessRateNotifyThreshold,
    slaNotifyOnDurationBreach, setSlaNotifyOnDurationBreach,
    slaNotifyOnAppInstallBreach, setSlaNotifyOnAppInstallBreach,
    slaNotifyOnConsecutiveFailures, setSlaNotifyOnConsecutiveFailures,
    slaConsecutiveFailureThreshold, setSlaConsecutiveFailureThreshold,
    handleSaveSlaTargets, handleResetSlaTargets,
    savingSection,
  } = useTenantConfig();

  return (
    <div className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-indigo-50 to-blue-50">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-gray-900">
              SLA Targets
            </h2>
            <p className="text-sm text-gray-500 mt-1">
              SLA targets and breach notification settings
            </p>
          </div>
        </div>
      </div>
      <div className="p-6 space-y-8">
      <ReadOnlyFieldset readOnly={!canEditConfig}>
      <div className="space-y-8">
      {/* SLA Targets */}
      <div>
        <p className="text-sm text-gray-500 mb-4">
          Define SLA targets for enrollment success rate, duration, and app installs.
          These are tracked on the SLA Compliance dashboard.
        </p>

        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Enrollment Success Rate (%)
            </label>
            <input
              type="number"
              min={0}
              max={100}
              step={0.1}
              value={slaTargetSuccessRate ?? ""}
              onChange={(e) => setSlaTargetSuccessRate(e.target.value ? Number(e.target.value) : null)}
              placeholder="e.g. 95.0"
              className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm"
            />
            <p className="text-xs text-gray-400 mt-1">Leave empty to disable</p>
          </div>

          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Max Duration P95 (minutes)
            </label>
            <input
              type="number"
              min={1}
              max={480}
              value={slaTargetMaxDurationMinutes ?? ""}
              onChange={(e) => setSlaTargetMaxDurationMinutes(e.target.value ? Number(e.target.value) : null)}
              placeholder="e.g. 60"
              className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm"
            />
            <p className="text-xs text-gray-400 mt-1">Leave empty to disable</p>
          </div>

          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              App Install Success Rate (%)
            </label>
            <input
              type="number"
              min={0}
              max={100}
              step={0.1}
              value={slaTargetAppInstallSuccessRate ?? ""}
              onChange={(e) => setSlaTargetAppInstallSuccessRate(e.target.value ? Number(e.target.value) : null)}
              placeholder="e.g. 98.0"
              className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm"
            />
            <p className="text-xs text-gray-400 mt-1">Requires 5+ installs per week to evaluate</p>
          </div>
        </div>
      </div>

      {/* Notification Subscriptions */}
      <div>
        <h3 className="text-lg font-semibold text-gray-900 mb-1">Breach Notifications</h3>
        <p className="text-sm text-gray-500 mb-4">
          Choose which SLA breaches trigger webhook notifications.
          Requires a webhook to be configured in Notifications settings.
        </p>

        <div className="space-y-4">
          {/* Success Rate Breach */}
          <div className="flex items-start gap-3 p-3 border border-gray-200 rounded-md">
            <input
              type="checkbox"
              checked={slaNotifyOnSuccessRateBreach}
              onChange={(e) => {
                trackEvent('sla_channel_toggled', { channel: 'success_rate_breach', enabled: e.target.checked });
                setSlaNotifyOnSuccessRateBreach(e.target.checked);
              }}
              className="mt-1 h-4 w-4 rounded border-gray-300 text-indigo-600"
            />
            <div className="flex-1">
              <div className="text-sm font-medium text-gray-700">Enrollment Success Rate Breach</div>
              <div className="text-xs text-gray-500">
                Notify when success rate drops below threshold
              </div>
              {slaNotifyOnSuccessRateBreach && (
                <div className="mt-2">
                  <label className="block text-xs text-gray-500 mb-1">
                    Warning threshold (defaults to target if empty)
                  </label>
                  <input
                    type="number"
                    min={0}
                    max={100}
                    step={0.1}
                    value={slaSuccessRateNotifyThreshold ?? ""}
                    onChange={(e) => setSlaSuccessRateNotifyThreshold(e.target.value ? Number(e.target.value) : null)}
                    placeholder={slaTargetSuccessRate != null ? `${slaTargetSuccessRate}` : "e.g. 90.0"}
                    className="w-40 border border-gray-300 rounded-md px-2 py-1 text-sm"
                  />
                </div>
              )}
            </div>
          </div>

          {/* Duration Breach */}
          <div className="flex items-start gap-3 p-3 border border-gray-200 rounded-md">
            <input
              type="checkbox"
              checked={slaNotifyOnDurationBreach}
              onChange={(e) => {
                trackEvent('sla_channel_toggled', { channel: 'duration_breach', enabled: e.target.checked });
                setSlaNotifyOnDurationBreach(e.target.checked);
              }}
              className="mt-1 h-4 w-4 rounded border-gray-300 text-indigo-600"
            />
            <div>
              <div className="text-sm font-medium text-gray-700">Duration Breach</div>
              <div className="text-xs text-gray-500">
                Notify when P95 enrollment duration exceeds target
              </div>
            </div>
          </div>

          {/* App Install Breach */}
          <div className="flex items-start gap-3 p-3 border border-gray-200 rounded-md">
            <input
              type="checkbox"
              checked={slaNotifyOnAppInstallBreach}
              onChange={(e) => {
                trackEvent('sla_channel_toggled', { channel: 'app_install_breach', enabled: e.target.checked });
                setSlaNotifyOnAppInstallBreach(e.target.checked);
              }}
              className="mt-1 h-4 w-4 rounded border-gray-300 text-indigo-600"
            />
            <div>
              <div className="text-sm font-medium text-gray-700">App Install Rate Breach</div>
              <div className="text-xs text-gray-500">
                Notify when app install success rate drops below target
              </div>
            </div>
          </div>

          {/* Consecutive Failures */}
          <div className="flex items-start gap-3 p-3 border border-gray-200 rounded-md">
            <input
              type="checkbox"
              checked={slaNotifyOnConsecutiveFailures}
              onChange={(e) => {
                trackEvent('sla_channel_toggled', { channel: 'consecutive_failures', enabled: e.target.checked });
                setSlaNotifyOnConsecutiveFailures(e.target.checked);
              }}
              className="mt-1 h-4 w-4 rounded border-gray-300 text-indigo-600"
            />
            <div className="flex-1">
              <div className="text-sm font-medium text-gray-700">Consecutive Failures</div>
              <div className="text-xs text-gray-500">
                Notify when multiple enrollments fail in a row
              </div>
              {slaNotifyOnConsecutiveFailures && (
                <div className="mt-2">
                  <label className="block text-xs text-gray-500 mb-1">
                    Failure threshold
                  </label>
                  <input
                    type="number"
                    min={2}
                    max={20}
                    value={slaConsecutiveFailureThreshold}
                    onChange={(e) => setSlaConsecutiveFailureThreshold(Number(e.target.value) || 5)}
                    className="w-24 border border-gray-300 rounded-md px-2 py-1 text-sm"
                  />
                </div>
              )}
            </div>
          </div>
        </div>
      </div>

      </div>
      </ReadOnlyFieldset>

      {canEditConfig && (
        <SaveResetBar
          onSave={() => {
            trackEvent('sla_targets_saved', {
              hasSuccessRateTarget: slaTargetSuccessRate != null,
              hasDurationTarget: slaTargetMaxDurationMinutes != null,
              hasAppInstallTarget: slaTargetAppInstallSuccessRate != null,
            });
            return handleSaveSlaTargets();
          }}
          onReset={() => {
            trackEvent('sla_targets_reset');
            return handleResetSlaTargets();
          }}
          saving={savingSection === "slaTargets"}
        />
      )}
      </div>
    </div>
  );
}
