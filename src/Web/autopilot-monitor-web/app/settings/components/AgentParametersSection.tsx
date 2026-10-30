"use client";

import SaveResetBar from "./SaveResetBar";
import ReadOnlyFieldset from "./ReadOnlyFieldset";
import { SectionCardHeader } from "@/components/SectionCardHeader";
import { DOCS_PATHS } from "@/lib/docsPaths";

interface AgentParametersSectionProps {
  selfDestructOnComplete: boolean;
  setSelfDestructOnComplete: (value: boolean) => void;
  keepLogFile: boolean;
  setKeepLogFile: (value: boolean) => void;
  rebootOnComplete: boolean;
  setRebootOnComplete: (value: boolean) => void;
  rebootDelaySeconds: number;
  setRebootDelaySeconds: (value: number) => void;
  enableGeoLocation: boolean;
  setEnableGeoLocation: (value: boolean) => void;
  enableTimezoneAutoSet: boolean;
  setEnableTimezoneAutoSet: (value: boolean) => void;
  enableDoGroupIdAutoSet: boolean;
  setEnableDoGroupIdAutoSet: (value: boolean) => void;
  keepAwakeDuringUserEsp: boolean;
  setKeepAwakeDuringUserEsp: (value: boolean) => void;
  enableImeMatchLog: boolean;
  setEnableImeMatchLog: (value: boolean) => void;
  enableGatherRuleDebugLog: boolean;
  setEnableGatherRuleDebugLog: (value: boolean) => void;
  logLevel: string;
  setLogLevel: (value: string) => void;
  showScriptOutput: boolean;
  setShowScriptOutput: (value: boolean) => void;
  showEnrollmentSummary: boolean;
  setShowEnrollmentSummary: (value: boolean) => void;
  enrollmentSummaryTimeoutSeconds: number;
  setEnrollmentSummaryTimeoutSeconds: (value: number) => void;
  enrollmentSummaryBrandingImageUrl: string;
  setEnrollmentSummaryBrandingImageUrl: (value: string) => void;
  enrollmentSummaryLaunchRetrySeconds: number;
  setEnrollmentSummaryLaunchRetrySeconds: (value: number) => void;
  onSave: () => Promise<void> | void;
  onReset: () => void;
  saving: boolean;
  /** Read-only viewer (Operator): settings visible but inert, no Save/Reset bar. */
  readOnly?: boolean;
}

export default function AgentParametersSection({
  selfDestructOnComplete,
  setSelfDestructOnComplete,
  keepLogFile,
  setKeepLogFile,
  rebootOnComplete,
  setRebootOnComplete,
  rebootDelaySeconds,
  setRebootDelaySeconds,
  enableGeoLocation,
  setEnableGeoLocation,
  enableTimezoneAutoSet,
  setEnableTimezoneAutoSet,
  enableDoGroupIdAutoSet,
  setEnableDoGroupIdAutoSet,
  keepAwakeDuringUserEsp,
  setKeepAwakeDuringUserEsp,
  enableImeMatchLog,
  setEnableImeMatchLog,
  enableGatherRuleDebugLog,
  setEnableGatherRuleDebugLog,
  logLevel,
  setLogLevel,
  showScriptOutput,
  setShowScriptOutput,
  showEnrollmentSummary,
  setShowEnrollmentSummary,
  enrollmentSummaryTimeoutSeconds,
  setEnrollmentSummaryTimeoutSeconds,
  enrollmentSummaryBrandingImageUrl,
  setEnrollmentSummaryBrandingImageUrl,
  enrollmentSummaryLaunchRetrySeconds,
  setEnrollmentSummaryLaunchRetrySeconds,
  onSave,
  onReset,
  saving,
  readOnly = false,
}: AgentParametersSectionProps) {
  return (
    <div className="bg-white rounded-lg shadow">
      <SectionCardHeader
        tone="violet"
        iconPath="M12 6V4m0 2a2 2 0 100 4m0-4a2 2 0 110 4m-6 8a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4m6 6v10m6-2a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4"
        title="Agent Parameters"
        subtitle="Control agent behavior on enrolled devices. Changes take effect on the next agent config refresh."
        docsPath={DOCS_PATHS.agentParameters}
      />
      <div className="p-6 space-y-4">
        <ReadOnlyFieldset readOnly={readOnly}>
        <div className="space-y-4">

        {/* Self-Destruct */}
        <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors">
          <div>
            <p className="font-medium text-gray-900">Self-Destruct on Complete</p>
            <p className="text-sm text-gray-500">Remove Scheduled Task and all agent files when enrollment completes</p>
          </div>
          <button onClick={() => setSelfDestructOnComplete(!selfDestructOnComplete)}
            className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${selfDestructOnComplete ? 'bg-violet-500' : 'bg-gray-300'}`}>
            <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${selfDestructOnComplete ? 'translate-x-6' : 'translate-x-1'}`} />
          </button>
        </div>

        {/* Keep Log File */}
        {selfDestructOnComplete && (
          <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors ml-4">
            <div>
              <p className="font-medium text-gray-900">Keep Log File</p>
              <p className="text-sm text-gray-500">Preserve the agent log during self-destruct (all other files are removed)</p>
            </div>
            <button onClick={() => setKeepLogFile(!keepLogFile)}
              className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${keepLogFile ? 'bg-violet-500' : 'bg-gray-300'}`}>
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${keepLogFile ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </div>
        )}

        {/* Reboot on Complete */}
        <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors">
          <div>
            <p className="font-medium text-gray-900">Reboot on Complete</p>
            <p className="text-sm text-gray-500">Reboot the device after enrollment completes (and after self-destruct if enabled)</p>
          </div>
          <button onClick={() => setRebootOnComplete(!rebootOnComplete)}
            className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${rebootOnComplete ? 'bg-violet-500' : 'bg-gray-300'}`}>
            <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${rebootOnComplete ? 'translate-x-6' : 'translate-x-1'}`} />
          </button>
        </div>

        {rebootOnComplete && (
          <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors ml-4">
            <div>
              <p className="font-medium text-gray-900">Reboot Delay</p>
              <p className="text-sm text-gray-500">Seconds before reboot is initiated — gives the user time to see what is happening</p>
            </div>
            <div className="flex items-center gap-2">
              <input
                type="number"
                min={0}
                max={3600}
                value={rebootDelaySeconds}
                onChange={(e) => setRebootDelaySeconds(Math.max(0, parseInt(e.target.value) || 0))}
                className="w-20 px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 text-right focus:ring-2 focus:ring-violet-500 focus:border-violet-500"
              />
              <span className="text-sm text-gray-500 whitespace-nowrap">seconds</span>
            </div>
          </div>
        )}

        {/* Geo Location */}
        <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors">
          <div>
            <p className="font-medium text-gray-900">Geo-Location Detection</p>
            <p className="text-sm text-gray-500">Capture device location, ISP and network info at enrollment start (queries external IP service)</p>
          </div>
          <button onClick={() => setEnableGeoLocation(!enableGeoLocation)}
            className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${enableGeoLocation ? 'bg-violet-500' : 'bg-gray-300'}`}>
            <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enableGeoLocation ? 'translate-x-6' : 'translate-x-1'}`} />
          </button>
        </div>

        {/* Timezone Auto-Set (sub-toggle of Geo-Location) */}
        {enableGeoLocation && (
          <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors ml-4">
            <div>
              <p className="font-medium text-gray-900">Set Timezone Automatically</p>
              <p className="text-sm text-gray-500">Set the device timezone based on IP geolocation result (uses tzutil /s)</p>
            </div>
            <button onClick={() => setEnableTimezoneAutoSet(!enableTimezoneAutoSet)}
              className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${enableTimezoneAutoSet ? 'bg-violet-500' : 'bg-gray-300'}`}>
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enableTimezoneAutoSet ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </div>
        )}

        {/* Delivery Optimization Group ID */}
        <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors">
          <div>
            <p className="font-medium text-gray-900">Set Delivery Optimization Group ID</p>
            <p className="text-sm text-gray-500">
              Set the DOGroupId policy from a network fingerprint — a deterministic GUID derived from the default gateway&apos;s IP and MAC address, so devices on the same local network peer with each other. Takes effect with DO Download Mode &quot;Group (2)&quot;; an existing DOGroupId or DOGroupIdSource policy (Intune/GPO) is never overwritten.
            </p>
          </div>
          <button onClick={() => setEnableDoGroupIdAutoSet(!enableDoGroupIdAutoSet)}
            className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${enableDoGroupIdAutoSet ? 'bg-violet-500' : 'bg-gray-300'}`}>
            <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enableDoGroupIdAutoSet ? 'translate-x-6' : 'translate-x-1'}`} />
          </button>
        </div>

        {/* Keep Awake During User-ESP */}
        <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors">
          <div>
            <p className="font-medium text-gray-900">Keep Awake During User-ESP</p>
            <p className="text-sm text-gray-500">Hold the device awake (system and display) during the User-ESP (Account Setup) phase so idle standby or sleep cannot stall app installs and account provisioning. Reboots are unaffected; the hold is released once the phase completes.</p>
          </div>
          <button onClick={() => setKeepAwakeDuringUserEsp(!keepAwakeDuringUserEsp)}
            className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${keepAwakeDuringUserEsp ? 'bg-violet-500' : 'bg-gray-300'}`}>
            <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${keepAwakeDuringUserEsp ? 'translate-x-6' : 'translate-x-1'}`} />
          </button>
        </div>

        {/* IME Match Log */}
        <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors">
          <div>
            <p className="font-medium text-gray-900">IME Pattern Match Log</p>
            <p className="text-sm text-gray-500">
              Write every matched IME log line to a local file for diagnostics
              {enableImeMatchLog && <span className="block text-xs text-gray-400 mt-0.5 font-mono">%ProgramData%\AutopilotMonitor\Logs\ime-pattern-matches.log</span>}
            </p>
          </div>
          <button onClick={() => setEnableImeMatchLog(!enableImeMatchLog)}
            className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${enableImeMatchLog ? 'bg-violet-500' : 'bg-gray-300'}`}>
            <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enableImeMatchLog ? 'translate-x-6' : 'translate-x-1'}`} />
          </button>
        </div>

        {/* Gather Rule Debug Log */}
        <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors">
          <div>
            <p className="font-medium text-gray-900">Gather Rule Debug Log</p>
            <p className="text-sm text-gray-500">
              Trace gather rule evaluation to a local file to diagnose rules that produce no timeline events. The trace never leaves the device.
              {enableGatherRuleDebugLog && <span className="block text-xs text-gray-400 mt-0.5 font-mono">%ProgramData%\AutopilotMonitor\Logs\gather-rules-debug.log</span>}
            </p>
          </div>
          <button onClick={() => setEnableGatherRuleDebugLog(!enableGatherRuleDebugLog)}
            className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${enableGatherRuleDebugLog ? 'bg-violet-500' : 'bg-gray-300'}`}>
            <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enableGatherRuleDebugLog ? 'translate-x-6' : 'translate-x-1'}`} />
          </button>
        </div>

        {/* Show Script Output */}
        <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors">
          <div className="flex-1">
            <p className="font-medium text-gray-900">Show Script Output (stdout)</p>
            <p className="text-sm text-gray-500">Show standard output from PowerShell scripts in the timeline. Disable if scripts may output sensitive data. Error output (stderr) is always shown.</p>
          </div>
          <button onClick={() => setShowScriptOutput(!showScriptOutput)}
            className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${showScriptOutput ? 'bg-violet-500' : 'bg-gray-300'}`}>
            <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${showScriptOutput ? 'translate-x-6' : 'translate-x-1'}`} />
          </button>
        </div>

        {/* Log Level */}
        <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors">
          <div>
            <p className="font-medium text-gray-900">Log Level</p>
            <p className="text-sm text-gray-500">Agent log verbosity — Info shows the enrollment flow, Debug adds detail, Verbose adds every detail, Trace adds each internal step (every watcher, poll and queue event)</p>
          </div>
          <select
            value={logLevel}
            onChange={(e) => setLogLevel(e.target.value)}
            className="px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 focus:ring-2 focus:ring-violet-500 focus:border-violet-500"
          >
            <option value="Info">Info</option>
            <option value="Debug">Debug</option>
            <option value="Verbose">Verbose</option>
            <option value="Trace">Trace</option>
          </select>
        </div>

        {/* Enrollment Summary Dialog */}
        <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors">
          <div className="flex-1">
            <p className="font-medium text-gray-900">Show Enrollment Summary</p>
            <p className="text-sm text-gray-500">Display a visual enrollment summary dialog to the end user after enrollment completes (success or failure). Requires the SummaryDialog companion to be deployed alongside the agent.</p>
          </div>
          <button onClick={() => setShowEnrollmentSummary(!showEnrollmentSummary)}
            className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${showEnrollmentSummary ? 'bg-violet-500' : 'bg-gray-300'}`}>
            <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${showEnrollmentSummary ? 'translate-x-6' : 'translate-x-1'}`} />
          </button>
        </div>

        {showEnrollmentSummary && (
          <>
            {/* Auto-close timeout */}
            <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors ml-4">
              <div>
                <p className="font-medium text-gray-900">Auto-Close Timeout</p>
                <p className="text-sm text-gray-500">Seconds until the summary dialog closes automatically. 0 = no auto-close.</p>
              </div>
              <div className="flex items-center gap-2">
                <input
                  type="number"
                  min={0}
                  max={3600}
                  value={enrollmentSummaryTimeoutSeconds}
                  onChange={(e) => setEnrollmentSummaryTimeoutSeconds(Math.max(0, parseInt(e.target.value) || 0))}
                  className="w-20 px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 text-right focus:ring-2 focus:ring-violet-500 focus:border-violet-500"
                />
                <span className="text-sm text-gray-500 whitespace-nowrap">seconds</span>
              </div>
            </div>

            {/* Launch Retry Timeout */}
            <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors ml-4">
              <div>
                <p className="font-medium text-gray-900">Launch Retry Timeout</p>
                <p className="text-sm text-gray-500">How long the agent retries launching the dialog when the desktop is locked by a credential UI (e.g. Windows Hello). 0 = no retry.</p>
              </div>
              <div className="flex items-center gap-2 ml-4">
                <input
                  type="number"
                  min={0}
                  max={3600}
                  value={enrollmentSummaryLaunchRetrySeconds}
                  onChange={(e) => setEnrollmentSummaryLaunchRetrySeconds(Math.min(3600, Math.max(0, parseInt(e.target.value) || 0)))}
                  className="w-20 px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 text-right focus:ring-2 focus:ring-violet-500 focus:border-violet-500"
                />
                <span className="text-sm text-gray-500 whitespace-nowrap">seconds</span>
              </div>
            </div>

            {/* Branding Image URL */}
            <div className="p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors ml-4">
              <div className="mb-2">
                <p className="font-medium text-gray-900">Branding Image URL</p>
                <p className="text-sm text-gray-500">Optional banner image at the top of the summary dialog. Recommended size: 540 x 80 px. Larger images will be center-cropped.</p>
              </div>
              <input
                type="url"
                value={enrollmentSummaryBrandingImageUrl}
                onChange={(e) => setEnrollmentSummaryBrandingImageUrl(e.target.value)}
                placeholder="https://example.com/branding-banner.png"
                className="w-full px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 focus:ring-2 focus:ring-violet-500 focus:border-violet-500"
              />
            </div>
          </>
        )}
        </div>
        </ReadOnlyFieldset>

        {!readOnly && <SaveResetBar onSave={onSave} onReset={onReset} saving={saving} />}
      </div>
    </div>
  );
}
