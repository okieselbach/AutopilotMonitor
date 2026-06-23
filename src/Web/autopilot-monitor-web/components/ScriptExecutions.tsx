"use client";

import { useEffect, useMemo, useState } from "react";
import { compareVersions } from "@/utils/bootstrapVersion";
import {
  buildScriptItemLabel,
  formatScriptDuration,
  getPhaseBadge,
  groupScriptItems,
  isDetectOnlyRow,
  isNonCompliantReport,
  mapRemediationStatus,
  reduceScriptEvents,
  scriptCardKey,
  scriptItemKey,
  STALE_RUNNING_THRESHOLD_SECONDS,
  type ScriptCard,
  type ScriptInputEvent,
  type ScriptItem,
} from "@/lib/scriptExecutions";
import { lookupScriptDisplayName, type DisplayNamesByRefKey } from "@/lib/scriptDisplayNames";

interface ScriptExecutionsProps {
  events: ScriptInputEvent[];
  showScriptOutput?: boolean;
  latestBootstrapVersion?: string | null;
  /**
   * Optional "{Kind}:{Id}" → Intune display name lookup. Populated by the parent via
   * `useScriptDisplayNames` once the backend resolver returns. When present, the row
   * label gains a subtle " · {displayName}" suffix so users see e.g.
   * "Platform Script · Bootstrap.ps1" without losing the headline category.
   * Tenants without the optional Graph permission see only the category — falling
   * back to the canonical UI is the natural degraded state.
   * Key shape preserves ScriptKind so the same id under Platform and Remediation
   * stay distinct (rare in practice but never silently collide).
   */
  displayNamesByRefKey?: DisplayNamesByRefKey;
}

export default function ScriptExecutions({ events, showScriptOutput, latestBootstrapVersion, displayNamesByRefKey }: ScriptExecutionsProps) {
  const cards = useMemo(() => groupScriptItems(reduceScriptEvents(events)), [events]);

  const [expanded, setExpanded] = useState(true);

  if (cards.length === 0) return null;

  // Header counters work on cards (one per policy), so a remediated cycle reads as
  // "1 succeeded" rather than "1 succeeded + 2 non-compliant" when its detection /
  // post-detection were non-compliant en route.
  const runningCount = cards.filter(c => c.headerState === "Running").length;
  const successCount = cards.filter(c => c.headerState === "Success").length;
  const nonCompliantCount = cards.filter(c => c.headerState === "NonCompliant").length;
  const failedCount = cards.filter(c => c.headerState === "Failed").length;
  const platformCount = cards.filter(c => c.scriptType === "platform").length;
  const remediationCount = cards.filter(c => c.scriptType === "remediation").length;

  return (
    <div className="bg-white shadow rounded-lg p-6 mb-6">
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex items-center justify-between w-full text-left"
      >
        <div className="flex items-center space-x-2">
          <svg className="w-5 h-5 text-violet-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 9l3 3-3 3m5 0h3M5 20h14a2 2 0 002-2V6a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z" />
          </svg>
          <h2 className="text-lg font-semibold text-gray-900">Script Executions</h2>
          <div className="flex items-center space-x-2 text-xs">
            {runningCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-gray-100 text-gray-700 font-medium">
                {runningCount} running
              </span>
            )}
            {successCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-green-100 text-green-700 font-medium">
                {successCount} succeeded
              </span>
            )}
            {nonCompliantCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-amber-100 text-amber-700 font-medium" title="Detection ran successfully but reported a non-compliant state">
                {nonCompliantCount} non-compliant
              </span>
            )}
            {failedCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-red-100 text-red-700 font-medium">
                {failedCount} failed
              </span>
            )}
            {platformCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-blue-50 text-blue-600 font-medium">
                {platformCount} platform
              </span>
            )}
            {remediationCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-amber-50 text-amber-600 font-medium">
                {remediationCount} remediation
              </span>
            )}
          </div>
        </div>
        <svg className={`w-5 h-5 text-gray-400 transition-transform duration-200 ${expanded ? 'rotate-90' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
        </svg>
      </button>

      {expanded && (
        <div className="space-y-3 mt-4">
          {cards.map((card) => (
            <ScriptCardView
              key={scriptCardKey(card)}
              card={card}
              showScriptOutput={showScriptOutput}
              latestBootstrapVersion={latestBootstrapVersion}
              displayNamesByRefKey={displayNamesByRefKey}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function getIntuneScriptUrl(policyId: string, scriptType: string): string | null {
  if (scriptType === "remediation") {
    return null; // Remediation URL requires scriptName which we don't have
  }
  return `https://intune.microsoft.com/#view/Microsoft_Intune_DeviceSettings/ConfigureWMPolicyMenuBlade/~/overview/policyId/${policyId}/policyType/0`;
}

function cardContainerClass(state: ScriptCard["headerState"]): string {
  switch (state) {
    case "Failed": return "bg-red-50 border border-red-200";
    case "Running": return "bg-gray-50 border border-gray-200";
    case "NonCompliant": return "bg-amber-50 border border-amber-200";
    default: return "bg-green-50 border border-green-200";
  }
}

function cardIconColor(state: ScriptCard["headerState"]): string {
  switch (state) {
    case "Failed": return "text-red-500";
    case "Running": return "text-gray-500";
    case "NonCompliant": return "text-amber-500";
    default: return "text-green-500";
  }
}

function cardStatusTextColor(state: ScriptCard["headerState"]): string {
  switch (state) {
    case "Failed": return "text-red-600";
    case "Running": return "text-gray-600";
    case "NonCompliant": return "text-amber-700";
    default: return "text-green-600";
  }
}

/**
 * Top-level renderer for one logical script card. Multi-phase remediation cycles render
 * the parent header + nested phase rows under it; platform / single-phase remediation
 * cards collapse the parent header into the single phase row (no nesting).
 */
function ScriptCardView({ card, showScriptOutput, latestBootstrapVersion, displayNamesByRefKey }: { card: ScriptCard; showScriptOutput?: boolean; latestBootstrapVersion?: string | null; displayNamesByRefKey?: DisplayNamesByRefKey }) {
  // Cycles default to COLLAPSED so a session with many remediation policies stays
  // scannable — the header carries the cycle-level outcome which is the at-a-glance
  // signal the user wants first. Click the chevron to drill into per-phase details.
  const [phasesOpen, setPhasesOpen] = useState(false);

  if (!card.isCycle) {
    // Single-phase / platform: keep the existing row layout, no parent wrapper.
    return (
      <ScriptItemRow
        item={card.phases[0]}
        showScriptOutput={showScriptOutput}
        latestBootstrapVersion={latestBootstrapVersion}
        displayName={lookupScriptDisplayName(displayNamesByRefKey, card.phases[0].scriptType, card.phases[0].policyId)}
      />
    );
  }

  // Multi-phase remediation cycle — render parent header + nested phase rows.
  const containerClass = cardContainerClass(card.headerState);
  const iconColor = cardIconColor(card.headerState);
  const statusColor = cardStatusTextColor(card.headerState);
  const shortId = card.policyId
    ? (card.policyId.length >= 8 ? card.policyId.substring(0, 8) : card.policyId)
    : "unknown";

  // Pull the most-relevant cycle metadata once for the header detail strip.
  const meta = card.phases.find(p => p.runContext) ?? card.phases[0];
  const remediationStatus = card.phases.map(p => p.remediationStatus).find(s => s != null);
  const remediationStatusLabel = mapRemediationStatus(remediationStatus);
  const cardDisplayName = lookupScriptDisplayName(displayNamesByRefKey, card.scriptType, card.policyId);

  return (
    <div className={`rounded-lg ${containerClass}`}>
      <button
        onClick={() => setPhasesOpen(!phasesOpen)}
        className="flex items-center justify-between w-full text-left p-3 hover:bg-black/[.02]"
        aria-expanded={phasesOpen}
      >
        <div className="flex items-center space-x-2 min-w-0">
          <CardIcon state={card.headerState} className={iconColor} />
          <span className="text-sm font-medium text-gray-900 truncate">Remediation</span>
          {cardDisplayName && (
            <span className="text-sm text-gray-600 truncate" title={cardDisplayName}>· {cardDisplayName}</span>
          )}
          <span className="text-xs font-mono text-gray-500">{shortId}…</span>
          <span className="text-xs px-2 py-0.5 rounded-full font-medium bg-amber-100 text-amber-700">
            cycle · {card.phases.length} phases
          </span>
          {meta.runContext && (
            <span className="text-xs text-gray-500">{meta.runContext}</span>
          )}
          {meta.targetType != null && (
            <span className="text-xs text-gray-500">Target: {meta.targetType === 2 ? "Device" : "User"}</span>
          )}
          {remediationStatusLabel && (
            <span className="text-xs text-gray-500">Status: {remediationStatusLabel}</span>
          )}
        </div>
        <div className="flex items-center space-x-3 text-xs flex-shrink-0 ml-2">
          <span className={`font-medium ${statusColor}`}>{card.headerLabel}</span>
          <svg className={`w-4 h-4 text-gray-400 transition-transform duration-200 ${phasesOpen ? 'rotate-90' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
          </svg>
        </div>
      </button>

      {phasesOpen && (
        // Nested phase rows. Slightly inset so the parent → child relationship is visual.
        <div className="border-t border-black/5 px-3 py-2 space-y-2">
          {card.phases.map((phase) => (
            <ScriptItemRow
              key={scriptItemKey(phase)}
              item={phase}
              showScriptOutput={showScriptOutput}
              latestBootstrapVersion={latestBootstrapVersion}
              displayName={lookupScriptDisplayName(displayNamesByRefKey, phase.scriptType, phase.policyId)}
              nested
            />
          ))}
        </div>
      )}
    </div>
  );
}

function CardIcon({ state, className }: { state: ScriptCard["headerState"]; className: string }) {
  if (state === "Running") {
    return (
      <svg className={`w-4 h-4 flex-shrink-0 animate-spin ${className}`} fill="none" viewBox="0 0 24 24">
        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
      </svg>
    );
  }
  if (state === "Failed") {
    return (
      <svg className={`w-4 h-4 flex-shrink-0 ${className}`} fill="none" viewBox="0 0 24 24" stroke="currentColor">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
      </svg>
    );
  }
  if (state === "NonCompliant") {
    return (
      <svg className={`w-4 h-4 flex-shrink-0 ${className}`} fill="none" viewBox="0 0 24 24" stroke="currentColor">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
      </svg>
    );
  }
  return (
    <svg className={`w-4 h-4 flex-shrink-0 ${className}`} fill="none" viewBox="0 0 24 24" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
    </svg>
  );
}

function ScriptItemRow({ item, showScriptOutput, latestBootstrapVersion, nested, displayName }: { item: ScriptItem; showScriptOutput?: boolean; latestBootstrapVersion?: string | null; nested?: boolean; displayName?: string | null }) {
  const [showDetails, setShowDetails] = useState(false);
  // Re-render every 5s while in Running state so elapsed-time updates live.
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (item.state !== "Running") return;
    const id = setInterval(() => setNow(Date.now()), 5000);
    return () => clearInterval(id);
  }, [item.state]);

  const elapsedSeconds = item.state === "Running"
    ? Math.max(0, Math.floor((now - new Date(item.timestamp).getTime()) / 1000))
    : null;
  const isStale = elapsedSeconds !== null && elapsedSeconds > STALE_RUNNING_THRESHOLD_SECONDS;

  const label = buildScriptItemLabel(item);
  const isDetectOnly = isDetectOnlyRow(item);
  const isNonCompliant = isNonCompliantReport(item);

  // Standalone rows get their own coloured container; nested phase rows live inside an
  // already-coloured parent card so they use a lighter, neutral inner background to
  // avoid the noisy double-tinting effect.
  const containerClass = nested
    ? "bg-white/60 border border-black/5"
    : item.state === "Failed"
      ? "bg-red-50 border border-red-200"
      : item.state === "Running"
        ? "bg-gray-50 border border-gray-200"
        : isNonCompliant
          ? "bg-amber-50 border border-amber-200"
          : "bg-green-50 border border-green-200";

  const shortId = item.policyId
    ? (item.policyId.length >= 8 ? item.policyId.substring(0, 8) : item.policyId)
    : "unknown";
  const intuneUrl = getIntuneScriptUrl(item.policyId, item.scriptType);

  // Status text for the right-hand summary cell
  let statusText: string;
  if (item.state === "Running") {
    statusText = isStale ? `Running (${elapsedSeconds}s — stuck?)` : `Running (${elapsedSeconds}s)`;
  } else if (item.scriptType === "remediation" && item.complianceResult) {
    statusText = item.complianceResult === "True" ? "Compliant" : "Non-compliant";
  } else {
    statusText = item.result ?? (item.state === "Success" ? "Success" : "Failed");
  }

  // Run duration (from the agent's start→completion timing). Surfaced for every completed
  // script so slow / inefficient scripts stand out; styled amber past the stale threshold
  // (10 min) since at that range a script is a plausible enrollment-pipeline blocker.
  const durationLabel = item.state !== "Running" ? formatScriptDuration(item.durationSeconds) : null;
  const isSlowDuration = item.durationSeconds != null && item.durationSeconds >= STALE_RUNNING_THRESHOLD_SECONDS;

  const hasStdout = item.state !== "Running" && showScriptOutput !== false && item.stdout && item.stdout.trim().length > 0;
  const hasStderr = item.state !== "Running" && item.stderr && item.stderr.trim().length > 0;
  const hasOutput = hasStdout || hasStderr;
  const remediationStatusLabel = mapRemediationStatus(item.remediationStatus);

  return (
    <div className={`rounded-lg p-3 ${containerClass}`}>
      <div className="flex items-center justify-between">
        <div className="flex items-center space-x-2 min-w-0">
          {item.state === "Running" ? (
            <svg className={`w-4 h-4 text-gray-500 flex-shrink-0 ${isStale ? "" : "animate-spin"}`} fill="none" viewBox="0 0 24 24">
              <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
              <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
            </svg>
          ) : item.state === "Failed" ? (
            <svg className="w-4 h-4 text-red-500 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          ) : isNonCompliant ? (
            <svg className="w-4 h-4 text-amber-500 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
            </svg>
          ) : (
            <svg className="w-4 h-4 text-green-500 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
            </svg>
          )}
          <span className="text-sm font-medium text-gray-900 truncate">{label}</span>
          {/* Intune display name from the optional Graph add-on permission (if granted). */}
          {displayName && (
            <span className="text-sm text-gray-600 truncate" title={displayName}>· {displayName}</span>
          )}
          {/* shortId only on standalone rows — inside a cycle the parent header already shows it. */}
          {!nested && (
            intuneUrl ? (
              <a href={intuneUrl} target="_blank" rel="noopener noreferrer" className="text-xs font-mono text-blue-600 hover:text-blue-800 hover:underline" title="Open in Intune portal">{shortId}…</a>
            ) : (
              <span className="text-xs font-mono text-gray-500">{shortId}…</span>
            )
          )}
          {item.scriptType === "remediation" && getPhaseBadge(item) && (
            <span className="text-xs px-2 py-0.5 rounded-full font-medium bg-amber-100 text-amber-700">
              {getPhaseBadge(item)}
            </span>
          )}
          {item.scriptType !== "remediation" && (
            <span className="text-xs px-2 py-0.5 rounded-full font-medium bg-blue-100 text-blue-700">
              {item.scriptType}
            </span>
          )}
          {isDetectOnly && (
            <span
              className="text-xs px-2 py-0.5 rounded-full font-medium bg-gray-100 text-gray-600"
              title="Detection-only policy — no remediation script attached"
            >
              detect-only
            </span>
          )}
          {item.bootstrapVersion && (
            <span
              className="text-xs px-2 py-0.5 rounded-full font-medium bg-indigo-100 text-indigo-700"
              title="Detected Autopilot-Monitor bootstrap script"
            >
              bootstrap v{item.bootstrapVersion}
            </span>
          )}
          {item.bootstrapVersion && latestBootstrapVersion && compareVersions(item.bootstrapVersion, latestBootstrapVersion) < 0 && (
            <span
              className="text-xs px-2 py-0.5 rounded-full font-medium bg-amber-100 text-amber-800 border border-amber-200"
              title={`latest: v${latestBootstrapVersion}`}
            >
              outdated
            </span>
          )}
          {/* runContext/Target only on standalone rows — inside a cycle the parent header carries them. */}
          {!nested && item.runContext && (
            <span className="text-xs text-gray-500">{item.runContext}</span>
          )}
        </div>
        <div className="flex items-center space-x-3 text-xs text-gray-500 flex-shrink-0 ml-2">
          <span className={`font-medium ${
            item.state === "Failed" ? "text-red-600"
            : item.state === "Running" ? (isStale ? "text-amber-600" : "text-gray-600")
            : isNonCompliant ? "text-amber-700"
            : "text-green-600"
          }`}>
            {statusText}
          </span>
          {item.exitCode != null && (
            <span className={`font-mono ${
              item.state === "Failed" && item.exitCode !== 0 ? "text-red-600"
              : isNonCompliant ? "text-amber-700"
              : "text-gray-500"
            }`}>
              exit {item.exitCode}
            </span>
          )}
          {durationLabel && (
            <span
              className={`font-mono ${isSlowDuration ? "text-amber-600 font-medium" : "text-gray-500"}`}
              title={isSlowDuration ? "Long run — a script near the IME timeout can block the enrollment pipeline" : "Script run duration"}
            >
              {durationLabel}
            </span>
          )}
          {item.state !== "Running" && (
            <button
              onClick={() => setShowDetails(!showDetails)}
              className="text-xs text-blue-600 hover:text-blue-800"
            >
              {showDetails ? 'Hide' : 'Details'}
            </button>
          )}
        </div>
      </div>

      {showDetails && item.state !== "Running" && (
        <div className="mt-3 space-y-2">
          {/* Metadata — slimmer when nested (parent header already shows policy id / context / target / status). */}
          <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-600">
            {!nested && (
              <span><span className="font-medium text-gray-700">Policy ID:</span> {intuneUrl ? (
                <a href={intuneUrl} target="_blank" rel="noopener noreferrer" className="font-mono text-blue-600 hover:text-blue-800 hover:underline">{item.policyId}</a>
              ) : (
                <span className="font-mono">{item.policyId}</span>
              )}</span>
            )}
            {!nested && item.runContext && <span><span className="font-medium text-gray-700">Context:</span> {item.runContext}</span>}
            {!nested && item.targetType != null && <span><span className="font-medium text-gray-700">Target:</span> {item.targetType === 2 ? "Device" : "User"}</span>}
            {item.exitCode != null && <span><span className="font-medium text-gray-700">Exit Code:</span> <span className="font-mono">{item.exitCode}</span></span>}
            {durationLabel && <span><span className="font-medium text-gray-700">Duration:</span> <span className={`font-mono ${isSlowDuration ? "text-amber-600" : ""}`}>{durationLabel}</span></span>}
            {item.result && <span><span className="font-medium text-gray-700">Result:</span> {item.result}</span>}
            {item.complianceResult && <span><span className="font-medium text-gray-700">Compliance:</span> {item.complianceResult === "True" ? "Compliant" : "Non-compliant"}</span>}
            {!nested && remediationStatusLabel && <span><span className="font-medium text-gray-700">Status:</span> {remediationStatusLabel}</span>}
            {item.errorCode != null && item.errorCode !== 0 && (
              <span><span className="font-medium text-gray-700">Error Code:</span> <span className="font-mono">{item.errorCode}</span></span>
            )}
            <span><span className="font-medium text-gray-700">Time:</span> {new Date(item.timestamp).toLocaleTimeString()}</span>
          </div>

          {item.errorDetails && (
            <div className="text-xs text-red-700 bg-red-50 border border-red-200 rounded p-2">
              <span className="font-medium">Error details:</span> {item.errorDetails}
            </div>
          )}

          {hasStdout && (
            <div>
              <div className="text-xs font-medium text-gray-500 mb-1">stdout</div>
              <div className="p-2 bg-gray-900 rounded text-xs text-gray-100 font-mono overflow-x-auto max-h-48 overflow-y-auto">
                <pre className="whitespace-pre-wrap break-words">{item.stdout}</pre>
              </div>
            </div>
          )}

          {showScriptOutput === false && item.stdout && item.stdout.trim().length > 0 && (
            <div className="text-xs text-gray-400 italic">stdout hidden by admin setting</div>
          )}

          {hasStderr && (
            <div>
              <div className="text-xs font-medium text-red-500 mb-1">stderr</div>
              <div className="p-2 bg-gray-900 rounded text-xs text-red-300 font-mono overflow-x-auto max-h-48 overflow-y-auto">
                <pre className="whitespace-pre-wrap break-words">{item.stderr}</pre>
              </div>
            </div>
          )}

          {!hasOutput && !(showScriptOutput === false && item.stdout && item.stdout.trim().length > 0) && (
            <div className="text-xs text-gray-400 italic">No script output captured</div>
          )}
        </div>
      )}
    </div>
  );
}
