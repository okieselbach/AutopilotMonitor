"use client";

import SaveBar from "./SaveBar";
import ReadOnlyFieldset from "./ReadOnlyFieldset";
import { SectionCardHeader } from "@/components/SectionCardHeader";
import { DOCS_PATHS } from "@/lib/docsPaths";
import {
  clampHelloWaitTimeoutSeconds,
  HELLO_WAIT_TIMEOUT_DEFAULT_SECONDS,
  HELLO_WAIT_TIMEOUT_MAX_SECONDS,
  HELLO_WAIT_TIMEOUT_MIN_SECONDS,
} from "../lib/helloWaitTimeout";

interface AgentCollectorsSectionProps {
  enablePerformanceCollector: boolean;
  setEnablePerformanceCollector: (value: boolean) => void;
  performanceCollectorInterval: number;
  setPerformanceCollectorInterval: (value: number) => void;
  helloWaitTimeoutSeconds: number;
  setHelloWaitTimeoutSeconds: (value: number) => void;
  enableRealmJoinWatcher: boolean;
  setEnableRealmJoinWatcher: (value: boolean) => void;
  enableConfigMgrWatcher: boolean;
  setEnableConfigMgrWatcher: (value: boolean) => void;
  onSave: () => Promise<void> | void;
  saving: boolean;
  /** Read-only viewer (Operator): settings visible but inert, no Save bar. */
  readOnly?: boolean;
}

export default function AgentCollectorsSection({
  enablePerformanceCollector,
  setEnablePerformanceCollector,
  performanceCollectorInterval,
  setPerformanceCollectorInterval,
  helloWaitTimeoutSeconds,
  setHelloWaitTimeoutSeconds,
  enableRealmJoinWatcher,
  setEnableRealmJoinWatcher,
  enableConfigMgrWatcher,
  setEnableConfigMgrWatcher,
  onSave,
  saving,
  readOnly = false,
}: AgentCollectorsSectionProps) {
  return (
    <div className="bg-white rounded-lg shadow">
      <SectionCardHeader
        tone="emerald"
        iconPath="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z"
        title="Agent Collectors"
        subtitle="Enable optional data collectors on enrolled devices. These generate additional telemetry traffic."
        docsPath={DOCS_PATHS.agentCollectors}
      />
      <div className="p-6 space-y-5">
        {/* notice={false}: the Agent Parameters card above already shows the read-only notice. */}
        <ReadOnlyFieldset readOnly={readOnly} notice={false}>
        <div className="space-y-5">
        {/* Performance Collector */}
        <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-emerald-200 transition-colors">
          <div className="flex-1">
            <div className="flex items-center space-x-2">
              <p className="font-medium text-gray-900">Performance Collector</p>
            </div>
            <p className="text-sm text-gray-500 mt-1">CPU, memory, disk metrics at configurable intervals</p>
            {enablePerformanceCollector && (
              <div className="mt-2">
                <div className="flex items-center space-x-2">
                  <span className="text-sm text-gray-600">Interval:</span>
                  <input
                    type="number"
                    min={30}
                    max={300}
                    value={performanceCollectorInterval}
                    onChange={(e) => setPerformanceCollectorInterval(parseInt(e.target.value) || 30)}
                    onBlur={() => setPerformanceCollectorInterval(Math.max(30, Math.min(300, performanceCollectorInterval)))}
                    className="w-20 px-2 py-1 border border-gray-300 rounded text-sm text-gray-900 focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500"
                  />
                  <span className="text-sm text-gray-500">seconds</span>
                </div>
                <p className="text-xs text-gray-400 mt-1">Minimum: 30 seconds, Maximum: 300 seconds (5 minutes)</p>
              </div>
            )}
          </div>
          <button
            onClick={() => setEnablePerformanceCollector(!enablePerformanceCollector)}
            className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${enablePerformanceCollector ? 'bg-emerald-500' : 'bg-gray-300'}`}
          >
            <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enablePerformanceCollector ? 'translate-x-6' : 'translate-x-1'}`} />
          </button>
        </div>

        {/* RealmJoin Watcher */}
        <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-emerald-200 transition-colors">
          <div className="flex-1">
            <p className="font-medium text-gray-900">RealmJoin Watcher</p>
            <p className="text-sm text-gray-500 mt-1">Tracks RealmJoin deployment state during provisioning — deployment phase, per-package start and completion, and the RealmJoin completion gate. Off by default; enable only for tenants that provision devices with RealmJoin, elsewhere it produces no signal.</p>
          </div>
          <button
            onClick={() => setEnableRealmJoinWatcher(!enableRealmJoinWatcher)}
            className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${enableRealmJoinWatcher ? 'bg-emerald-500' : 'bg-gray-300'}`}
          >
            <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enableRealmJoinWatcher ? 'translate-x-6' : 'translate-x-1'}`} />
          </button>
        </div>

        {/* ConfigMgr Watcher */}
        <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-emerald-200 transition-colors">
          <div className="flex-1">
            <p className="font-medium text-gray-900">ConfigMgr Watcher</p>
            <p className="text-sm text-gray-500 mt-1">Shows in Session Info whether the Configuration Manager client is installed, with its service state, version and site code. Off by default; enable for tenants that co-manage devices with Configuration Manager.</p>
          </div>
          <button
            onClick={() => setEnableConfigMgrWatcher(!enableConfigMgrWatcher)}
            className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${enableConfigMgrWatcher ? 'bg-emerald-500' : 'bg-gray-300'}`}
          >
            <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enableConfigMgrWatcher ? 'translate-x-6' : 'translate-x-1'}`} />
          </button>
        </div>

        {/* Hello Wait Timeout */}
        <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-emerald-200 transition-colors">
          <div className="flex-1">
            <div className="flex items-center space-x-2">
              <p className="font-medium text-gray-900">Hello Wait Timeout</p>
            </div>
            <p className="text-sm text-gray-500 mt-1">Seconds to wait for the Windows Hello wizard after ESP exit</p>
            <div className="mt-2">
              <div className="flex items-center space-x-2">
                <span className="text-sm text-gray-600">Timeout:</span>
                <input
                  type="number"
                  min={HELLO_WAIT_TIMEOUT_MIN_SECONDS}
                  max={HELLO_WAIT_TIMEOUT_MAX_SECONDS}
                  value={helloWaitTimeoutSeconds}
                  onChange={(e) => setHelloWaitTimeoutSeconds(parseInt(e.target.value) || HELLO_WAIT_TIMEOUT_DEFAULT_SECONDS)}
                  onBlur={() => setHelloWaitTimeoutSeconds(clampHelloWaitTimeoutSeconds(helloWaitTimeoutSeconds))}
                  className="w-20 px-2 py-1 border border-gray-300 rounded text-sm text-gray-900 focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500"
                />
                <span className="text-sm text-gray-500">seconds</span>
              </div>
              <p className="text-xs text-gray-400 mt-1">Minimum: 30 seconds, Maximum: 3600 seconds (1 hour). Values up to 300 seconds use the built-in 5-minute wait.</p>
            </div>
          </div>
        </div>

        </div>
        </ReadOnlyFieldset>

        {!readOnly && <SaveBar onSave={onSave} saving={saving} />}
      </div>
    </div>
  );
}
