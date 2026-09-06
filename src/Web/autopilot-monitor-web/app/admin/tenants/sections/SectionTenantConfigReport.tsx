"use client";

import { useEffect, useState, useCallback } from 'react';
import { useAuth } from '../../../../contexts/AuthContext';
import { api } from '@/lib/api';
import { apiErrorText, fetchJson } from "@/lib/apiClient";
import type {
  AdminConfiguration,
  TenantConfiguration,
  TenantFeatureFlagsResponse,
} from "@/utils/wire-types.generated";
import {
  isNonDefault,
  RUNTIME_REPORT_SECTIONS,
  TENANT_REPORT_SECTIONS,
  UNRESOLVED,
  type RowKind,
  type RuntimeContext,
  type RuntimeSource,
} from './tenantConfigReportCatalog';

// ── Types ────────────────────────────────────────────────────────────────────

interface TenantInfo {
  tenantId: string;
  domainName: string;
}

// ── Helpers ──────────────────────────────────────────────────────────────────

const WEBHOOK_PROVIDERS: Record<number, string> = {
  0: 'None',
  1: 'Teams (Legacy Connector)',
  2: 'Teams (Workflow Webhook)',
  10: 'Slack',
  20: 'Generic JSON',
  30: 'Discord',
};

const SOURCE_LABELS: Record<RuntimeSource, string> = {
  tenant: 'tenant',
  admin: 'global',
  flags: 'entitlement',
  constant: 'fixed',
};

function formatValue(val: unknown): string {
  if (val === null || val === undefined) return '—';
  if (val === UNRESOLVED) return 'n/a (source not loaded)';
  if (typeof val === 'boolean') return val ? 'Yes' : 'No';
  if (typeof val === 'string') return val || '—';
  if (Array.isArray(val)) return val.length ? val.join(', ') : '—';
  return String(val);
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

function formatByKind(kind: RowKind | undefined, value: unknown): string {
  switch (kind) {
    case 'date':
      return formatDate(value as string);
    case 'masked':
      return maskSasUrl(value as string);
    case 'secret':
      return value ? 'configured (hidden)' : '—';
    default:
      return formatValue(value);
  }
}

// ── Row and section primitives ───────────────────────────────────────────────

interface ConfigRowProps {
  label: string;
  display: string;
  highlight?: boolean;
  sourceTag?: string;
}

function ConfigRow({ label, display, highlight, sourceTag }: ConfigRowProps) {
  return (
    <tr className={highlight ? 'bg-purple-50 dark:bg-purple-900/20' : ''}>
      <td className="py-1.5 px-3 text-sm text-gray-600 dark:text-gray-400 font-medium whitespace-nowrap">
        {label}
        {sourceTag && <span className="ml-2 text-xs text-gray-400 dark:text-gray-500 font-normal">{sourceTag}</span>}
      </td>
      <td className="py-1.5 px-3 text-sm text-gray-900 dark:text-gray-100 font-mono break-all">
        {display}
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

// ── Notification channels (the one non-scalar block) ─────────────────────────

interface ChannelView {
  id: string;
  name?: string;
  providerType?: number;
  url?: string;
  enabled?: boolean;
  notifyOnStart?: boolean;
  notifyOnSuccess?: boolean;
  notifyOnFailure?: boolean;
  notifyOnSlaEvents?: boolean;
}

/** Channel list when migrated, else the legacy single-webhook fields — mirrors TenantConfiguration.GetNotificationChannels. */
function resolveChannels(config: TenantConfiguration): ChannelView[] {
  if (config.notificationChannelsJson) {
    try {
      const parsed = JSON.parse(config.notificationChannelsJson);
      if (Array.isArray(parsed) && parsed.length > 0) return parsed as ChannelView[];
    } catch { /* malformed → fall back to legacy display */ }
  }
  if (config.webhookUrl && config.webhookProviderType) {
    return [{ id: 'legacy', name: 'Default (legacy)', providerType: config.webhookProviderType, url: config.webhookUrl, enabled: true, notifyOnStart: config.webhookNotifyOnStart, notifyOnSuccess: config.webhookNotifyOnSuccess, notifyOnFailure: config.webhookNotifyOnFailure, notifyOnSlaEvents: true }];
  }
  if (config.teamsWebhookUrl) {
    return [{ id: 'legacy', name: 'Default (legacy)', providerType: 1, url: config.teamsWebhookUrl, enabled: true, notifyOnStart: config.teamsNotifyOnStart, notifyOnSuccess: config.teamsNotifyOnSuccess, notifyOnFailure: config.teamsNotifyOnFailure, notifyOnSlaEvents: true }];
  }
  return [];
}

function ChannelRows({ config }: { config: TenantConfiguration }) {
  const channels = resolveChannels(config);
  if (channels.length === 0) {
    return <ConfigRow label="Provider" display={WEBHOOK_PROVIDERS[0]} />;
  }
  return (
    <>
      {channels.map((ch, i) => (
        <tr key={ch.id ?? i} className={i > 0 ? 'border-t-2 border-gray-200 dark:border-gray-600' : ''}>
          <td colSpan={2} className="p-0">
            <table className="w-full">
              <tbody className="divide-y divide-gray-100 dark:divide-gray-700">
                <ConfigRow label="Channel" display={`${ch.name || ch.id}${ch.enabled === false ? ' (disabled)' : ''}`} />
                <ConfigRow label="Provider" display={WEBHOOK_PROVIDERS[ch.providerType ?? 0] ?? 'Unknown'} />
                <ConfigRow label="Webhook URL" display={maskSasUrl(ch.url)} />
                <ConfigRow label="Notify On Start" display={formatValue(ch.notifyOnStart ?? false)} />
                <ConfigRow label="Notify On Success" display={formatValue(ch.notifyOnSuccess ?? false)} />
                <ConfigRow label="Notify On Failure" display={formatValue(ch.notifyOnFailure ?? false)} />
                <ConfigRow label="Notify On SLA" display={formatValue(ch.notifyOnSlaEvents ?? false)} />
              </tbody>
            </table>
          </td>
        </tr>
      ))}
    </>
  );
}

// ── Main component ───────────────────────────────────────────────────────────

export function SectionTenantConfigReport() {
  const { user, getAccessToken } = useAuth();

  const [tenants, setTenants] = useState<TenantInfo[]>([]);
  const [selectedTenantId, setSelectedTenantId] = useState('');
  const [config, setConfig] = useState<TenantConfiguration | null>(null);
  const [flags, setFlags] = useState<TenantFeatureFlagsResponse | null>(null);
  const [adminConfig, setAdminConfig] = useState<AdminConfiguration | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadingTenants, setLoadingTenants] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [sideError, setSideError] = useState<string | null>(null);

  // Fetch tenant list
  useEffect(() => {
    if (!user?.isGlobalAdmin) return;
    const fetchTenants = async () => {
      try {
        setLoadingTenants(true);
        // config/all is a bare array of tenant configurations (deliberately untyped, D-043).
        const data = await fetchJson<Array<{ tenantId: string; domainName?: string }>>(api.config.all(), getAccessToken);
        const mapped: TenantInfo[] = data.map((t) => ({ tenantId: t.tenantId, domainName: t.domainName || '' }));
        mapped.sort((a, b) => (a.domainName || a.tenantId).localeCompare(b.domainName || b.tenantId));
        setTenants(mapped);
        // Don't auto-select: the user must pick a tenant from the dropdown.
      } catch (err) {
        console.error('Error fetching tenant list:', err);
        setError(apiErrorText(err, 'Failed to load tenants'));
      } finally {
        setLoadingTenants(false);
      }
    };
    fetchTenants();
  }, [user?.isGlobalAdmin, getAccessToken]);

  // Fetch config plus its two runtime sources for the selected tenant. The full config is
  // the report; feature-flags (entitlement) and global config (operator knobs) are fail-soft —
  // their rows show "n/a" and a notice instead of blocking the whole report.
  const fetchConfig = useCallback(async () => {
    if (!selectedTenantId) return;
    try {
      setLoading(true);
      setError(null);
      setSideError(null);
      const [configResult, flagsResult, adminResult] = await Promise.allSettled([
        fetchJson<TenantConfiguration>(api.config.tenant(selectedTenantId), getAccessToken),
        fetchJson<TenantFeatureFlagsResponse>(api.config.featureFlags(selectedTenantId), getAccessToken),
        fetchJson<AdminConfiguration>(api.globalConfig.get(), getAccessToken),
      ]);
      if (configResult.status === 'rejected') throw configResult.reason;
      setConfig(configResult.value);
      setFlags(flagsResult.status === 'fulfilled' ? flagsResult.value : null);
      setAdminConfig(adminResult.status === 'fulfilled' ? adminResult.value : null);
      const sideFailures = [
        flagsResult.status === 'rejected' ? `feature flags: ${apiErrorText(flagsResult.reason)}` : null,
        adminResult.status === 'rejected' ? `global config: ${apiErrorText(adminResult.reason)}` : null,
      ].filter((s): s is string => s !== null);
      setSideError(sideFailures.length ? `Runtime sources unavailable — ${sideFailures.join('; ')}` : null);
    } catch (err) {
      setError(apiErrorText(err));
      setConfig(null);
      setFlags(null);
      setAdminConfig(null);
    } finally {
      setLoading(false);
    }
  }, [selectedTenantId, getAccessToken]);

  useEffect(() => {
    const run = async () => {
      await fetchConfig();
    };
    void run();
  }, [fetchConfig]);

  const runtimeCtx: RuntimeContext | null = config ? { config, adminConfig, flags } : null;

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
      {!loading && config && runtimeCtx && (
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
            {/* Effective entitlement — backend-resolved (feature-flags), not the stored plan tier */}
            {flags && (
              <div className="grid grid-cols-2 sm:grid-cols-4 gap-4 text-sm mt-3 pt-3 border-t border-purple-200 dark:border-purple-800">
                <div>
                  <span className="text-gray-500 dark:text-gray-400 block">Effective Edition</span>
                  <span className="font-medium text-gray-900 dark:text-gray-100">
                    {flags.edition}
                    <span className="ml-1 text-xs text-gray-500 dark:text-gray-400">via {flags.editionSource}</span>
                    {flags.isTrial && flags.trialExpiresUtc ? ` (trial until ${formatDate(flags.trialExpiresUtc)})` : ''}
                  </span>
                </div>
                <div>
                  <span className="text-gray-500 dark:text-gray-400 block">MCP Usage Plan</span>
                  <span className="font-medium text-gray-900 dark:text-gray-100">{flags.entitlements.mcpUsagePlan || '—'}</span>
                </div>
                <div>
                  <span className="text-gray-500 dark:text-gray-400 block">Delegated Tenant Slots</span>
                  <span className="font-medium text-gray-900 dark:text-gray-100">
                    {flags.entitlements.maxDelegatedTenants}
                    {!flags.entitlements.delegatedAdminAllowed && <span className="ml-1 text-xs text-gray-500 dark:text-gray-400">(delegation not included)</span>}
                  </span>
                </div>
                <div>
                  <span className="text-gray-500 dark:text-gray-400 block">Retention Cap (days)</span>
                  <span className="font-medium text-gray-900 dark:text-gray-100">{flags.entitlements.retentionCapDays}</span>
                </div>
              </div>
            )}
            {config.disabled && (
              <div className="mt-3 p-2 bg-red-100 dark:bg-red-900/40 rounded text-sm text-red-700 dark:text-red-300 font-medium">
                TENANT DISABLED{config.disabledReason ? `: ${config.disabledReason}` : ''}
                {config.disabledUntil ? ` (until ${formatDate(config.disabledUntil)})` : ''}
              </div>
            )}
            {config.mcpDisabled && (
              <div className="mt-3 p-2 bg-amber-100 dark:bg-amber-900/40 rounded text-sm text-amber-800 dark:text-amber-300 font-medium">
                MCP ACCESS DISABLED{config.mcpDisabledReason ? `: ${config.mcpDisabledReason}` : ''}
              </div>
            )}
          </div>

          {sideError && (
            <div className="mb-4 p-3 bg-amber-50 dark:bg-amber-900/30 border border-amber-200 dark:border-amber-800 rounded-lg text-sm text-amber-800 dark:text-amber-300">
              {sideError}
            </div>
          )}

          {/* Legend */}
          <div className="mb-4 text-xs text-gray-500 dark:text-gray-400 flex items-center gap-2">
            <span className="inline-block w-3 h-3 bg-purple-50 dark:bg-purple-900/20 border border-purple-200 dark:border-purple-700 rounded" />
            <span>Purple rows = non-default value (custom)</span>
          </div>

          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
            {/* ────── LEFT: Tenant Configuration ────── */}
            <div>
              <h2 className="text-lg font-semibold text-gray-800 dark:text-gray-200 mb-4">Tenant Configuration</h2>

              {TENANT_REPORT_SECTIONS.map(({ section, rows }) => (
                <Section key={section} title={section}>
                  {section === 'Webhooks' && <ChannelRows config={config} />}
                  {rows.map(({ key, row }) => {
                    const value = config[key as keyof TenantConfiguration];
                    return (
                      <ConfigRow
                        key={key}
                        label={row.label}
                        display={formatByKind(row.kind, value)}
                        highlight={'default' in row && isNonDefault(value, row.default)}
                      />
                    );
                  })}
                </Section>
              ))}
            </div>

            {/* ────── RIGHT: Runtime Parameters (Agent Config) ────── */}
            <div>
              <h2 className="text-lg font-semibold text-gray-800 dark:text-gray-200 mb-4">Runtime Parameters (Agent)</h2>
              <p className="text-xs text-gray-500 dark:text-gray-400 mb-4">
                The effective values the agent receives at runtime, with defaults applied for unset fields.
                The tag says where a value is resolved from: tenant config, global config, the tenant&apos;s entitlement, or a fixed backend constant.
              </p>

              {RUNTIME_REPORT_SECTIONS.map(({ section, rows }) => (
                <Section key={section} title={section}>
                  {rows.map(({ key, row }) => {
                    const value = row.value(runtimeCtx);
                    return (
                      <ConfigRow
                        key={key}
                        label={row.label}
                        sourceTag={SOURCE_LABELS[row.source]}
                        display={formatValue(value)}
                        highlight={'default' in row && value !== UNRESOLVED && isNonDefault(value, row.default)}
                      />
                    );
                  })}
                </Section>
              ))}
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
