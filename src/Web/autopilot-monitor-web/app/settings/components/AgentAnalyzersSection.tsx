"use client";

import { useState } from "react";
import SaveBar from "./SaveBar";
import ReadOnlyFieldset from "./ReadOnlyFieldset";
import { SectionCardHeader } from "@/components/SectionCardHeader";
import { DOCS_PATHS } from "@/lib/docsPaths";
import {
  BUILT_IN_GROUPS,
  formatAllowedEntry,
  isBuiltInName,
  isCoveredByBuiltIn,
  parseAllowedEntry,
  validateNewEntry,
  type AllowedRole,
} from "../lib/allowedAccounts";

interface AgentAnalyzersSectionProps {
  enableLocalAdminAnalyzer: boolean;
  setEnableLocalAdminAnalyzer: (value: boolean) => void;
  localAdminAllowedAccounts: string[];
  setLocalAdminAllowedAccounts: (value: string[]) => void;
  newAllowedAccount: string;
  setNewAllowedAccount: (value: string) => void;
  enableSoftwareInventoryAnalyzer: boolean;
  setEnableSoftwareInventoryAnalyzer: (value: boolean) => void;
  enableIntegrityBypassAnalyzer: boolean;
  setEnableIntegrityBypassAnalyzer: (value: boolean) => void;
  enableConsoleBypassDetection: boolean;
  setEnableConsoleBypassDetection: (value: boolean) => void;
  onSave: () => Promise<void> | void;
  saving: boolean;
  /** Unsaved edits of this card: the Save bar pins itself and enables Save. Left out, Save stays available. */
  unsaved?: boolean;
  /** Read-only viewer (Operator): analyzers visible but inert, no Save bar. */
  readOnly?: boolean;
}

const ROLE_LABELS: Record<AllowedRole, string> = {
  any: "Admin allowed",
  user: "Standard user only",
};

function BuiltInGroup({ label, names }: { label: string; names: readonly string[] }) {
  return (
    <div className="flex flex-col sm:flex-row sm:items-start gap-1.5 sm:gap-3">
      <p className="text-xs text-gray-500 sm:w-36 sm:shrink-0 sm:pt-1">{label}</p>
      <div className="flex flex-wrap gap-1.5">
        {names.map((name) => (
          <span key={name} className="inline-flex items-center px-2.5 py-1 rounded-full text-xs font-medium bg-gray-100 text-gray-600 border border-gray-200">
            {name}
          </span>
        ))}
      </div>
    </div>
  );
}

const namesOf = (entries: readonly { name: string }[]) => entries.map((e) => e.name);

export default function AgentAnalyzersSection({
  enableLocalAdminAnalyzer,
  setEnableLocalAdminAnalyzer,
  localAdminAllowedAccounts,
  setLocalAdminAllowedAccounts,
  newAllowedAccount,
  setNewAllowedAccount,
  enableSoftwareInventoryAnalyzer,
  setEnableSoftwareInventoryAnalyzer,
  enableIntegrityBypassAnalyzer,
  setEnableIntegrityBypassAnalyzer,
  enableConsoleBypassDetection,
  setEnableConsoleBypassDetection,
  onSave,
  saving,
  unsaved,
  readOnly = false,
}: AgentAnalyzersSectionProps) {
  const [newRole, setNewRole] = useState<AllowedRole>("any");
  const trimmed = newAllowedAccount.trim();
  // An entry typed in full ("kiosk|user") keeps its own role; otherwise the role picker decides.
  const typed = parseAllowedEntry(trimmed);
  const newEntry = typed?.role === "user" ? trimmed : formatAllowedEntry(trimmed, newRole);
  const newPattern = typed?.pattern ?? trimmed;
  const isDuplicate =
    trimmed !== "" &&
    (isBuiltInName(newPattern) ||
      localAdminAllowedAccounts.some((a) => (parseAllowedEntry(a)?.pattern ?? a).toLowerCase() === newPattern.toLowerCase()));
  // Same rules the backend applies to a new entry (a bare "*" would disable the analyzer).
  const entryError = trimmed === "" || isDuplicate ? null : validateNewEntry(newEntry);

  const addAccount = () => {
    if (!trimmed || isDuplicate || entryError) return;
    setLocalAdminAllowedAccounts([...localAdminAllowedAccounts, newEntry]);
    setNewAllowedAccount("");
  };

  const setEntryRole = (index: number, role: AllowedRole) => {
    const parsed = parseAllowedEntry(localAdminAllowedAccounts[index]);
    if (!parsed) return;
    setLocalAdminAllowedAccounts(
      localAdminAllowedAccounts.map((entry, i) => (i === index ? formatAllowedEntry(parsed.pattern, role) : entry)),
    );
  };

  return (
    <div id="agent-analyzers" className="bg-white rounded-lg shadow">
      <SectionCardHeader
        tone="rose"
        iconPath="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z"
        title="Agent Analyzers"
        subtitle="Security analyzers that run on enrolled devices to detect configuration anomalies."
        docsPath={DOCS_PATHS.agentAnalyzers}
      />
      <div className="p-6 space-y-6">
        <ReadOnlyFieldset readOnly={readOnly}>
        <div className="space-y-6">

        {/* ── Local Admin Analyzer ─────────────────────────── */}
        <div>
          <h3 className="text-sm font-semibold text-gray-700 uppercase tracking-wider mb-3">Local Admin Analyzer</h3>
          <p className="text-sm text-gray-500 mb-4">
            Detects pre-enrollment local admin account creation, a known Autopilot bypass technique.
            Checks for unexpected local user accounts and profile directories at enrollment start and completion.
          </p>

          {/* Enable toggle */}
          <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-rose-200 transition-colors">
            <div>
              <p className="font-medium text-gray-900">Enable Local Admin Analyzer</p>
              <p className="text-sm text-gray-500">Run at enrollment start and completion to detect unauthorized accounts</p>
            </div>
            <button
              onClick={() => setEnableLocalAdminAnalyzer(!enableLocalAdminAnalyzer)}
              className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${enableLocalAdminAnalyzer ? 'bg-rose-500' : 'bg-gray-300'}`}
            >
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enableLocalAdminAnalyzer ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </div>
        </div>

        {/* Allowed Accounts */}
        <div className={`p-4 rounded-lg border border-gray-200 transition-opacity ${!enableLocalAdminAnalyzer ? 'opacity-50' : ''}`}>
          <p className="font-medium text-gray-900 mb-1">Allowed Local Accounts</p>
          <p className="text-sm text-gray-500 mb-3">
            Accounts listed here are considered expected on enrolled devices and will not trigger alerts. These are merged with the built-in entries on the agent.
            Wildcards are supported: <code className="px-1 bg-gray-100 rounded">*</code> matches any sequence of characters,{" "}
            <code className="px-1 bg-gray-100 rounded">?</code> exactly one — e.g.{" "}
            <code className="px-1 bg-gray-100 rounded">adm-*</code> allows generated accounts like <code className="px-1 bg-gray-100 rounded">adm-83921</code>.
            Matching is case-insensitive and applies to both account names and profile folders.
            Choose <span className="font-medium text-gray-700">Standard user only</span> for an account that must never be a local administrator:
            it is still reported as soon as it is a member of the Administrators group.
          </p>

          {/* Built-in entries (read-only) */}
          <div className="mb-3 space-y-2">
            <p className="text-xs font-medium text-gray-400 uppercase tracking-wide">Built-in (always applied)</p>
            <BuiltInGroup label={ROLE_LABELS.any} names={namesOf(BUILT_IN_GROUPS.adminAllowed)} />
            <BuiltInGroup label={ROLE_LABELS.user} names={namesOf(BUILT_IN_GROUPS.standardUser)} />
            <BuiltInGroup label="Profile folders only" names={namesOf(BUILT_IN_GROUPS.folders)} />
            <p className="text-xs text-gray-500">
              Built-in accounts are recognised in every language. Other accounts that only carry the name{" "}
              {namesOf(BUILT_IN_GROUPS.sharedNames).join(", ")} are allowed as standard users.
            </p>
          </div>

          {/* Custom allowed accounts */}
          {localAdminAllowedAccounts.length > 0 && (
            <div className="mb-3">
              <p className="text-xs font-medium text-gray-400 uppercase tracking-wide mb-2">Your allowed accounts</p>
              <div className="space-y-1.5">
                {localAdminAllowedAccounts.map((account, idx) => {
                  const parsed = parseAllowedEntry(account);
                  return (
                    <div key={idx} className="flex flex-wrap items-center gap-x-3 gap-y-1 bg-rose-50 border border-rose-200 rounded-lg px-3 py-2">
                      <p className="flex-1 basis-40 min-w-0 truncate text-sm text-rose-900">{parsed?.pattern ?? account}</p>
                      {!parsed ? (
                        <span className="text-xs text-red-600">Not valid — the agent ignores it</span>
                      ) : isCoveredByBuiltIn(account) ? (
                        <span className="text-xs text-rose-600">Built in — not needed</span>
                      ) : null}
                      <div className="flex items-center gap-2">
                        <select
                          value={parsed?.role ?? "any"}
                          disabled={!parsed}
                          onChange={(e) => setEntryRole(idx, e.target.value as AllowedRole)}
                          aria-label={`Role for ${parsed?.pattern ?? account}`}
                          className="px-2 py-1 border border-rose-200 rounded-lg text-xs text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-rose-500 focus:border-rose-500 disabled:opacity-50"
                        >
                          <option value="any">{ROLE_LABELS.any}</option>
                          <option value="user">{ROLE_LABELS.user}</option>
                        </select>
                        <button
                          onClick={() => setLocalAdminAllowedAccounts(localAdminAllowedAccounts.filter((_, i) => i !== idx))}
                          className="flex-shrink-0 text-rose-400 hover:text-red-600 transition-colors"
                          title="Remove"
                        >
                          <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                          </svg>
                        </button>
                      </div>
                    </div>
                  );
                })}
              </div>
            </div>
          )}

          {/* Add new account */}
          <div className="flex flex-wrap gap-2 mt-2">
            <input
              type="text"
              placeholder="Account name or pattern (e.g. SupportAdmin, adm-*)"
              value={newAllowedAccount}
              onChange={(e) => setNewAllowedAccount(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); addAccount(); } }}
              className="flex-1 basis-56 min-w-0 px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-rose-500 focus:border-rose-500"
            />
            <select
              value={newRole}
              onChange={(e) => setNewRole(e.target.value as AllowedRole)}
              aria-label="Role for the new account"
              className="px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-rose-500 focus:border-rose-500"
            >
              <option value="any">{ROLE_LABELS.any}</option>
              <option value="user">{ROLE_LABELS.user}</option>
            </select>
            <button
              onClick={addAccount}
              disabled={!trimmed || isDuplicate || entryError !== null}
              className="px-4 py-1.5 bg-rose-600 text-white rounded-lg text-sm font-medium hover:bg-rose-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors whitespace-nowrap"
            >
              Add
            </button>
          </div>
          {isDuplicate && (
            <p className="text-xs text-red-500 mt-1">This account is already in the list or built in.</p>
          )}
          {entryError && (
            <p className="text-xs text-red-500 mt-1">{entryError}</p>
          )}
        </div>

        {/* ── Divider ─────────────────────────────────────── */}
        <div className="border-t border-gray-200" />

        {/* ── Software Inventory & Vulnerability Analyzer ── */}
        <div data-tour="software-inventory">
          <h3 className="text-sm font-semibold text-gray-700 uppercase tracking-wider mb-3">Software Inventory & Vulnerability Analyzer</h3>
          <p className="text-sm text-gray-500 mb-4">
            Collects installed software from the Windows registry at enrollment start and completion.
            Produces a normalized inventory snapshot, detects software installed during enrollment (delta detection),
            and correlates findings against known vulnerabilities (NVD CVEs, CISA KEV, MSRC).
          </p>

          {/* Enable toggle */}
          <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-rose-200 transition-colors">
            <div>
              <p className="font-medium text-gray-900">Enable Software Inventory & Vulnerability Analyzer</p>
              <p className="text-sm text-gray-500">Collect installed software inventory and correlate against known vulnerabilities</p>
            </div>
            <button
              onClick={() => setEnableSoftwareInventoryAnalyzer(!enableSoftwareInventoryAnalyzer)}
              className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${enableSoftwareInventoryAnalyzer ? 'bg-rose-500' : 'bg-gray-300'}`}
            >
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enableSoftwareInventoryAnalyzer ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </div>
        </div>

        {/* ── Divider ─────────────────────────────────────── */}
        <div className="border-t border-gray-200" />

        {/* ── Integrity Bypass Analyzer ───────────────────── */}
        <div>
          <h3 className="text-sm font-semibold text-gray-700 uppercase tracking-wider mb-3">Integrity Bypass Analyzer</h3>
          <p className="text-sm text-gray-500 mb-4">
            Detects Windows 11 &quot;unsupported hardware&quot; installations where PC Health Check / Setup-time gates were bypassed
            (LabConfig TPM / SecureBoot / CPU / RAM / Disk bypass keys, MoSetup upgrade flag, PCHC UpgradeEligibility).
            Also flags suspicious post-OOBE <code className="px-1 bg-gray-100 rounded">SetupComplete.cmd</code> / <code className="px-1 bg-gray-100 rounded">ErrorHandler.cmd</code> scripts.
            Results are correlated against current TPM and SecureBoot state.
          </p>

          {/* Enable toggle */}
          <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-rose-200 transition-colors">
            <div>
              <p className="font-medium text-gray-900">Enable Integrity Bypass Analyzer</p>
              <p className="text-sm text-gray-500">Run at enrollment start to surface devices with bypassed Win11 hardware gates</p>
            </div>
            <button
              onClick={() => setEnableIntegrityBypassAnalyzer(!enableIntegrityBypassAnalyzer)}
              className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${enableIntegrityBypassAnalyzer ? 'bg-rose-500' : 'bg-gray-300'}`}
            >
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enableIntegrityBypassAnalyzer ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </div>
        </div>

        {/* ── Divider ─────────────────────────────────────── */}
        <div className="border-t border-gray-200" />

        {/* ── OOBE Console / Shift+F10 Detection ───────────── */}
        <div>
          <h3 className="text-sm font-semibold text-gray-700 uppercase tracking-wider mb-3">Detect OOBE Console Access (Shift+F10)</h3>
          <p className="text-sm text-gray-500 mb-4">
            Flags an interactive command prompt opened during enrollment — the classic Shift+F10 OOBE
            bypass. Detects an interactive-session cmd.exe with a bare (non-scripted) command line and
            emits a Warning event with the process signature; it stops once the real-user desktop arrives
            (Shift+F10 no longer possible). A startup scan of the cmd.exe prefetch artifact additionally
            covers a console opened before the agent installed.
          </p>
          <p className="text-sm text-amber-600 mb-4">
            Best-effort, not gapless: a Shift+F10 pressed very early in OOBE may only be detected
            coarsely (via the prefetch artifact), and detection cannot block the console — it only reports it.
          </p>

          {/* Enable toggle */}
          <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-rose-200 transition-colors">
            <div>
              <p className="font-medium text-gray-900">Enable OOBE Console Detection</p>
              <p className="text-sm text-gray-500">On by default. Disable only if your support staff knowingly use Shift+F10 during enrollment</p>
            </div>
            <button
              onClick={() => setEnableConsoleBypassDetection(!enableConsoleBypassDetection)}
              className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${enableConsoleBypassDetection ? 'bg-rose-500' : 'bg-gray-300'}`}
            >
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enableConsoleBypassDetection ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </div>
        </div>

        </div>
        </ReadOnlyFieldset>

        {/* Save */}
        {!readOnly && <SaveBar onSave={onSave} saving={saving} unsaved={unsaved} />}
      </div>
    </div>
  );
}
