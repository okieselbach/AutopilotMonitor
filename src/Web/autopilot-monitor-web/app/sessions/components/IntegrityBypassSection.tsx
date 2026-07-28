"use client";

import { useMemo } from "react";
import type { EnrollmentEvent } from "@/types";

interface IntegrityBypassSectionProps {
  events: EnrollmentEvent[];
  expanded: boolean;
  setExpanded: (v: boolean) => void;
}

interface LabConfigPayload {
  key_exists?: boolean;
  values?: Record<string, number>;
  active_bypasses?: string[];
}

interface MoSetupPayload {
  key_exists?: boolean;
  value?: number;
  flagged?: boolean;
}

interface PchcPayload {
  users_checked?: string[];
  users_with_flag?: string[];
  any_user_with_flag?: boolean;
}

interface SetupScriptEntry {
  path?: string;
  exists?: boolean;
  size_bytes?: number;
  last_modified_utc?: string | null;
}

interface CorrelationPayload {
  tpm_present_and_enabled?: boolean | null;
  secure_boot_enabled?: boolean | null;
}

interface ChecksPayload {
  lab_config?: LabConfigPayload;
  mo_setup?: MoSetupPayload;
  pchc_upgrade_eligibility?: PchcPayload;
  setup_scripts?: SetupScriptEntry[];
  correlation?: CorrelationPayload;
}

interface AnalysisPayload {
  severity?: string;
  finding?: string;
  findings?: string[];
  triggered_at?: string;
  checks?: ChecksPayload;
}

const severityBadgeColors: Record<string, string> = {
  critical: "bg-red-100 text-red-700 border-red-200",
  error: "bg-red-100 text-red-700 border-red-200",
  warning: "bg-amber-100 text-amber-700 border-amber-200",
  info: "bg-blue-100 text-blue-700 border-blue-200",
  clean: "bg-emerald-100 text-emerald-700 border-emerald-200",
};

function pickLatest(events: EnrollmentEvent[]): EnrollmentEvent | null {
  const analyses = events
    .filter((e) => e.eventType === "integrity_bypass_analysis")
    .sort((a, b) => a.sequence - b.sequence);
  return analyses.length > 0 ? analyses[analyses.length - 1] : null;
}

function severityLabel(sev?: string, findings?: string[]): string {
  if (!sev) return "unknown";
  const lower = sev.toLowerCase();
  if (lower === "info" && findings && findings.includes("no_bypass_indicators")) return "clean";
  return lower;
}

function describeTpm(v: boolean | null | undefined): string {
  if (v === true) return "TPM enabled";
  if (v === false) return "TPM missing / disabled";
  return "TPM status unknown";
}

function describeSecureBoot(v: boolean | null | undefined): string {
  if (v === true) return "SecureBoot enabled";
  if (v === false) return "SecureBoot disabled";
  return "SecureBoot status unknown";
}

export default function IntegrityBypassSection({ events, expanded, setExpanded }: IntegrityBypassSectionProps) {
  const primary = useMemo(() => pickLatest(events), [events]);

  if (!primary) return null;

  const payload = (primary.data ?? {}) as AnalysisPayload;
  const severity = severityLabel(payload.severity, payload.findings);
  const badgeClass = severityBadgeColors[severity] ?? "bg-gray-100 text-gray-700 border-gray-200";

  const activeBypasses = payload.checks?.lab_config?.active_bypasses ?? [];
  const labValues = payload.checks?.lab_config?.values ?? {};
  const moSetupFlagged = payload.checks?.mo_setup?.flagged === true;
  const pchcFlagged = payload.checks?.pchc_upgrade_eligibility?.any_user_with_flag === true;
  const pchcUsers = payload.checks?.pchc_upgrade_eligibility?.users_with_flag ?? [];
  const setupScripts = (payload.checks?.setup_scripts ?? []).filter((s) => s.exists === true);
  const corr = payload.checks?.correlation ?? {};

  return (
    <div className="bg-white shadow rounded-lg p-6 mb-6">
      <div
        onClick={() => setExpanded(!expanded)}
        className="flex items-start justify-between gap-2 w-full text-left cursor-pointer"
      >
        <div className="flex items-center flex-wrap gap-x-2 gap-y-1 min-w-0">
          <svg className="w-6 h-6 shrink-0 text-rose-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z" />
          </svg>
          <h2 className="text-xl font-semibold text-gray-900">Integrity Bypass Analyzer</h2>
          <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-semibold border ${badgeClass}`}>
            {severity.toUpperCase()}
          </span>
          {payload.finding && (
            <span className="text-xs text-gray-500 font-mono break-all">{payload.finding}</span>
          )}
        </div>
        <svg className={`w-5 h-5 shrink-0 text-gray-400 transition-transform duration-200 ${expanded ? 'rotate-90' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
        </svg>
      </div>

      {expanded && (
        <div className="mt-4 space-y-4 text-sm text-gray-700">
          {/* Correlation row — always visible to give context */}
          <div className="flex flex-wrap gap-2">
            <span className="inline-flex items-center px-2 py-1 rounded bg-gray-50 border border-gray-200 text-xs">
              {describeTpm(corr.tpm_present_and_enabled)}
            </span>
            <span className="inline-flex items-center px-2 py-1 rounded bg-gray-50 border border-gray-200 text-xs">
              {describeSecureBoot(corr.secure_boot_enabled)}
            </span>
          </div>

          {/* LabConfig bypass keys */}
          <div>
            <h3 className="text-sm font-semibold text-gray-800 mb-1">LabConfig — Install-time bypass keys</h3>
            {!payload.checks?.lab_config?.key_exists ? (
              <p className="text-xs text-gray-500">HKLM\SYSTEM\Setup\LabConfig not present — normal on standard Windows installs.</p>
            ) : activeBypasses.length === 0 ? (
              <p className="text-xs text-gray-500">Key exists but no bypass values set to 1 (possible manipulated install medium, currently dormant).</p>
            ) : (
              <ul className="space-y-1">
                {Object.entries(labValues).map(([name, value]) => {
                  const active = value === 1;
                  return (
                    <li key={name} className="flex items-center gap-2">
                      <span className={`inline-block w-2 h-2 rounded-full ${active ? 'bg-rose-500' : 'bg-gray-300'}`} />
                      <span className={`font-mono text-xs ${active ? 'text-rose-700' : 'text-gray-500'}`}>{name} = {value}</span>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>

          {/* MoSetup upgrade bypass */}
          <div>
            <h3 className="text-sm font-semibold text-gray-800 mb-1">MoSetup — In-place upgrade bypass</h3>
            <p className="text-xs">
              <span className="font-mono">AllowUpgradesWithUnsupportedTPMOrCPU</span>{" "}
              {moSetupFlagged ? (
                <span className="text-rose-700 font-semibold">= 1 (bypass active)</span>
              ) : (
                <span className="text-gray-500">not set / = 0</span>
              )}
            </p>
          </div>

          {/* PCHC UpgradeEligibility */}
          <div>
            <h3 className="text-sm font-semibold text-gray-800 mb-1">PCHC — Windows Update eligibility bypass</h3>
            {pchcFlagged ? (
              <div>
                <p className="text-xs text-rose-700">
                  <span className="font-mono">HKU\&lt;SID&gt;\SOFTWARE\Microsoft\PCHC\UpgradeEligibility = 1</span> found for {pchcUsers.length} user{pchcUsers.length === 1 ? '' : 's'}:
                </p>
                <ul className="ml-3 mt-1 space-y-0.5">
                  {pchcUsers.map((sid) => (
                    <li key={sid} className="font-mono text-xs text-gray-600">{sid}</li>
                  ))}
                </ul>
              </div>
            ) : (
              <p className="text-xs text-gray-500">No user hive has UpgradeEligibility = 1.</p>
            )}
          </div>

          {/* Setup scripts */}
          <div>
            <h3 className="text-sm font-semibold text-gray-800 mb-1">Setup Scripts — %WINDIR%\Setup\Scripts\</h3>
            {setupScripts.length === 0 ? (
              <p className="text-xs text-gray-500">No SetupComplete.cmd / ErrorHandler.cmd present.</p>
            ) : (
              <ul className="space-y-1">
                {setupScripts.map((s) => {
                  return (
                    <li key={s.path ?? ''} className="flex items-center gap-2">
                      <span className="inline-block w-2 h-2 rounded-full bg-amber-500" />
                      <span className="font-mono text-xs text-amber-800">{s.path}</span>
                      <span className="text-xs text-gray-500">({s.size_bytes ?? 0} bytes{s.last_modified_utc ? `, modified ${new Date(s.last_modified_utc).toISOString().replace('T', ' ').substring(0, 19)} UTC` : ""})</span>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>

          {/* Findings list */}
          {payload.findings && payload.findings.length > 0 && payload.findings[0] !== "no_bypass_indicators" && (
            <div>
              <h3 className="text-sm font-semibold text-gray-800 mb-1">Findings</h3>
              <ul className="list-disc list-inside text-xs text-gray-600 ml-2 space-y-0.5">
                {payload.findings.map((f) => (
                  <li key={f} className="font-mono">{f}</li>
                ))}
              </ul>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
