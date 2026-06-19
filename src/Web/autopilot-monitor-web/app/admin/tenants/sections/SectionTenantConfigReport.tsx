"use client";

import { useEffect, useState, useCallback } from 'react';
import { useAuth } from '../../../../contexts/AuthContext';
import { api } from '@/lib/api';
import { authenticatedFetch, TokenExpiredError } from '@/lib/authenticatedFetch';

// ── Types ────────────────────────────────────────────────────────────────────

interface TenantInfo {
  tenantId: string;
  domainName: string;
}

/** Full tenant configuration as returned by GET /api/config/{tenantId}. */
interface TenantConfig {
  tenantId: string;
  domainName?: string;
  lastUpdated?: string;
  updatedBy?: string;
  onboardedAt?: string;

  // Tenant status
  disabled?: boolean;
  disabledReason?: string;
  disabledUntil?: string;

  // Security
  rateLimitRequestsPerMinute?: number;
  customRateLimitRequestsPerMinute?: number | null;
  manufacturerWhitelist?: string;
  modelWhitelist?: string;
  validateAutopilotDevice?: boolean;
  validateCorporateIdentifier?: boolean;
  allowInsecureAgentRequests?: boolean;

  // Data management
  dataRetentionDays?: number;
  sessionTimeoutHours?: number;
  maxNdjsonPayloadSizeMB?: number;

  // Collectors
  enablePerformanceCollector?: boolean;
  performanceCollectorIntervalSeconds?: number;
  helloWaitTimeoutSeconds?: number;

  // Auth circuit breaker
  maxAuthFailures?: number | null;
  authFailureTimeoutMinutes?: number | null;
  agentMaxLifetimeMinutes?: number | null;

  // Agent behavior
  selfDestructOnComplete?: boolean | null;
  keepLogFile?: boolean | null;
  rebootOnComplete?: boolean | null;
  rebootDelaySeconds?: number | null;
  enableGeoLocation?: boolean | null;
  enableTimezoneAutoSet?: boolean | null;
  ntpServer?: string | null;
  enableImeMatchLog?: boolean | null;
  logLevel?: string | null;
  maxBatchSize?: number | null;
  showEnrollmentSummary?: boolean | null;
  enrollmentSummaryTimeoutSeconds?: number | null;
  enrollmentSummaryBrandingImageUrl?: string | null;
  enrollmentSummaryLaunchRetrySeconds?: number | null;
  showScriptOutput?: boolean | null;
  sendTraceEvents?: boolean;

  // Analyzers
  enableLocalAdminAnalyzer?: boolean | null;
  enableSoftwareInventoryAnalyzer?: boolean | null;
  localAdminAllowedAccountsJson?: string | null;

  // Plan tier
  planTier?: string;

  // Bootstrap & unrestricted
  bootstrapTokenEnabled?: boolean;
  unrestrictedModeEnabled?: boolean;
  unrestrictedMode?: boolean;

  // Diagnostics
  diagnosticsBlobSasUrl?: string | null;
  diagnosticsUploadMode?: string;
  diagnosticsLogPathsJson?: string | null;

  // Webhook notifications (new)
  webhookProviderType?: number;
  webhookUrl?: string | null;
  webhookNotifyOnSuccess?: boolean;
  webhookNotifyOnFailure?: boolean;
  webhookNotifyOnStart?: boolean;

  // Teams notifications (legacy)
  teamsWebhookUrl?: string | null;
  teamsNotifyOnSuccess?: boolean;
  teamsNotifyOnFailure?: boolean;
  teamsNotifyOnStart?: boolean;
}

// ── Defaults (mirrors C# TenantConfiguration defaults) ──────────────────────

const DEFAULTS: Record<string, unknown> = {
  disabled: false,
  rateLimitRequestsPerMinute: 100,
  customRateLimitRequestsPerMinute: null,
  manufacturerWhitelist: 'Dell*,HP*,Lenovo*,Microsoft Corporation',
  modelWhitelist: '*',
  validateAutopilotDevice: false,
  validateCorporateIdentifier: false,
  allowInsecureAgentRequests: false,
  dataRetentionDays: 90,
  sessionTimeoutHours: 5,
  maxNdjsonPayloadSizeMB: 5,
  enablePerformanceCollector: true,
  performanceCollectorIntervalSeconds: 30,
  helloWaitTimeoutSeconds: 30,
  maxAuthFailures: null,
  authFailureTimeoutMinutes: null,
  agentMaxLifetimeMinutes: null,
  selfDestructOnComplete: true,
  keepLogFile: false,
  rebootOnComplete: null,
  rebootDelaySeconds: null,
  enableGeoLocation: null,
  enableTimezoneAutoSet: null,
  ntpServer: 'time.windows.com',
  enableImeMatchLog: null,
  logLevel: null,
  maxBatchSize: null,
  showEnrollmentSummary: null,
  enrollmentSummaryTimeoutSeconds: null,
  enrollmentSummaryBrandingImageUrl: null,
  enrollmentSummaryLaunchRetrySeconds: null,
  showScriptOutput: true,
  sendTraceEvents: true,
  enableLocalAdminAnalyzer: null,
  enableSoftwareInventoryAnalyzer: null,
  localAdminAllowedAccountsJson: null,
  planTier: 'free',
  bootstrapTokenEnabled: false,
  unrestrictedModeEnabled: false,
  unrestrictedMode: false,
  diagnosticsBlobSasUrl: null,
  diagnosticsUploadMode: 'Off',
  diagnosticsLogPathsJson: null,
  webhookProviderType: 0,
  webhookUrl: null,
  webhookNotifyOnSuccess: true,
  webhookNotifyOnFailure: true,
  webhookNotifyOnStart: false,
  teamsWebhookUrl: null,
  teamsNotifyOnSuccess: true,
  teamsNotifyOnFailure: true,
  teamsNotifyOnStart: false,
};

// Runtime defaults (what the agent actually receives if tenant didn't set a value)
const RUNTIME_DEFAULTS: Record<string, unknown> = {
  uploadIntervalSeconds: 30,
  selfDestructOnComplete: true,
  keepLogFile: false,
  enableGeoLocation: true,
  enableTimezoneAutoSet: false,
  ntpServer: 'time.windows.com',
  enableImeMatchLog: false,
  maxAuthFailures: 5,
  authFailureTimeoutMinutes: 0,
  logLevel: 'Info',
  rebootOnComplete: false,
  rebootDelaySeconds: 10,
  showEnrollmentSummary: false,
  enrollmentSummaryTimeoutSeconds: 60,
  enrollmentSummaryLaunchRetrySeconds: 120,
  maxBatchSize: 100,
  diagnosticsUploadMode: 'Off',
  sendTraceEvents: true,
  unrestrictedMode: false,
  // Collectors
  enablePerformanceCollector: true,
  performanceIntervalSeconds: 30,
  collectorIdleTimeoutMinutes: 15,
  enableAgentSelfMetrics: true,
  agentSelfMetricsIntervalSeconds: 60,
  helloWaitTimeoutSeconds: 30,
  agentMaxLifetimeMinutes: 360,
  // Analyzers
  enableLocalAdminAnalyzer: true,
  enableSoftwareInventoryAnalyzer: false,
};

// ── Helpers ──────────────────────────────────────────────────────────────────

const WEBHOOK_PROVIDERS: Record<number, string> = {
  0: 'None',
  1: 'Teams (Legacy Connector)',
  2: 'Teams (Workflow Webhook)',
  10: 'Slack',
  20: 'Generic JSON',
};

function formatValue(val: unknown): string {
  if (val === null || val === undefined) return '—';
  if (typeof val === 'boolean') return val ? 'Yes' : 'No';
  if (typeof val === 'string') return val || '—';
  return String(val);
}

function isNonDefault(key: string, val: unknown, defaults: Record<string, unknown>): boolean {
  if (!(key in defaults)) return false;
  const def = defaults[key];
  // Both null/undefined → same
  if ((val === null || val === undefined) && (def === null || def === undefined)) return false;
  return val !== def;
}

function maskSasUrl(url: string | null | undefined): string {
  if (!url) return '—';
  try {
    const u = new URL(url);
    return `${u.origin}${u.pathname}?***`;
  } catch {
    return url.length > 40 ? url.substring(0, 40) + '...' : url;
  }
}

function formatDate(val: string | null | undefined): string {
  if (!val) return '—';
  try {
    return new Date(val).toLocaleString();
  } catch {
    return val;
  }
}

// ── Section component ────────────────────────────────────────────────────────

interface ConfigRowProps {
  label: string;
  value: unknown;
  configKey?: string;
  defaults?: Record<string, unknown>;
  masked?: boolean;
  isDate?: boolean;
}

function ConfigRow({ label, value, configKey, defaults, masked, isDate }: ConfigRowProps) {
  const highlight = configKey && defaults ? isNonDefault(configKey, value, defaults) : false;
  const displayVal = masked
    ? maskSasUrl(value as string)
    : isDate
    ? formatDate(value as string)
    : formatValue(value);

  return (
    <tr className={highlight ? 'bg-purple-50 dark:bg-purple-900/20' : ''}>
      <td className="py-1.5 px-3 text-sm text-gray-600 dark:text-gray-400 font-medium whitespace-nowrap">{label}</td>
      <td className="py-1.5 px-3 text-sm text-gray-900 dark:text-gray-100 font-mono break-all">
        {displayVal}
        {highlight && <span className="ml-2 text-xs text-purple-600 dark:text-purple-400 font-sans">(custom)</span>}
      </td>
    </tr>
  );
}

interface SectionProps {
  title: string;
  children: React.ReactNode;
}

function Section({ title, children }: SectionProps) {
  return (
    <div className="mb-6">
      <h3 className="text-sm font-semibold text-gray-700 dark:text-gray-300 uppercase tracking-wide mb-2">{title}</h3>
      <div className="bg-white dark:bg-gray-800 rounded-lg border border-gray-200 dark:border-gray-700 overflow-hidden">
        <table className="w-full">
          <tbody className="divide-y divide-gray-100 dark:divide-gray-700">{children}</tbody>
        </table>
      </div>
    </div>
  );
}

// ── Compute runtime parameters ───────────────────────────────────────────────

function computeRuntime(c: TenantConfig): Record<string, unknown> {
  return {
    uploadIntervalSeconds: 30,
    selfDestructOnComplete: c.selfDestructOnComplete ?? true,
    keepLogFile: c.keepLogFile ?? false,
    enableGeoLocation: c.enableGeoLocation ?? true,
    enableTimezoneAutoSet: c.enableTimezoneAutoSet ?? false,
    ntpServer: c.ntpServer ?? 'time.windows.com',
    enableImeMatchLog: c.enableImeMatchLog ?? false,
    maxAuthFailures: c.maxAuthFailures ?? 5,
    authFailureTimeoutMinutes: c.authFailureTimeoutMinutes ?? 0,
    logLevel: c.logLevel ?? 'Info',
    rebootOnComplete: c.rebootOnComplete ?? false,
    rebootDelaySeconds: c.rebootDelaySeconds ?? 10,
    showEnrollmentSummary: c.showEnrollmentSummary ?? false,
    enrollmentSummaryTimeoutSeconds: c.enrollmentSummaryTimeoutSeconds ?? 60,
    enrollmentSummaryBrandingImageUrl: c.enrollmentSummaryBrandingImageUrl ?? null,
    enrollmentSummaryLaunchRetrySeconds: c.enrollmentSummaryLaunchRetrySeconds ?? 120,
    maxBatchSize: c.maxBatchSize ?? 100,
    diagnosticsUploadEnabled: !!(c.diagnosticsBlobSasUrl && c.diagnosticsUploadMode && c.diagnosticsUploadMode !== 'Off'),
    diagnosticsUploadMode: c.diagnosticsUploadMode ?? 'Off',
    sendTraceEvents: c.sendTraceEvents ?? true,
    unrestrictedMode: c.unrestrictedMode ?? false,
    // Collectors
    enablePerformanceCollector: c.enablePerformanceCollector ?? true,
    performanceIntervalSeconds: c.performanceCollectorIntervalSeconds ?? 30,
    collectorIdleTimeoutMinutes: 15,
    enableAgentSelfMetrics: true,
    agentSelfMetricsIntervalSeconds: 60,
    helloWaitTimeoutSeconds: c.helloWaitTimeoutSeconds ?? 30,
    agentMaxLifetimeMinutes: c.agentMaxLifetimeMinutes ?? 360,
    // Analyzers
    enableLocalAdminAnalyzer: c.enableLocalAdminAnalyzer ?? true,
    enableSoftwareInventoryAnalyzer: c.enableSoftwareInventoryAnalyzer ?? false,
  };
}

// ── Main component ───────────────────────────────────────────────────────────

export function SectionTenantConfigReport() {
  const { user, getAccessToken } = useAuth();

  const [tenants, setTenants] = useState<TenantInfo[]>([]);
  const [selectedTenantId, setSelectedTenantId] = useState('');
  const [config, setConfig] = useState<TenantConfig | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadingTenants, setLoadingTenants] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Fetch tenant list
  useEffect(() => {
    if (!user?.isGlobalAdmin) return;
    const fetchTenants = async () => {
      try {
        setLoadingTenants(true);
        const response = await authenticatedFetch(api.config.all(), getAccessToken);
        if (response.ok) {
          const data = await response.json();
          const mapped: TenantInfo[] = data.map((t: { tenantId: string; domainName: string }) => ({
            tenantId: t.tenantId,
            domainName: t.domainName || '',
          }));
          mapped.sort((a, b) => (a.domainName || a.tenantId).localeCompare(b.domainName || b.tenantId));
          setTenants(mapped);
          // Don't auto-select — user must pick a tenant from the dropdown
        }
      } catch (err) {
        if (err instanceof TokenExpiredError) {
          setError('Session expired. Please refresh.');
        } else {
          console.error('Error fetching tenant list:', err);
        }
      } finally {
        setLoadingTenants(false);
      }
    };
    fetchTenants();
  }, [user?.isGlobalAdmin, getAccessToken]);

  // Fetch config for selected tenant
  const fetchConfig = useCallback(async () => {
    if (!selectedTenantId) return;
    try {
      setLoading(true);
      setError(null);
      const response = await authenticatedFetch(
        api.config.tenant(selectedTenantId),
        getAccessToken,
      );
      if (!response.ok) {
        throw new Error(`Failed to load config: ${response.status} ${response.statusText}`);
      }
      const data: TenantConfig = await response.json();
      setConfig(data);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        setError('Session expired. Please refresh.');
      } else {
        setError((err as Error).message);
      }
      setConfig(null);
    } finally {
      setLoading(false);
    }
  }, [selectedTenantId, getAccessToken]);

  useEffect(() => {
    fetchConfig();
  }, [fetchConfig]);

  // Computed runtime
  const runtime = config ? computeRuntime(config) : null;

  // Webhook display helper
  const webhookDisplay = config
    ? (() => {
        if (config.webhookUrl && config.webhookProviderType) {
          return { url: config.webhookUrl, provider: config.webhookProviderType, onSuccess: config.webhookNotifyOnSuccess, onFailure: config.webhookNotifyOnFailure, onStart: config.webhookNotifyOnStart };
        }
        if (config.teamsWebhookUrl) {
          return { url: config.teamsWebhookUrl, provider: 1, onSuccess: config.teamsNotifyOnSuccess, onFailure: config.teamsNotifyOnFailure, onStart: config.teamsNotifyOnStart };
        }
        return { url: null, provider: 0, onSuccess: true, onFailure: true, onStart: false };
      })()
    : null;

  const selectedTenant = tenants.find((t) => t.tenantId === selectedTenantId);

  return (
    <div className="max-w-5xl mx-auto px-4 py-8">
      {/* Header */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900 dark:text-gray-100">Tenant Config Report</h1>
          <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">
            Read-only overview of all tenant configuration and runtime parameters
          </p>
        </div>
        <div className="flex items-center gap-3">
          <label className="text-sm text-gray-500 whitespace-nowrap">Tenant:</label>
          <select
            value={selectedTenantId}
            onChange={(e) => setSelectedTenantId(e.target.value)}
            className="text-sm border border-gray-300 dark:border-gray-600 rounded-md px-2 py-1.5 max-w-xs bg-white dark:bg-gray-800 text-gray-900 dark:text-gray-100"
            disabled={loadingTenants}
          >
            {loadingTenants && <option>Loading...</option>}
            {!loadingTenants && <option value="">— Select tenant —</option>}
            {tenants.map((t) => (
              <option key={t.tenantId} value={t.tenantId}>
                {t.domainName
                  ? `${t.domainName} (${t.tenantId.substring(0, 8)}...)`
                  : t.tenantId}
              </option>
            ))}
          </select>
          <button
            onClick={fetchConfig}
            disabled={loading || !selectedTenantId}
            className="text-sm px-3 py-1.5 bg-purple-600 text-white rounded-md hover:bg-purple-700 disabled:opacity-50"
          >
            {loading ? 'Loading...' : 'Refresh'}
          </button>
        </div>
      </div>

      {/* Error */}
      {error && (
        <div className="mb-6 p-3 bg-red-50 dark:bg-red-900/30 border border-red-200 dark:border-red-800 rounded-lg text-sm text-red-700 dark:text-red-300">
          {error}
        </div>
      )}

      {/* Loading */}
      {loading && (
        <div className="text-center py-12 text-gray-500">
          <svg className="animate-spin h-8 w-8 mx-auto mb-3 text-purple-600" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
          </svg>
          Loading configuration...
        </div>
      )}

      {/* Config report */}
      {!loading && config && (
        <>
          {/* Tenant header card */}
          <div className="mb-6 p-4 bg-gradient-to-r from-purple-50 to-indigo-50 dark:from-purple-900/20 dark:to-indigo-900/20 rounded-lg border border-purple-200 dark:border-purple-800">
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-4 text-sm">
              <div>
                <span className="text-gray-500 dark:text-gray-400 block">Domain</span>
                <span className="font-medium text-gray-900 dark:text-gray-100">{config.domainName || '—'}</span>
              </div>
              <div>
                <span className="text-gray-500 dark:text-gray-400 block">Tenant ID</span>
                <span className="font-mono text-gray-900 dark:text-gray-100 text-xs">{config.tenantId}</span>
              </div>
              <div>
                <span className="text-gray-500 dark:text-gray-400 block">Last Updated</span>
                <span className="font-medium text-gray-900 dark:text-gray-100">{formatDate(config.lastUpdated)}</span>
              </div>
              <div>
                <span className="text-gray-500 dark:text-gray-400 block">Updated By</span>
                <span className="font-medium text-gray-900 dark:text-gray-100">{config.updatedBy || '—'}</span>
              </div>
            </div>
            {config.disabled && (
              <div className="mt-3 p-2 bg-red-100 dark:bg-red-900/40 rounded text-sm text-red-700 dark:text-red-300 font-medium">
                TENANT DISABLED{config.disabledReason ? `: ${config.disabledReason}` : ''}
                {config.disabledUntil ? ` (until ${formatDate(config.disabledUntil)})` : ''}
              </div>
            )}
          </div>

          {/* Legend */}
          <div className="mb-4 text-xs text-gray-500 dark:text-gray-400 flex items-center gap-2">
            <span className="inline-block w-3 h-3 bg-purple-50 dark:bg-purple-900/20 border border-purple-200 dark:border-purple-700 rounded" />
            <span>Purple rows = non-default value (custom)</span>
          </div>

          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
            {/* ────── LEFT: Tenant Configuration ────── */}
            <div>
              <h2 className="text-lg font-semibold text-gray-800 dark:text-gray-200 mb-4">Tenant Configuration</h2>

              <Section title="Tenant Status">
                <ConfigRow label="Disabled" value={config.disabled} configKey="disabled" defaults={DEFAULTS} />
                <ConfigRow label="Disabled Reason" value={config.disabledReason} />
                <ConfigRow label="Disabled Until" value={config.disabledUntil} isDate />
                <ConfigRow label="Onboarded At" value={config.onboardedAt} isDate />
                <ConfigRow label="Plan Tier" value={config.planTier || 'free'} configKey="planTier" defaults={DEFAULTS} />
              </Section>

              <Section title="Security & Validation">
                <ConfigRow label="Validate Autopilot Device" value={config.validateAutopilotDevice} configKey="validateAutopilotDevice" defaults={DEFAULTS} />
                <ConfigRow label="Validate Corporate Identifier" value={config.validateCorporateIdentifier} configKey="validateCorporateIdentifier" defaults={DEFAULTS} />
                <ConfigRow label="Allow Insecure Agent Requests" value={config.allowInsecureAgentRequests} configKey="allowInsecureAgentRequests" defaults={DEFAULTS} />
                <ConfigRow label="Rate Limit (req/min)" value={config.rateLimitRequestsPerMinute} configKey="rateLimitRequestsPerMinute" defaults={DEFAULTS} />
                <ConfigRow label="Custom Rate Limit" value={config.customRateLimitRequestsPerMinute} configKey="customRateLimitRequestsPerMinute" defaults={DEFAULTS} />
              </Section>

              <Section title="Hardware Whitelist">
                <ConfigRow label="Manufacturers" value={config.manufacturerWhitelist} configKey="manufacturerWhitelist" defaults={DEFAULTS} />
                <ConfigRow label="Models" value={config.modelWhitelist} configKey="modelWhitelist" defaults={DEFAULTS} />
              </Section>

              <Section title="Data Management">
                <ConfigRow label="Data Retention (days)" value={config.dataRetentionDays} configKey="dataRetentionDays" defaults={DEFAULTS} />
                <ConfigRow label="Session Timeout (hours)" value={config.sessionTimeoutHours} configKey="sessionTimeoutHours" defaults={DEFAULTS} />
                <ConfigRow label="Max NDJSON Payload (MB)" value={config.maxNdjsonPayloadSizeMB} configKey="maxNdjsonPayloadSizeMB" defaults={DEFAULTS} />
              </Section>

              <Section title="Agent Collectors">
                <ConfigRow label="Performance Collector" value={config.enablePerformanceCollector} configKey="enablePerformanceCollector" defaults={DEFAULTS} />
                <ConfigRow label="Perf. Interval (sec)" value={config.performanceCollectorIntervalSeconds} configKey="performanceCollectorIntervalSeconds" defaults={DEFAULTS} />
                <ConfigRow label="Hello Wait Timeout (sec)" value={config.helloWaitTimeoutSeconds} configKey="helloWaitTimeoutSeconds" defaults={DEFAULTS} />
              </Section>

              <Section title="Auth Circuit Breaker">
                <ConfigRow label="Max Auth Failures" value={config.maxAuthFailures} configKey="maxAuthFailures" defaults={DEFAULTS} />
                <ConfigRow label="Auth Failure Timeout (min)" value={config.authFailureTimeoutMinutes} configKey="authFailureTimeoutMinutes" defaults={DEFAULTS} />
                <ConfigRow label="Agent Max Lifetime (min)" value={config.agentMaxLifetimeMinutes} configKey="agentMaxLifetimeMinutes" defaults={DEFAULTS} />
              </Section>

              <Section title="Agent Behavior">
                <ConfigRow label="Self-Destruct On Complete" value={config.selfDestructOnComplete} configKey="selfDestructOnComplete" defaults={DEFAULTS} />
                <ConfigRow label="Keep Log File" value={config.keepLogFile} configKey="keepLogFile" defaults={DEFAULTS} />
                <ConfigRow label="Reboot On Complete" value={config.rebootOnComplete} configKey="rebootOnComplete" defaults={DEFAULTS} />
                <ConfigRow label="Reboot Delay (sec)" value={config.rebootDelaySeconds} configKey="rebootDelaySeconds" defaults={DEFAULTS} />
                <ConfigRow label="Geo-Location" value={config.enableGeoLocation} configKey="enableGeoLocation" defaults={DEFAULTS} />
                <ConfigRow label="Timezone Auto-Set" value={config.enableTimezoneAutoSet} configKey="enableTimezoneAutoSet" defaults={DEFAULTS} />
                <ConfigRow label="NTP Server" value={config.ntpServer} configKey="ntpServer" defaults={DEFAULTS} />
                <ConfigRow label="IME Match Log" value={config.enableImeMatchLog} configKey="enableImeMatchLog" defaults={DEFAULTS} />
                <ConfigRow label="Log Level" value={config.logLevel} configKey="logLevel" defaults={DEFAULTS} />
                <ConfigRow label="Max Batch Size" value={config.maxBatchSize} configKey="maxBatchSize" defaults={DEFAULTS} />
                <ConfigRow label="Show Script Output" value={config.showScriptOutput} configKey="showScriptOutput" defaults={DEFAULTS} />
                <ConfigRow label="Send Trace Events" value={config.sendTraceEvents} configKey="sendTraceEvents" defaults={DEFAULTS} />
              </Section>

              <Section title="Enrollment Summary">
                <ConfigRow label="Show Summary" value={config.showEnrollmentSummary} configKey="showEnrollmentSummary" defaults={DEFAULTS} />
                <ConfigRow label="Timeout (sec)" value={config.enrollmentSummaryTimeoutSeconds} configKey="enrollmentSummaryTimeoutSeconds" defaults={DEFAULTS} />
                <ConfigRow label="Branding Image URL" value={config.enrollmentSummaryBrandingImageUrl} configKey="enrollmentSummaryBrandingImageUrl" defaults={DEFAULTS} />
                <ConfigRow label="Launch Retry (sec)" value={config.enrollmentSummaryLaunchRetrySeconds} configKey="enrollmentSummaryLaunchRetrySeconds" defaults={DEFAULTS} />
              </Section>

              <Section title="Analyzers">
                <ConfigRow label="Local Admin Analyzer" value={config.enableLocalAdminAnalyzer} configKey="enableLocalAdminAnalyzer" defaults={DEFAULTS} />
                <ConfigRow label="Software Inventory Analyzer" value={config.enableSoftwareInventoryAnalyzer} configKey="enableSoftwareInventoryAnalyzer" defaults={DEFAULTS} />
                <ConfigRow label="Allowed Local Admin Accounts" value={config.localAdminAllowedAccountsJson} configKey="localAdminAllowedAccountsJson" defaults={DEFAULTS} />
              </Section>

              <Section title="Webhooks">
                <ConfigRow label="Provider" value={WEBHOOK_PROVIDERS[webhookDisplay?.provider ?? 0] ?? 'Unknown'} />
                <ConfigRow label="Webhook URL" value={webhookDisplay?.url} masked />
                <ConfigRow label="Notify On Start" value={webhookDisplay?.onStart} />
                <ConfigRow label="Notify On Success" value={webhookDisplay?.onSuccess} />
                <ConfigRow label="Notify On Failure" value={webhookDisplay?.onFailure} />
              </Section>

              <Section title="Diagnostics">
                <ConfigRow label="Upload Mode" value={config.diagnosticsUploadMode} configKey="diagnosticsUploadMode" defaults={DEFAULTS} />
                <ConfigRow label="Blob SAS URL" value={config.diagnosticsBlobSasUrl} masked />
                <ConfigRow label="Custom Log Paths" value={config.diagnosticsLogPathsJson} configKey="diagnosticsLogPathsJson" defaults={DEFAULTS} />
              </Section>

              <Section title="Feature Flags (Global)">
                <ConfigRow label="Bootstrap Token Enabled" value={config.bootstrapTokenEnabled} configKey="bootstrapTokenEnabled" defaults={DEFAULTS} />
                <ConfigRow label="Unrestricted Mode Enabled" value={config.unrestrictedModeEnabled} configKey="unrestrictedModeEnabled" defaults={DEFAULTS} />
                <ConfigRow label="Unrestricted Mode Active" value={config.unrestrictedMode} configKey="unrestrictedMode" defaults={DEFAULTS} />
              </Section>
            </div>

            {/* ────── RIGHT: Runtime Parameters (Agent Config) ────── */}
            <div>
              <h2 className="text-lg font-semibold text-gray-800 dark:text-gray-200 mb-4">Runtime Parameters (Agent)</h2>
              <p className="text-xs text-gray-500 dark:text-gray-400 mb-4">
                These are the effective values the agent receives at runtime, with defaults applied for unset fields.
              </p>

              {runtime && (
                <>
                  <Section title="Upload & Batching">
                    <ConfigRow label="Upload Interval (sec)" value={runtime.uploadIntervalSeconds} configKey="uploadIntervalSeconds" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Max Batch Size" value={runtime.maxBatchSize} configKey="maxBatchSize" defaults={RUNTIME_DEFAULTS} />
                  </Section>

                  <Section title="Agent Behavior">
                    <ConfigRow label="Self-Destruct" value={runtime.selfDestructOnComplete} configKey="selfDestructOnComplete" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Keep Log File" value={runtime.keepLogFile} configKey="keepLogFile" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Reboot On Complete" value={runtime.rebootOnComplete} configKey="rebootOnComplete" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Reboot Delay (sec)" value={runtime.rebootDelaySeconds} configKey="rebootDelaySeconds" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Geo-Location" value={runtime.enableGeoLocation} configKey="enableGeoLocation" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Timezone Auto-Set" value={runtime.enableTimezoneAutoSet} configKey="enableTimezoneAutoSet" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="NTP Server" value={runtime.ntpServer} configKey="ntpServer" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="IME Match Log" value={runtime.enableImeMatchLog} configKey="enableImeMatchLog" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Log Level" value={runtime.logLevel} configKey="logLevel" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Send Trace Events" value={runtime.sendTraceEvents} configKey="sendTraceEvents" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Unrestricted Mode" value={runtime.unrestrictedMode} configKey="unrestrictedMode" defaults={RUNTIME_DEFAULTS} />
                  </Section>

                  <Section title="Auth Circuit Breaker">
                    <ConfigRow label="Max Auth Failures" value={runtime.maxAuthFailures} configKey="maxAuthFailures" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Auth Failure Timeout (min)" value={runtime.authFailureTimeoutMinutes} configKey="authFailureTimeoutMinutes" defaults={RUNTIME_DEFAULTS} />
                  </Section>

                  <Section title="Enrollment Summary">
                    <ConfigRow label="Show Summary" value={runtime.showEnrollmentSummary} configKey="showEnrollmentSummary" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Timeout (sec)" value={runtime.enrollmentSummaryTimeoutSeconds} configKey="enrollmentSummaryTimeoutSeconds" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Branding Image URL" value={runtime.enrollmentSummaryBrandingImageUrl} />
                    <ConfigRow label="Launch Retry (sec)" value={runtime.enrollmentSummaryLaunchRetrySeconds} configKey="enrollmentSummaryLaunchRetrySeconds" defaults={RUNTIME_DEFAULTS} />
                  </Section>

                  <Section title="Collectors">
                    <ConfigRow label="Performance Collector" value={runtime.enablePerformanceCollector} configKey="enablePerformanceCollector" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Perf. Interval (sec)" value={runtime.performanceIntervalSeconds} configKey="performanceIntervalSeconds" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Idle Timeout (min)" value={runtime.collectorIdleTimeoutMinutes} configKey="collectorIdleTimeoutMinutes" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Agent Self-Metrics" value={runtime.enableAgentSelfMetrics} configKey="enableAgentSelfMetrics" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Self-Metrics Interval (sec)" value={runtime.agentSelfMetricsIntervalSeconds} configKey="agentSelfMetricsIntervalSeconds" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Hello Wait Timeout (sec)" value={runtime.helloWaitTimeoutSeconds} configKey="helloWaitTimeoutSeconds" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Agent Max Lifetime (min)" value={runtime.agentMaxLifetimeMinutes} configKey="agentMaxLifetimeMinutes" defaults={RUNTIME_DEFAULTS} />
                  </Section>

                  <Section title="Diagnostics">
                    <ConfigRow label="Upload Enabled" value={runtime.diagnosticsUploadEnabled} />
                    <ConfigRow label="Upload Mode" value={runtime.diagnosticsUploadMode} configKey="diagnosticsUploadMode" defaults={RUNTIME_DEFAULTS} />
                  </Section>

                  <Section title="Analyzers">
                    <ConfigRow label="Local Admin Analyzer" value={runtime.enableLocalAdminAnalyzer} configKey="enableLocalAdminAnalyzer" defaults={RUNTIME_DEFAULTS} />
                    <ConfigRow label="Software Inventory Analyzer" value={runtime.enableSoftwareInventoryAnalyzer} configKey="enableSoftwareInventoryAnalyzer" defaults={RUNTIME_DEFAULTS} />
                  </Section>
                </>
              )}
            </div>
          </div>
        </>
      )}

      {/* Empty state */}
      {!loading && !error && !config && (
        <div className="text-center py-12 text-gray-500 dark:text-gray-400">
          Select a tenant to view its configuration report.
        </div>
      )}
    </div>
  );
}
