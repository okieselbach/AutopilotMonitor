"use client";

import { useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { useCanMutatePlatform } from "@/hooks/useCanMutatePlatform";
import { SectionCardHeader } from "@/components/SectionCardHeader";

interface BlockedVersion {
  versionPattern: string;
  action: string;
  createdByEmail: string;
  createdAt: string;
  reason?: string;
}

interface VersionBlockSectionProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}

export function VersionBlockSection({
  getAccessToken,
  setError,
  setSuccessMessage,
}: VersionBlockSectionProps) {
  // Read-only Global Readers may view block rules but not add/remove them.
  const canMutate = useCanMutatePlatform();
  const [versionPattern, setVersionPattern] = useState("");
  const [versionAction, setVersionAction] = useState<"Block" | "Kill">("Block");
  const [versionReason, setVersionReason] = useState("");
  const [addingRule, setAddingRule] = useState(false);
  const [blockedVersions, setBlockedVersions] = useState<BlockedVersion[]>([]);
  const [loadingVersions, setLoadingVersions] = useState(false);
  const [removingPattern, setRemovingPattern] = useState<string | null>(null);

  const fetchBlockedVersions = async () => {
    try {
      setLoadingVersions(true);
      const response = await authenticatedFetch(api.versions.blocked(), getAccessToken);
      if (!response.ok) throw new Error(`Failed to load version blocks: ${response.statusText}`);
      const data = await response.json();
      setBlockedVersions(data.rules ?? []);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while loading version blocks");
      }
      setError(err instanceof Error ? err.message : "Failed to load version blocks");
    } finally {
      setLoadingVersions(false);
    }
  };

  const handleAddRule = async () => {
    if (!canMutate) return; // read-only Global Reader
    if (!versionPattern.trim()) return;

    if (versionAction === "Kill" && !confirm(
      `VERSION KILL: This will send a remote kill signal to ALL agents matching pattern "${versionPattern.trim()}". This cannot be undone. Continue?`
    )) return;

    try {
      setAddingRule(true);
      setError(null);
      const response = await authenticatedFetch(api.versions.block(), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          versionPattern: versionPattern.trim(),
          action: versionAction,
          reason: versionReason.trim() || undefined,
        }),
      });
      const result = await response.json();
      if (!response.ok) throw new Error(result.message || response.statusText);
      setSuccessMessage(
        versionAction === "Kill"
          ? `Version pattern "${versionPattern.trim()}" set to kill.`
          : `Version pattern "${versionPattern.trim()}" blocked.`
      );
      setTimeout(() => setSuccessMessage(null), 4000);
      setVersionPattern("");
      setVersionReason("");
      setVersionAction("Block");
      if (blockedVersions.length > 0) await fetchBlockedVersions();
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while adding version block");
      }
      setError(err instanceof Error ? err.message : "Failed to add version block rule");
    } finally {
      setAddingRule(false);
    }
  };

  const handleRemoveRule = async (pattern: string) => {
    if (!canMutate) return; // read-only Global Reader
    try {
      setRemovingPattern(pattern);
      setError(null);
      const response = await authenticatedFetch(
        api.versions.unblock(pattern),
        getAccessToken,
        { method: "DELETE" }
      );
      const result = await response.json();
      if (!response.ok) throw new Error(result.message || response.statusText);
      setSuccessMessage(`Version pattern "${pattern}" removed.`);
      setTimeout(() => setSuccessMessage(null), 3000);
      setBlockedVersions((prev) => prev.filter((v) => v.versionPattern !== pattern));
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while removing version block");
      }
      setError(err instanceof Error ? err.message : "Failed to remove version block rule");
    } finally {
      setRemovingPattern(null);
    }
  };

  const getPatternDescription = (pattern: string) => {
    if (pattern.startsWith("=")) return `exact version ${pattern.substring(1)}`;
    if (pattern.endsWith(".*")) return `all ${pattern.replace(".*", ".x")} versions`;
    return `all versions ≤ ${pattern}`;
  };

  return (
    <div className="bg-gradient-to-br from-amber-50 to-orange-50 dark:from-gray-800 dark:to-gray-800 border-2 border-amber-300 dark:border-amber-700 rounded-lg shadow-lg">
      <SectionCardHeader
        tone="adminAmber"
        iconPath="M7 7h.01M7 3h5c.512 0 1.024.195 1.414.586l7 7a2 2 0 010 2.828l-7 7a2 2 0 01-2.828 0l-7-7A1.994 1.994 0 013 12V7a4 4 0 014-4z"
        title="Version Block Management"
        subtitle="Block or kill agents by version pattern. Useful for catching old agents that are still running."
      />
      <div className="p-6 space-y-6">
        {/* Pattern help */}
        <div className="border border-amber-200 dark:border-amber-700 rounded-lg p-4 bg-amber-50/50 dark:bg-amber-900/10">
          <h3 className="text-sm font-semibold text-amber-900 dark:text-amber-100 mb-2">Pattern Syntax</h3>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-2 text-xs text-amber-800 dark:text-amber-200">
            <div className="flex items-start gap-2">
              <code className="bg-amber-100 dark:bg-amber-900/40 px-1.5 py-0.5 rounded font-mono whitespace-nowrap">1.*</code>
              <span>All agents with major version 1</span>
            </div>
            <div className="flex items-start gap-2">
              <code className="bg-amber-100 dark:bg-amber-900/40 px-1.5 py-0.5 rounded font-mono whitespace-nowrap">1.0.*</code>
              <span>All agents with version 1.0.x</span>
            </div>
            <div className="flex items-start gap-2">
              <code className="bg-amber-100 dark:bg-amber-900/40 px-1.5 py-0.5 rounded font-mono whitespace-nowrap">1.0.30</code>
              <span>All agents with version ≤ 1.0.30</span>
            </div>
            <div className="flex items-start gap-2">
              <code className="bg-amber-100 dark:bg-amber-900/40 px-1.5 py-0.5 rounded font-mono whitespace-nowrap">=1.0.30</code>
              <span>Exactly version 1.0.30 only</span>
            </div>
          </div>
        </div>

        {/* Add rule form */}
        <div>
          <h3 className="text-sm font-semibold text-amber-900 dark:text-amber-100 mb-3">Add Version Block Rule</h3>
          <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Version Pattern</label>
              <input
                type="text"
                placeholder="e.g. 1.0.30 or 1.0.*"
                value={versionPattern}
                onChange={(e) => setVersionPattern(e.target.value)}
                className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-amber-500 focus:border-amber-500 font-mono"
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Action</label>
              <select
                value={versionAction}
                onChange={(e) => setVersionAction(e.target.value as "Block" | "Kill")}
                className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 focus:outline-none focus:ring-2 focus:ring-amber-500 focus:border-amber-500"
              >
                <option value="Block">Block (stop uploads)</option>
                <option value="Kill">Kill (remote shutdown)</option>
              </select>
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Reason (optional)</label>
              <input
                type="text"
                placeholder="e.g. Deprecated version"
                value={versionReason}
                onChange={(e) => setVersionReason(e.target.value)}
                className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-amber-500 focus:border-amber-500"
              />
            </div>
          </div>
          {versionAction === "Kill" && (
            <div className="mt-3 p-3 bg-red-100 dark:bg-red-900/40 border border-red-300 dark:border-red-700 rounded-lg">
              <p className="text-xs text-red-800 dark:text-red-200 font-medium">
                All agents matching this version pattern will execute self-destruct on their next upload attempt. This is irreversible.
              </p>
            </div>
          )}
          {versionPattern.trim() && (
            <p className="mt-2 text-xs text-amber-700 dark:text-amber-300">
              This will match: <strong>{getPatternDescription(versionPattern.trim())}</strong>
            </p>
          )}
          <button
            onClick={handleAddRule}
            disabled={!canMutate || addingRule || !versionPattern.trim()}
            className={`mt-4 px-4 py-2 text-white rounded-lg text-sm font-medium disabled:opacity-50 flex items-center space-x-2 ${
              versionAction === "Kill"
                ? "bg-red-800 hover:bg-red-900 dark:bg-red-700 dark:hover:bg-red-800"
                : "bg-amber-600 hover:bg-amber-700"
            }`}
          >
            {addingRule ? (
              <><div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div><span>{versionAction === "Kill" ? "Adding Kill Rule..." : "Adding Block Rule..."}</span></>
            ) : (
              <span>{versionAction === "Kill" ? "Add Kill Rule" : "Add Block Rule"}</span>
            )}
          </button>
        </div>

        {/* Active rules list */}
        <div className="border-t border-amber-200 dark:border-amber-700 pt-6">
          <div className="flex items-center justify-between mb-4">
            <h3 className="text-sm font-semibold text-amber-900 dark:text-amber-100">Active Version Block Rules</h3>
            <button
              onClick={fetchBlockedVersions}
              disabled={loadingVersions}
              className="px-4 py-2 bg-amber-100 dark:bg-amber-900/40 text-amber-800 dark:text-amber-200 border border-amber-300 dark:border-amber-600 rounded-lg text-sm font-medium hover:bg-amber-200 disabled:opacity-50"
            >
              {loadingVersions ? "Loading..." : "Load Rules"}
            </button>
          </div>
          {blockedVersions.length > 0 ? (
            <div className="overflow-x-auto">
              <table className="w-full text-sm text-left">
                <thead className="text-xs text-amber-800 dark:text-amber-200 uppercase bg-amber-100 dark:bg-amber-900/30">
                  <tr>
                    <th className="px-3 py-2">Pattern</th>
                    <th className="px-3 py-2">Matches</th>
                    <th className="px-3 py-2">Type</th>
                    <th className="px-3 py-2">Created</th>
                    <th className="px-3 py-2">Created By</th>
                    <th className="px-3 py-2">Reason</th>
                    <th className="px-3 py-2">Action</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-amber-100 dark:divide-amber-900/30">
                  {blockedVersions.map((v) => (
                    <tr key={v.versionPattern} className="bg-white dark:bg-gray-800">
                      <td className="px-3 py-2 font-mono font-medium text-gray-900 dark:text-gray-100">{v.versionPattern}</td>
                      <td className="px-3 py-2 text-xs text-gray-500 dark:text-gray-400">{getPatternDescription(v.versionPattern)}</td>
                      <td className="px-3 py-2">
                        {v.action === "Kill" ? (
                          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-semibold bg-red-700 text-white">Kill</span>
                        ) : (
                          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-semibold bg-orange-200 dark:bg-orange-800 text-orange-800 dark:text-orange-200">Block</span>
                        )}
                      </td>
                      <td className="px-3 py-2 text-gray-600 dark:text-gray-400">{new Date(v.createdAt).toLocaleString()}</td>
                      <td className="px-3 py-2 text-gray-600 dark:text-gray-400">{v.createdByEmail}</td>
                      <td className="px-3 py-2 text-gray-600 dark:text-gray-400">{v.reason || "\u2014"}</td>
                      <td className="px-3 py-2">
                        {canMutate ? (
                          <button
                            onClick={() => handleRemoveRule(v.versionPattern)}
                            disabled={removingPattern === v.versionPattern}
                            className="px-3 py-1 bg-green-600 text-white rounded text-xs font-medium hover:bg-green-700 disabled:opacity-50"
                          >
                            {removingPattern === v.versionPattern ? "Removing..." : "Remove"}
                          </button>
                        ) : (
                          <span className="text-xs text-gray-400">Read-only</span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : loadingVersions ? null : (
            <p className="text-sm text-gray-500 dark:text-gray-400 italic">Click &quot;Load Rules&quot; to see active version block rules.</p>
          )}
        </div>
      </div>
    </div>
  );
}
