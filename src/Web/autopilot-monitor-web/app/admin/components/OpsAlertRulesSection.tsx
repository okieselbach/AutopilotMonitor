"use client";

import { useState } from "react";
import { type OpsAlertRule } from "../AdminConfigContext";
import { AUTO_ACTION_MODES, describeAutoActionWarning, type AutoActionMode } from "./excessiveEventAutoAction";

// All known ops event types grouped by category
const OPS_EVENT_TYPES: Record<string, string[]> = {
  Consent: [
    "ConsentFlowStarted",
    "ConsentFlowSuccess",
    "ConsentFlowFailed",
    "ConsentRedirectUriMismatch",
    // Dual app-reg homing flip (consent-driven auto-flip or manual endpoint). Backend helpers
    // RecordAppHomingFlipped{,WithEntraRoles}Async — the WithEntraRoles variant (Warning) reminds
    // the operator to re-assign app roles on the new enterprise app.
    "AppHomingFlipped",
    "AppHomingFlippedWithEntraRoles",
  ],
  Maintenance: [
    // Verdict-calibration drift radar (operator-only classifier diagnostics): a verdict path's
    // session share doubled over its 28d baseline, the silence share doubled, or the r6
    // fallthrough decides ≥20% of classifier verdicts. Warning-tier, once per episode
    // (tracker-deduped). Dual-register per memory feedback_ops_event_types_dual_register.
    "VerdictCalibrationDrift",
    "MaintenanceCompleted",
    "MaintenanceFailed",
    // Soft watchdog (Warning): a maintenance run completed but exceeded the 10min threshold and is
    // climbing toward the host's 60min functionTimeout. Backend helper RecordMaintenanceLongRunningAsync.
    "MaintenanceLongRunning",
    "OpsEventCleanup",
    "SessionTimeouts",
    // Hourly stalled-session sweep interleave (SessionSweepFunction, minute 30) — dual-register
    // per memory feedback_ops_event_types_dual_register. Backend helpers
    // RecordSessionSweep{Completed,Failed}Async. Completed is an hourly heartbeat (Info);
    // Failed fires only when the sweep's tenant enumeration itself blows up (Error).
    "SessionSweepCompleted",
    "SessionSweepFailed",
    // Cascade-delete maintenance (PR6) — dual-register per memory feedback_ops_event_types_dual_register.
    // Dispatched by SessionDeletionMaintenanceFunction; backend helpers in OpsEventService.cs are
    // RecordSessionDeletionMaintenance{LongRunning,LongRunningSevere,Failed,Completed,FanoutSkipped}Async +
    // RecordSessionDeletionStrandedQueuedAsync. The Completed + FanoutSkipped events landed with the
    // PR6 follow-up (F3) — they replaced the AuditLogs-based lifecycle audits that silently failed
    // because the schema requires a non-null PartitionKey (tenantId), but the maintenance lifecycle
    // events are global-scope.
    "SessionDeletionMaintenanceCompleted",
    "SessionDeletionMaintenanceLongRunning",
    "SessionDeletionMaintenanceLongRunningSevere",
    "SessionDeletionMaintenanceFailed",
    "SessionDeletionMaintenanceFanoutSkipped",
    // Run lifecycle + 50min run budget + lease serialization (manual-trigger feature):
    // Started marks run begin (timer or manual, details.triggeredBy), BudgetExceeded is the
    // clean self-stop at the 50min budget (Warning), SkippedLocked means another run held the
    // maintenance lease. Backend helpers RecordSessionDeletionMaintenance{Started,BudgetExceeded,SkippedLocked}Async.
    "SessionDeletionMaintenanceStarted",
    "SessionDeletionMaintenanceBudgetExceeded",
    "SessionDeletionMaintenanceSkippedLocked",
    "SessionDeletionStrandedQueued",
    // PR-B audit consolidation: the per-session deletion_poisoned tenant-audit moved here so
    // tenant admins see only the lifecycle endpoints (deletion_started/completed/restored).
    // Operators wire Telegram on this event + read it in the Session Cleanup admin page.
    "SessionDeletionPoisoned",
    // Critical-table backup (plan §PR1) — dual-register per memory feedback_ops_event_types_dual_register.
    // Dispatched by CriticalTableBackupQueueWorker + CriticalTableBackupTimerFunction.
    // Backend helpers: RecordCriticalTableBackup{Completed,Partial,Failed,SkippedLocked}Async.
    // Partial is a distinct type (Warning severity) so operators can wire a softer
    // Telegram rule than for Failed (Error severity, fatal manifest-write failure).
    "CriticalTableBackupCompleted",
    "CriticalTableBackupPartial",
    "CriticalTableBackupFailed",
    "CriticalTableBackupSkippedLocked",
    // PR2: GA restored a single row from a backup. Warning severity so operators
    // can wire a Telegram rule and see the audit in near-real-time.
    "BackupRowRestored",
    // Orphan cleanup sweep (Warning): events whose parent session no longer exists were
    // deleted during maintenance aggregation. Backend helper RecordOrphanEventsCleanedAsync.
    "OrphanEventsCleaned",
  ],
  // SLA breach evaluation (SlaBreachEvaluationService). Dual-register per memory
  // feedback_ops_event_types_dual_register. BreachNotification = Warning per tenant+type
  // (cooldown-gated), ConsecutiveFailures = Error (threshold crossed), EvaluationCompleted =
  // Info run summary. Backend helpers RecordSla{BreachNotification,ConsecutiveFailures,EvaluationCompleted}Async.
  Sla: [
    "SlaBreachNotification",
    "SlaConsecutiveFailures",
    "SlaEvaluationCompleted",
  ],
  Security: [
    "DeviceBlocked",
    // ExcessiveDataBlocked removed 2026-07-22 with the time-window auto-block that raised it.
    // Critical-tier auto-action: emitted when maintenance auto-blocks/kills a device
    // after its session crosses `excessiveEventAutoActionThreshold`. Dual-register
    // per memory feedback_ops_event_types_dual_register so operators can wire a
    // dedicated Telegram rule independent of the warn-tier ExcessiveSessionEvents.
    "ExcessiveSessionEventsAutoActioned",
    "VersionBlocked",
    // Delivery confirmation: a Kill signal was actually SERVED to an agent (config or
    // telemetry channel) — as opposed to DeviceBlocked/VersionBlocked which fire on rule
    // creation. Emitted by KillSwitchEvaluator, throttled 24h per tenant+serial+pattern.
    // Dual-register per memory feedback_ops_event_types_dual_register.
    "KillSignalDelivered",
    "EmbeddedCertExpiringSoon",
    "EmbeddedCertExpiringUrgent",
    "EmbeddedCertExpired",
    "EmbeddedCertBundleEmpty",
    "SignalRConnectionsHigh",
    "SignalRConnectionsCritical",
    "SignalRMessagesHigh",
    "SignalRMessagesCritical",
    "PoisonQueueBacklogHigh",
    "PoisonQueueBacklogCritical",
  ],
  // TenantOffboardingFailed is dispatched by TenantOffboardingWorker when the async cascade fails
  // closed (drain timeout, KillSwitch active, Poisoned session, ETag/CAS exhaustion, SafeWipe verify
  // abort, etc.). Marker remains in Failed state until operator action — this event is the
  // Telegram-routable signal that something needs human attention. Plan Rev-4 Q2 + Rev-9 §11 PR3.
  // OffboardingFeedbackReceived fires when a departing admin submits free-form feedback
  // in the drain-barrier banner. Information-tier — wire Telegram to get pinged when
  // someone leaves a comment so you can read it promptly.
  // TenantTrialExpiring/Expired — dual-register per memory feedback_ops_event_types_dual_register.
  // Dispatched by TrialExpirySweepFunction (daily 03:30 UTC, informational — enforcement is
  // read-time). Expired is Warning (conversion moment: tenant degraded to Community); Expiring
  // is Info (≤3-day heads-up, re-emitted daily until expiry).
  Tenant: [
    "TenantOffboarded",
    "TenantOffboardingFailed",
    "OffboardingFeedbackReceived",
    "TenantTrialExpiring",
    "TenantTrialExpired",
    // F3 regression radar: an analyze rule's 7d hit rate rose ≥2× over its 28d baseline
    // (Wilson-separated). Warning-tier, once per episode (tracker-deduped). Dual-register
    // per memory feedback_ops_event_types_dual_register.
    "RuleFrequencyRegression",
    // App-version duration regression radar: an app's newest version installs with a median
    // duration ≥2× (and ≥5 min absolute) over the previous version. Warning-tier, once per
    // (app, version) episode (tracker-deduped). Dual-register per memory
    // feedback_ops_event_types_dual_register.
    "AppVersionDurationRegression",
    // Auto-activation audit: the tenant-auto-approve worker activated a new signup
    // (AutoApproveNewTenants enabled). Info-tier. Dual-register per memory
    // feedback_ops_event_types_dual_register.
    "TenantAutoApproved",
    // Plan downgrade / retention grace. Downgraded is dispatched by PlanManagementFunction
    // (RecordTenantPlanDowngradedAsync) when a GA plan mutation drops the effective edition
    // Pro → Community; the grace events by TrialExpirySweepFunction — Expiring during the
    // ≤3-day heads-up (re-emitted daily), Ended once via the 24h look-back, both only when
    // stored retention exceeds the Community cap (data actually at risk). All Warning-tier.
    // Dual-register per memory feedback_ops_event_types_dual_register.
    "TenantPlanDowngraded",
    "TenantRetentionGraceExpiring",
    "TenantRetentionGraceEnded",
    // Activation welcome mail. The send fails soft, so these are the only record that a
    // customer was onboarded without one: Skipped (Warning) = no address could be resolved,
    // Failed (Error) = the provider refused an address we had, Sent (Info) = confirmation.
    // Dual-register per memory feedback_ops_event_types_dual_register.
    "WelcomeEmailSent",
    "WelcomeEmailSkipped",
    "WelcomeEmailFailed",
    // A tenant admin turned on-demand diagnostics upload (the Collect Logs capability) on or
    // off — portal Settings, the Collect Logs quick-config dialog, MCP update_tenant_config,
    // or a config revert. Fires on the flip only. Backend
    // RecordDiagnosticsUploadConfigChangedAsync. Dual-register per memory
    // feedback_ops_event_types_dual_register.
    "DiagnosticsUploadEnabled",
    "DiagnosticsUploadDisabled",
    // Someone clicked Collect Logs on a session and switched on Hosted diagnostics upload
    // through the quick-config dialog (PUT ?intent=collect-logs). Replaces
    // DiagnosticsUploadEnabled for that path only — the targeted push signal.
    "CollectLogsQuickConfigEnabled",
  ],
  Agent: [
    "BlobStorageMissing",
    "BlobStorageUnreachable",
    "NewImeVersionDetected",
    "ExcessiveSessionEvents",
    // 48h session-age emergency break reported over the agent's emergency channel — the
    // "are we silently losing agents?" signal. Emitted once per session by
    // ReportAgentErrorFunction (RecordAgentEmergencyBreakAsync). Dual-register per memory
    // feedback_ops_event_types_dual_register.
    "AgentEmergencyBreak",
    // The running exe's SHA-256 differs from the backend-advertised hash for its version —
    // the field binary is not the published build (tamper / stale blob / uncommitted-tree
    // build). Emitted by ReportAgentErrorFunction (RecordAgentBinaryIntegrityMismatchAsync).
    // Dual-register per memory feedback_ops_event_types_dual_register.
    "AgentBinaryIntegrityMismatch",
    // CMTrace time-skew regression tripwire: a terminal session's IME-derived event
    // timestamps diverged from its other events by a clean 15-minute-grid multiple — the
    // per-line self-anchoring missed a case. Goal state: never fires. Emitted by
    // EventIngestProcessor (RecordCmTraceTimeSkewRegressionAsync). Dual-register per memory
    // feedback_ops_event_types_dual_register.
    "CmTraceTimeSkewRegression",
    // A portal user queued a server action for a live session — Collect Logs
    // (request_diagnostics), quick-config (rotate_config) or terminate_session. Emitted by
    // QueueSessionActionFunction (RecordSessionActionQueuedAsync); rule-engine actions are
    // not included. Dual-register per memory feedback_ops_event_types_dual_register.
    "SessionActionQueued",
  ],
  Platform: [
    // Azure Monitor alerts relayed through POST ops/alert-webhook (Common Alert Schema →
    // AzureMonitorAlertWebhookFunction → RecordAzureMonitorAlertAsync). One event type for ALL
    // Azure Monitor alert rules — the rule name travels in the message/details. Severity is
    // mapped Sev0→Critical, Sev1→Error, Sev2→Warning, Sev3/4→Info; both Fired and Resolved
    // notifications carry the same mapped severity, so a rule here delivers recovery pings too.
    // Dual-register per memory feedback_ops_event_types_dual_register.
    "AzureMonitorAlert",
  ],
};

const SEVERITIES = ["Info", "Warning", "Error", "Critical"];

const CATEGORY_COLORS: Record<string, string> = {
  Consent: "text-indigo-700 dark:text-indigo-300",
  Maintenance: "text-purple-700 dark:text-purple-300",
  Sla: "text-rose-700 dark:text-rose-300",
  Security: "text-orange-700 dark:text-orange-300",
  Tenant: "text-teal-700 dark:text-teal-300",
  Agent: "text-cyan-700 dark:text-cyan-300",
  Platform: "text-sky-700 dark:text-sky-300",
};

// Full rule list: merge saved rules with all known event types
function buildFullRules(savedRules: OpsAlertRule[]): OpsAlertRule[] {
  const ruleMap = new Map(savedRules.map(r => [r.eventType, r]));
  const fullRules: OpsAlertRule[] = [];
  for (const eventTypes of Object.values(OPS_EVENT_TYPES)) {
    for (const et of eventTypes) {
      const existing = ruleMap.get(et);
      fullRules.push(existing ?? { eventType: et, minSeverity: "Error", enabled: false });
    }
  }
  return fullRules;
}

interface OpsAlertRulesSectionProps {
  loadingConfig: boolean;
  savingOpsAlerts: boolean;
  adminConfigExists: boolean;
  opsAlertRules: OpsAlertRule[];
  opsAlertTelegramEnabled: boolean;
  opsAlertTelegramChatId: string;
  opsAlertTeamsEnabled: boolean;
  opsAlertTeamsWebhookUrl: string;
  opsAlertSlackEnabled: boolean;
  opsAlertSlackWebhookUrl: string;
  excessiveEventCountThreshold: number;
  excessiveEventAutoActionMode: AutoActionMode;
  excessiveEventAutoActionThreshold: number;
  excessiveEventAutoActionDurationHours: number;
  onSave: (
    rules: OpsAlertRule[],
    telegramEnabled: boolean, telegramChatId: string,
    teamsEnabled: boolean, teamsWebhookUrl: string,
    slackEnabled: boolean, slackWebhookUrl: string,
    excessiveEventCountThreshold: number,
    excessiveEventAutoActionMode: AutoActionMode,
    excessiveEventAutoActionThreshold: number,
    excessiveEventAutoActionDurationHours: number,
  ) => Promise<void>;
}

export function OpsAlertRulesSection({
  loadingConfig,
  savingOpsAlerts,
  adminConfigExists,
  opsAlertRules,
  opsAlertTelegramEnabled,
  opsAlertTelegramChatId,
  opsAlertTeamsEnabled,
  opsAlertTeamsWebhookUrl,
  opsAlertSlackEnabled,
  opsAlertSlackWebhookUrl,
  excessiveEventCountThreshold: excessiveEventCountThresholdProp,
  excessiveEventAutoActionMode: autoActionModeProp,
  excessiveEventAutoActionThreshold: autoActionThresholdProp,
  excessiveEventAutoActionDurationHours: autoActionDurationProp,
  onSave,
}: OpsAlertRulesSectionProps) {
  // Local state for editing, seeded from props
  const [rules, setRules] = useState<OpsAlertRule[]>(() => buildFullRules(opsAlertRules));
  const [telegramEnabled, setTelegramEnabled] = useState(opsAlertTelegramEnabled);
  const [telegramChatId, setTelegramChatId] = useState(opsAlertTelegramChatId);
  const [teamsEnabled, setTeamsEnabled] = useState(opsAlertTeamsEnabled);
  const [teamsWebhookUrl, setTeamsWebhookUrl] = useState(opsAlertTeamsWebhookUrl);
  const [slackEnabled, setSlackEnabled] = useState(opsAlertSlackEnabled);
  const [slackWebhookUrl, setSlackWebhookUrl] = useState(opsAlertSlackWebhookUrl);
  const [excessiveThreshold, setExcessiveThreshold] = useState(excessiveEventCountThresholdProp);
  const [autoActionMode, setAutoActionMode] = useState<AutoActionMode>(autoActionModeProp);
  const [autoActionThreshold, setAutoActionThreshold] = useState(autoActionThresholdProp);
  const [autoActionDuration, setAutoActionDuration] = useState(autoActionDurationProp);

  // Re-seed the local draft whenever a synced prop changes (config load/save),
  // replicating the previous sync effect's dependency-array comparison via the
  // adjust-during-render pattern (react.dev "storing information from previous renders").
  const syncedProps = [opsAlertRules, opsAlertTelegramEnabled, opsAlertTelegramChatId, opsAlertTeamsEnabled, opsAlertTeamsWebhookUrl, opsAlertSlackEnabled, opsAlertSlackWebhookUrl, excessiveEventCountThresholdProp, autoActionModeProp, autoActionThresholdProp, autoActionDurationProp];
  const [prevSyncedProps, setPrevSyncedProps] = useState(syncedProps);
  if (syncedProps.some((value, i) => !Object.is(value, prevSyncedProps[i]))) {
    setPrevSyncedProps(syncedProps);
    setRules(buildFullRules(opsAlertRules));
    setTelegramEnabled(opsAlertTelegramEnabled);
    setTelegramChatId(opsAlertTelegramChatId);
    setTeamsEnabled(opsAlertTeamsEnabled);
    setTeamsWebhookUrl(opsAlertTeamsWebhookUrl);
    setSlackEnabled(opsAlertSlackEnabled);
    setSlackWebhookUrl(opsAlertSlackWebhookUrl);
    setExcessiveThreshold(excessiveEventCountThresholdProp);
    setAutoActionMode(autoActionModeProp);
    setAutoActionThreshold(autoActionThresholdProp);
    setAutoActionDuration(autoActionDurationProp);
  }

  const toggleRule = (eventType: string) => {
    setRules(prev => prev.map(r => r.eventType === eventType ? { ...r, enabled: !r.enabled } : r));
  };

  const setSeverity = (eventType: string, minSeverity: string) => {
    setRules(prev => prev.map(r => r.eventType === eventType ? { ...r, minSeverity } : r));
  };

  const handleSave = () => {
    onSave(
      rules, telegramEnabled, telegramChatId, teamsEnabled, teamsWebhookUrl, slackEnabled, slackWebhookUrl,
      excessiveThreshold, autoActionMode, autoActionThreshold, autoActionDuration);
  };

  const autoActionWarning = describeAutoActionWarning(autoActionMode, autoActionThreshold, excessiveThreshold);

  const enabledRulesCount = rules.filter(r => r.enabled).length;
  const enabledProviders = [telegramEnabled, teamsEnabled, slackEnabled].filter(Boolean).length;

  return (
    <div className="space-y-6">
      {/* Alert Rules */}
      <div className="bg-gradient-to-br from-amber-50 to-orange-50 dark:from-gray-800 dark:to-gray-800 border-2 border-amber-300 dark:border-amber-700 rounded-lg shadow-lg">
        <div className="p-6 border-b border-amber-200 dark:border-amber-700 bg-gradient-to-r from-amber-100 to-orange-100 dark:from-amber-900/40 dark:to-orange-900/40">
          <div className="flex items-center space-x-2">
            <svg className="w-6 h-6 text-amber-600 dark:text-amber-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9" />
            </svg>
            <div>
              <h2 className="text-xl font-semibold text-amber-900 dark:text-amber-100">Alert Rules</h2>
              <p className="text-sm text-amber-600 dark:text-amber-300 mt-1">
                Select which ops events trigger alerts. {enabledRulesCount} rule(s) active, {enabledProviders} provider(s) enabled.
              </p>
            </div>
          </div>
        </div>
        <div className="p-6">
          {loadingConfig ? (
            <div className="text-center py-8">
              <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600 dark:border-amber-400 mx-auto"></div>
              <p className="mt-3 text-amber-800 dark:text-amber-200 text-sm">Loading configuration...</p>
            </div>
          ) : (
            <div className="space-y-6">
              {Object.entries(OPS_EVENT_TYPES).map(([category, eventTypes]) => (
                <div key={category}>
                  <h3 className={`text-sm font-semibold uppercase tracking-wide mb-2 ${CATEGORY_COLORS[category] ?? "text-gray-700 dark:text-gray-300"}`}>
                    {category}
                  </h3>
                  <div className="space-y-1">
                    {eventTypes.map(et => {
                      const rule = rules.find(r => r.eventType === et);
                      if (!rule) return null;
                      return (
                        <div key={et}>
                          <div className="flex items-center gap-3 py-1.5 px-3 rounded hover:bg-amber-100/50 dark:hover:bg-gray-700/50 transition-colors">
                            <label className="relative inline-flex items-center cursor-pointer">
                              <input
                                type="checkbox"
                                checked={rule.enabled}
                                onChange={() => toggleRule(et)}
                                className="sr-only peer"
                              />
                              <div className="w-9 h-5 bg-gray-300 dark:bg-gray-600 rounded-full peer peer-checked:bg-amber-500 dark:peer-checked:bg-amber-500 transition-colors after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:rounded-full after:h-4 after:w-4 after:transition-all peer-checked:after:translate-x-full"></div>
                            </label>
                            <span className={`flex-1 text-sm font-mono ${rule.enabled ? "text-gray-900 dark:text-gray-100" : "text-gray-400 dark:text-gray-500"}`}>
                              {et}
                            </span>
                            <select
                              value={rule.minSeverity}
                              onChange={(e) => setSeverity(et, e.target.value)}
                              disabled={!rule.enabled}
                              className="text-xs px-2 py-1 rounded border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 disabled:opacity-40 disabled:cursor-not-allowed focus:outline-none focus:ring-1 focus:ring-amber-500"
                            >
                              {SEVERITIES.map(s => (
                                <option key={s} value={s}>{s}+</option>
                              ))}
                            </select>
                          </div>
                          {et === "ExcessiveSessionEvents" && rule.enabled && (
                            <div className="ml-12 mt-1 mb-2 flex items-center gap-2">
                              <label className="text-xs text-amber-700 dark:text-amber-300 whitespace-nowrap">Threshold:</label>
                              <input
                                type="number"
                                min={0}
                                step={100}
                                value={excessiveThreshold}
                                onChange={(e) => setExcessiveThreshold(parseInt(e.target.value, 10) || 0)}
                                className="w-24 text-xs px-2 py-1 rounded border border-amber-300 dark:border-amber-600 bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:outline-none focus:ring-1 focus:ring-amber-500"
                              />
                              <span className="text-xs text-amber-600 dark:text-amber-400">events per session (0 = disabled)</span>
                            </div>
                          )}
                          {et === "ExcessiveSessionEventsAutoActioned" && (
                            // Auto-action controls always render under the rule row (independent of `rule.enabled`,
                            // which only governs Telegram routing — the auto-block/kill itself fires from
                            // maintenance regardless). Operators can opt in/out via the Off mode here.
                            <div className="ml-12 mt-1 mb-2 space-y-1.5">
                              <div className="flex items-center gap-2 flex-wrap">
                                <label className="text-xs text-orange-700 dark:text-orange-300 whitespace-nowrap">Auto-action:</label>
                                <select
                                  value={autoActionMode}
                                  onChange={(e) => setAutoActionMode(e.target.value as AutoActionMode)}
                                  className="text-xs px-2 py-1 rounded border border-orange-300 dark:border-orange-600 bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:outline-none focus:ring-1 focus:ring-orange-500"
                                >
                                  {AUTO_ACTION_MODES.map(m => (
                                    <option key={m} value={m}>{m}</option>
                                  ))}
                                </select>
                                {autoActionMode !== "Off" && (
                                  <>
                                    <label className="text-xs text-orange-700 dark:text-orange-300 whitespace-nowrap ml-2">Threshold:</label>
                                    <input
                                      type="number"
                                      min={0}
                                      step={100}
                                      value={autoActionThreshold}
                                      onChange={(e) => setAutoActionThreshold(parseInt(e.target.value, 10) || 0)}
                                      className="w-24 text-xs px-2 py-1 rounded border border-orange-300 dark:border-orange-600 bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:outline-none focus:ring-1 focus:ring-orange-500"
                                    />
                                    <label className="text-xs text-orange-700 dark:text-orange-300 whitespace-nowrap ml-2">Duration:</label>
                                    <input
                                      type="number"
                                      min={1}
                                      step={1}
                                      value={autoActionDuration}
                                      onChange={(e) => setAutoActionDuration(parseInt(e.target.value, 10) || 1)}
                                      className="w-16 text-xs px-2 py-1 rounded border border-orange-300 dark:border-orange-600 bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:outline-none focus:ring-1 focus:ring-orange-500"
                                    />
                                    <span className="text-xs text-orange-600 dark:text-orange-400">hours</span>
                                  </>
                                )}
                              </div>
                              {autoActionMode === "Off" ? (
                                <p className="text-xs text-gray-500 dark:text-gray-400">Off = warn only. Block stops uploads, Kill issues remote self-destruct.</p>
                              ) : autoActionWarning ? (
                                <p className="text-xs text-red-600 dark:text-red-400">{autoActionWarning}</p>
                              ) : null}
                            </div>
                          )}
                        </div>
                      );
                    })}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>

      {/* Providers */}
      <div className="bg-gradient-to-br from-sky-50 to-cyan-50 dark:from-gray-800 dark:to-gray-800 border-2 border-sky-300 dark:border-sky-700 rounded-lg shadow-lg">
        <div className="p-6 border-b border-sky-200 dark:border-sky-700 bg-gradient-to-r from-sky-100 to-cyan-100 dark:from-sky-900/40 dark:to-cyan-900/40">
          <div className="flex items-center space-x-2">
            <svg className="w-6 h-6 text-sky-600 dark:text-sky-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
            </svg>
            <div>
              <h2 className="text-xl font-semibold text-sky-900 dark:text-sky-100">Alert Providers</h2>
              <p className="text-sm text-sky-600 dark:text-sky-300 mt-1">Configure where alert notifications are delivered</p>
            </div>
          </div>
        </div>
        <div className="p-6 space-y-6">
          {/* Telegram */}
          <div className="space-y-3">
            <div className="flex items-center gap-3">
              <label className="relative inline-flex items-center cursor-pointer">
                <input
                  type="checkbox"
                  checked={telegramEnabled}
                  onChange={(e) => setTelegramEnabled(e.target.checked)}
                  className="sr-only peer"
                />
                <div className="w-9 h-5 bg-gray-300 dark:bg-gray-600 rounded-full peer peer-checked:bg-sky-500 dark:peer-checked:bg-sky-500 transition-colors after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:rounded-full after:h-4 after:w-4 after:transition-all peer-checked:after:translate-x-full"></div>
              </label>
              <span className="font-medium text-sky-900 dark:text-sky-100">Telegram</span>
            </div>
            {telegramEnabled && (
              <div className="ml-12">
                <label className="block text-sm text-sky-800 dark:text-sky-200 mb-1">Chat ID</label>
                <input
                  type="text"
                  value={telegramChatId}
                  onChange={(e) => setTelegramChatId(e.target.value)}
                  placeholder="-1003785642894"
                  className="block w-full max-w-md px-3 py-2 border border-sky-300 dark:border-sky-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-sky-500 focus:border-sky-500 text-sm font-mono"
                />
                <p className="text-xs text-sky-600 dark:text-sky-400 mt-1">Telegram channel or group chat ID (negative number for groups)</p>
              </div>
            )}
          </div>

          <hr className="border-sky-200 dark:border-sky-700" />

          {/* Teams */}
          <div className="space-y-3">
            <div className="flex items-center gap-3">
              <label className="relative inline-flex items-center cursor-pointer">
                <input
                  type="checkbox"
                  checked={teamsEnabled}
                  onChange={(e) => setTeamsEnabled(e.target.checked)}
                  className="sr-only peer"
                />
                <div className="w-9 h-5 bg-gray-300 dark:bg-gray-600 rounded-full peer peer-checked:bg-sky-500 dark:peer-checked:bg-sky-500 transition-colors after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:rounded-full after:h-4 after:w-4 after:transition-all peer-checked:after:translate-x-full"></div>
              </label>
              <span className="font-medium text-sky-900 dark:text-sky-100">Microsoft Teams</span>
              <span className="text-xs px-2 py-0.5 rounded bg-sky-200 dark:bg-sky-800 text-sky-700 dark:text-sky-300">Workflow Webhook</span>
            </div>
            {teamsEnabled && (
              <div className="ml-12">
                <label className="block text-sm text-sky-800 dark:text-sky-200 mb-1">Webhook URL</label>
                <input
                  type="url"
                  value={teamsWebhookUrl}
                  onChange={(e) => setTeamsWebhookUrl(e.target.value)}
                  placeholder="https://prod-xx.westeurope.logic.azure.com/..."
                  className="block w-full px-3 py-2 border border-sky-300 dark:border-sky-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-sky-500 focus:border-sky-500 text-sm"
                />
                <p className="text-xs text-sky-600 dark:text-sky-400 mt-1">Teams Workflow webhook URL (Adaptive Card format)</p>
              </div>
            )}
          </div>

          <hr className="border-sky-200 dark:border-sky-700" />

          {/* Slack */}
          <div className="space-y-3">
            <div className="flex items-center gap-3">
              <label className="relative inline-flex items-center cursor-pointer">
                <input
                  type="checkbox"
                  checked={slackEnabled}
                  onChange={(e) => setSlackEnabled(e.target.checked)}
                  className="sr-only peer"
                />
                <div className="w-9 h-5 bg-gray-300 dark:bg-gray-600 rounded-full peer peer-checked:bg-sky-500 dark:peer-checked:bg-sky-500 transition-colors after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:rounded-full after:h-4 after:w-4 after:transition-all peer-checked:after:translate-x-full"></div>
              </label>
              <span className="font-medium text-sky-900 dark:text-sky-100">Slack</span>
              <span className="text-xs px-2 py-0.5 rounded bg-sky-200 dark:bg-sky-800 text-sky-700 dark:text-sky-300">Incoming Webhook</span>
            </div>
            {slackEnabled && (
              <div className="ml-12">
                <label className="block text-sm text-sky-800 dark:text-sky-200 mb-1">Webhook URL</label>
                <input
                  type="url"
                  value={slackWebhookUrl}
                  onChange={(e) => setSlackWebhookUrl(e.target.value)}
                  placeholder="https://hooks.slack.com/services/..."
                  className="block w-full px-3 py-2 border border-sky-300 dark:border-sky-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-sky-500 focus:border-sky-500 text-sm"
                />
                <p className="text-xs text-sky-600 dark:text-sky-400 mt-1">Slack Incoming Webhook URL</p>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* Save button */}
      <div className="flex justify-end">
        <button
          onClick={handleSave}
          disabled={savingOpsAlerts || loadingConfig || !adminConfigExists}
          className="px-6 py-2.5 rounded-lg font-medium text-white bg-amber-600 hover:bg-amber-700 dark:bg-amber-500 dark:hover:bg-amber-600 disabled:opacity-50 disabled:cursor-not-allowed transition-colors shadow-md"
        >
          {savingOpsAlerts ? "Saving..." : "Save Alert Configuration"}
        </button>
      </div>
    </div>
  );
}
