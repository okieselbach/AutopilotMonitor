"use client";

import SaveResetBar from "./SaveResetBar";

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
  enableRealmJoinWatcher: boolean;
  setEnableRealmJoinWatcher: (value: boolean) => void;
  onSave: () => Promise<void> | void;
  onReset: () => void;
  saving: boolean;
}

const BUILTIN_ACCOUNTS = [
  "Administrator",
  "Guest",
  "DefaultAccount",
  "WDAGUtilityAccount",
  "defaultuser0",
  "defaultuser1",
  "defaultuser2",
  "Public",
  "Default",
  "Default User",
  "All Users",
];

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
  enableRealmJoinWatcher,
  setEnableRealmJoinWatcher,
  onSave,
  onReset,
  saving,
}: AgentAnalyzersSectionProps) {
  const trimmed = newAllowedAccount.trim();
  const isDuplicate =
    trimmed !== "" &&
    (localAdminAllowedAccounts.some((a) => a.toLowerCase() === trimmed.toLowerCase()) ||
      BUILTIN_ACCOUNTS.some((a) => a.toLowerCase() === trimmed.toLowerCase()));

  const addAccount = () => {
    if (!trimmed || isDuplicate) return;
    setLocalAdminAllowedAccounts([...localAdminAllowedAccounts, trimmed]);
    setNewAllowedAccount("");
  };

  return (
    <div id="agent-analyzers" className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-rose-50 to-red-50">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-rose-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-gray-900">Agent Analyzers</h2>
            <p className="text-sm text-gray-500 mt-1">Security analyzers that run on enrolled devices to detect configuration anomalies.</p>
          </div>
        </div>
      </div>
      <div className="p-6 space-y-6">

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
            Accounts listed here are considered expected on enrolled devices and will not trigger alerts. These are merged with the built-in defaults on the agent.
          </p>

          {/* Built-in accounts (read-only) */}
          <div className="mb-3">
            <p className="text-xs font-medium text-gray-400 uppercase tracking-wide mb-2">Built-in (always allowed)</p>
            <div className="flex flex-wrap gap-1.5">
              {BUILTIN_ACCOUNTS.map((account) => (
                <span key={account} className="inline-flex items-center px-2.5 py-1 rounded-full text-xs font-medium bg-gray-100 text-gray-600 border border-gray-200">
                  {account}
                </span>
              ))}
            </div>
          </div>

          {/* Custom allowed accounts */}
          {localAdminAllowedAccounts.length > 0 && (
            <div className="mb-3">
              <p className="text-xs font-medium text-gray-400 uppercase tracking-wide mb-2">Your allowed accounts</p>
              <div className="space-y-1.5">
                {localAdminAllowedAccounts.map((account, idx) => (
                  <div key={idx} className="flex items-center justify-between bg-rose-50 border border-rose-200 rounded-lg px-3 py-2">
                    <p className="text-sm text-rose-900">{account}</p>
                    <button
                      onClick={() => setLocalAdminAllowedAccounts(localAdminAllowedAccounts.filter((_, i) => i !== idx))}
                      className="ml-2 flex-shrink-0 text-rose-400 hover:text-red-600 transition-colors"
                      title="Remove"
                    >
                      <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                      </svg>
                    </button>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Add new account */}
          <div className="flex gap-2 mt-2">
            <input
              type="text"
              placeholder="Account name (e.g. SupportAdmin)"
              value={newAllowedAccount}
              onChange={(e) => setNewAllowedAccount(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); addAccount(); } }}
              className="flex-1 px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-rose-500 focus:border-rose-500"
            />
            <button
              onClick={addAccount}
              disabled={!trimmed || isDuplicate}
              className="px-4 py-1.5 bg-rose-600 text-white rounded-lg text-sm font-medium hover:bg-rose-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors whitespace-nowrap"
            >
              Add
            </button>
          </div>
          {isDuplicate && (
            <p className="text-xs text-red-500 mt-1">This account is already in the list.</p>
          )}
        </div>

        {/* ── Divider ─────────────────────────────────────── */}
        <div className="border-t border-gray-200" />

        {/* ── Software Inventory & Vulnerability Analyzer ── */}
        <div>
          <div className="flex items-center gap-2 mb-3">
            <h3 className="text-sm font-semibold text-gray-700 uppercase tracking-wider">Software Inventory & Vulnerability Analyzer</h3>
            <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-semibold bg-emerald-100 text-emerald-700 border border-emerald-200">
              Experimental
            </span>
            <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-semibold bg-amber-100 text-amber-700 border border-amber-200">
              Pre-Release
            </span>
          </div>
          <p className="text-sm text-gray-500 mb-4">
            Collects installed software from the Windows registry at enrollment start and completion.
            Produces a normalized inventory snapshot, detects software installed during enrollment (delta detection),
            and correlates findings against known vulnerabilities (NVD CVEs, CISA KEV, MSRC).
          </p>

          {/* Enable toggle */}
          <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-emerald-200 transition-colors">
            <div>
              <p className="font-medium text-gray-900">Enable Software Inventory & Vulnerability Analyzer</p>
              <p className="text-sm text-gray-500">Collect installed software inventory and correlate against known vulnerabilities</p>
            </div>
            <button
              onClick={() => setEnableSoftwareInventoryAnalyzer(!enableSoftwareInventoryAnalyzer)}
              className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${enableSoftwareInventoryAnalyzer ? 'bg-emerald-500' : 'bg-gray-300'}`}
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

        {/* ── RealmJoin Watcher ───────────────────────────── */}
        <div>
          <h3 className="text-sm font-semibold text-gray-700 uppercase tracking-wider mb-3">RealmJoin Watcher</h3>
          <p className="text-sm text-gray-500 mb-4">
            Tracks RealmJoin deployment state and enrollment packages during provisioning (deployment phase,
            per-package start/completion, and the RealmJoin completion gate). Leave this off unless this tenant
            deploys via RealmJoin — for all other tenants it produces no signal and is best left disabled.
          </p>

          {/* Enable toggle */}
          <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-rose-200 transition-colors">
            <div>
              <p className="font-medium text-gray-900">Enable RealmJoin Watcher</p>
              <p className="text-sm text-gray-500">Off by default. Enable for tenants that provision devices with RealmJoin</p>
            </div>
            <button
              onClick={() => setEnableRealmJoinWatcher(!enableRealmJoinWatcher)}
              className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${enableRealmJoinWatcher ? 'bg-rose-500' : 'bg-gray-300'}`}
            >
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enableRealmJoinWatcher ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </div>
        </div>

        {/* Save / Reset */}
        <SaveResetBar onSave={onSave} onReset={onReset} saving={saving} />
      </div>
    </div>
  );
}
