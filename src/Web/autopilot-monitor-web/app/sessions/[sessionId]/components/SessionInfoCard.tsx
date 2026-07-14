"use client";

import { useState } from "react";
import { Session } from "@/types";
import { SessionStatusBadge } from "@/components/SessionStatusBadge";
import FailureSnapshotBlock from "./FailureSnapshotBlock";

interface SessionInfoCardProps {
  session: Session;
  enrollmentDuration: string | null;
  displayStatus: string;
  isGatherRulesSession: boolean;
  ntpOffset?: { offsetSeconds: number; ntpServer?: string } | null;
  configMgrDetected?: { ccmVersion?: string; ccmServiceState?: string; siteCode?: string; confidenceScore?: number } | null;
}

export default function SessionInfoCard({ session, enrollmentDuration, displayStatus, isGatherRulesSession, ntpOffset, configMgrDetected }: SessionInfoCardProps) {
  const lastContactTooltip = session.lastEventAt
    ? `Last contact: ${new Date(session.lastEventAt).toLocaleString([], { dateStyle: "short", timeStyle: "medium" })}`
    : undefined;
  return (
    <div className="bg-white shadow rounded-lg p-6 mb-6">
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-xl font-semibold text-gray-900">Session Info</h2>
        {isGatherRulesSession && (
          <span className="inline-flex items-center gap-1.5 px-3 py-1 rounded-full text-xs font-semibold bg-violet-100 text-violet-800 border border-violet-200">
            <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2" /></svg>
            Gather Rules Collection
          </span>
        )}
      </div>
      {isGatherRulesSession && (
        <div className="mb-4 p-3 bg-violet-50 border border-violet-200 rounded-lg text-sm text-violet-800">
          This session was created by running <code className="font-mono text-xs bg-violet-100 px-1 py-0.5 rounded">--run-gather-rules</code>. It contains diagnostic data collected outside of a regular enrollment flow.
        </div>
      )}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
        <InfoItem
          label="Device"
          value={session.deviceName || session.serialNumber}
          copyText={session.deviceName || session.serialNumber}
        />
        <InfoItem
          label="Model"
          value={`${session.manufacturer} ${session.model}`}
          copyText={`${session.manufacturer} ${session.model}`}
        />
        <InfoItem label="Serial Number" value={session.serialNumber} copyText={session.serialNumber} />
        <InfoItem
          label="Session ID"
          value={
            <span title={session.sessionId} className="cursor-default">
              {session.sessionId.split("-").slice(0, 3).join("-")}…
            </span>
          }
          copyText={session.sessionId}
        />
        <InfoItem label="Started" value={new Date(session.startedAt).toLocaleString([], { dateStyle: "short", timeStyle: "short" })} tooltip={lastContactTooltip} />
        <InfoItem label="Duration" value={enrollmentDuration ?? `${Math.round(session.durationSeconds / 60)} min`} tooltip={lastContactTooltip} />
        <InfoItem label="Events" value={session.eventCount.toString()} tooltip={lastContactTooltip} />
        <InfoItem label="Reboots" value={(session.rebootCount ?? 0).toString()} tooltip="System reboots observed during enrollment (V2 only)" />
        <InfoItem label="Status" value={<StatusBadge status={displayStatus} failureReason={session.failureReason} failureSource={session.failureSource} adminMarkedAction={session.adminMarkedAction} reconcileReason={session.reconcileReason} />} />
        <InfoItem label="Enrollment Type" value={enrollmentTypeLabel(session, isGatherRulesSession)} />
        <InfoItem label="Join Type" value={joinTypeLabel(session)} />
      </div>
      {displayStatus === "Succeeded" && !session.adminMarkedAction && session.reconcileReason && (
        <div className="mt-4 flex items-start gap-2 px-3 py-2 rounded-lg bg-sky-50 border border-sky-200 text-sm text-sky-800 dark:bg-sky-900/30 dark:border-sky-800 dark:text-sky-200">
          <svg className="w-4 h-4 flex-shrink-0 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
          </svg>
          <div>
            <span>
              <strong>Backend reconciled:</strong> {session.reconcileReason} — this success was declared
              by the platform, not reported by the agent on the device.
            </span>
            <ReconcileTimingFacts session={session} />
          </div>
        </div>
      )}
      {ntpOffset && Math.abs(ntpOffset.offsetSeconds) > 30 && (
        <div className="mt-4 flex items-center gap-2 px-3 py-2 rounded-lg bg-amber-50 border border-amber-200 text-sm text-amber-800">
          <svg className="w-4 h-4 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L4.082 16.5c-.77.833.192 2.5 1.732 2.5z" />
          </svg>
          <span>
            <strong>Clock Skew:</strong> Device clock is {Math.abs(ntpOffset.offsetSeconds).toFixed(1)}s
            {ntpOffset.offsetSeconds > 0 ? " ahead of" : " behind"} UTC
            {ntpOffset.ntpServer && <> (NTP: {ntpOffset.ntpServer})</>}
          </span>
        </div>
      )}
      {configMgrDetected && (
        <div className="mt-4 flex items-start gap-2 px-3 py-2 rounded-lg bg-amber-50 border border-amber-200 text-sm text-amber-800">
          <svg className="w-4 h-4 flex-shrink-0 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L4.082 16.5c-.77.833.192 2.5 1.732 2.5z" />
          </svg>
          <span>
            <strong>Co-Management:</strong> ConfigMgr client detected (confidence: {configMgrDetected.confidenceScore}/100)
            {configMgrDetected.ccmServiceState && configMgrDetected.ccmServiceState !== "not_found" && <> — {configMgrDetected.ccmServiceState}</>}
            {configMgrDetected.ccmVersion && configMgrDetected.ccmVersion !== "unknown" && <>, v{configMgrDetected.ccmVersion}</>}
            {configMgrDetected.siteCode && configMgrDetected.siteCode !== "unknown" && <>, site {configMgrDetected.siteCode}</>}.
            {" "}The SCCM agent may trigger reboots during ESP that disrupt the enrollment flow and cause unexpected event sequences (e.g., device events appearing in the account phase, AccountSetup never starting).
          </span>
        </div>
      )}
      <FailureSnapshotBlock failureSnapshotJson={session.failureSnapshotJson} />
    </div>
  );
}

/**
 * Friendly enrollment-type label for the summary card. V2 = Windows Autopilot Device
 * Preparation; everything else = classic Autopilot. White Glove (pre-provisioning) is appended
 * as a qualifier on either rail. Gather-rules diagnostic sessions are labelled distinctly
 * (they already carry the violet banner above).
 */
function enrollmentTypeLabel(session: Session, isGatherRulesSession: boolean): string {
  if (isGatherRulesSession) return "Gather Rules";
  const base =
    session.enrollmentType === "v2"
      ? "Device Preparation"
      : session.isSelfDeployingProfile
        ? "Autopilot (Self-Deploying)"
        : "Autopilot";
  return session.isPreProvisioned ? `${base} (PreProvisioned)` : base;
}

/** Entra (Azure AD) vs Hybrid Azure AD join, from the session's stored profile-derived flag. */
function joinTypeLabel(session: Session): string {
  return session.isHybridJoin ? "Hybrid Join" : "Entra Join";
}

function InfoItem({ label, value, copyText, tooltip }: { label: string; value: React.ReactNode; copyText?: string; tooltip?: string }) {
  const [copied, setCopied] = useState(false);

  const copyValue = async () => {
    if (!copyText) return;
    try {
      await navigator.clipboard.writeText(copyText);
      setCopied(true);
      setTimeout(() => setCopied(false), 1400);
    } catch (err) {
      console.error("Failed to copy value:", err);
    }
  };

  return (
    <div title={tooltip}>
      <div className="text-sm font-medium text-gray-500">{label}</div>
      <div className="mt-1 group flex items-center gap-1.5">
        <div className="text-sm text-gray-900 break-all">{value}</div>
        {copyText && (
          <button
            type="button"
            onClick={copyValue}
            title={copied ? "Copied!" : "Copy to clipboard"}
            aria-label={copied ? `${label} copied` : `Copy ${label}`}
            className="inline-flex items-center justify-center w-5 h-5 rounded border border-gray-200 bg-white text-gray-500 opacity-100 sm:opacity-0 sm:group-hover:opacity-100 focus:opacity-100 hover:bg-gray-50 hover:text-gray-700 transition-opacity"
          >
            {copied ? (
              <svg className="w-3.5 h-3.5 text-green-600" viewBox="0 0 20 20" fill="currentColor">
                <path fillRule="evenodd" d="M16.704 5.29a1 1 0 010 1.42l-7.2 7.2a1 1 0 01-1.415 0l-3.2-3.2a1 1 0 111.414-1.42l2.493 2.494 6.493-6.494a1 1 0 011.415 0z" clipRule="evenodd" />
              </svg>
            ) : (
              <svg className="w-3.5 h-3.5" viewBox="0 0 20 20" fill="currentColor">
                <path d="M6 2a2 2 0 00-2 2v8a2 2 0 002 2h1v-2H6V4h7v1h2V4a2 2 0 00-2-2H6z" />
                <path d="M9 7a2 2 0 00-2 2v7a2 2 0 002 2h7a2 2 0 002-2V9a2 2 0 00-2-2H9z" />
              </svg>
            )}
          </button>
        )}
      </div>
    </div>
  );
}

// Compact human silence duration ("5h 6m", "45m", "2d 3h") from milliseconds.
function formatSilence(ms: number): string {
  const totalMin = Math.max(0, Math.round(ms / 60000));
  const d = Math.floor(totalMin / 1440);
  const h = Math.floor((totalMin % 1440) / 60);
  const m = totalMin % 60;
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${m}m`;
  return `${m}m`;
}

// Structured, scannable timing facts under the "Backend reconciled" banner: the reconcile
// reason names the full silence-to-declaration window in prose; these chips give the customer
// the raw UTC anchors — last agent contact and when the sweep first flagged the silence — so
// "user finished and powered off" is distinguishable from "declared too early" (session efbc17ff).
function ReconcileTimingFacts({ session }: { session: Session }) {
  const fmtUtc = (iso?: string) =>
    iso
      ? `${new Date(iso).toLocaleString([], { dateStyle: "short", timeStyle: "short", timeZone: "UTC" })} UTC`
      : null;
  const lastContact = fmtUtc(session.lastEventAt);
  const flaggedSilent = fmtUtc(session.stalledAt);
  const silenceGap =
    session.lastEventAt && session.stalledAt
      ? formatSilence(new Date(session.stalledAt).getTime() - new Date(session.lastEventAt).getTime())
      : null;

  if (!lastContact && !flaggedSilent) return null;

  return (
    <dl className="mt-2 flex flex-wrap gap-x-5 gap-y-1 text-xs text-sky-700 dark:text-sky-300">
      {lastContact && (
        <div className="flex gap-1.5">
          <dt className="font-medium">Last agent contact:</dt>
          <dd>{lastContact}</dd>
        </div>
      )}
      {flaggedSilent && (
        <div className="flex gap-1.5">
          <dt className="font-medium">First flagged silent:</dt>
          <dd>
            {flaggedSilent}
            {silenceGap && <span className="opacity-80"> ({silenceGap} of silence)</span>}
          </dd>
        </div>
      )}
    </dl>
  );
}

function StatusBadge({ status, failureReason, failureSource, adminMarkedAction, reconcileReason }: { status: string; failureReason?: string; failureSource?: string; adminMarkedAction?: string; reconcileReason?: string }) {
  // Delegate the status pill (+ timeout affordance + "manual"/"reconciled" badges) to the shared,
  // canonical SessionStatusBadge so this page picks up every status — incl. the AwaitingUser/Incomplete
  // reclassification states — from one source instead of a divergent local map.
  const badge = (
    <SessionStatusBadge status={status} failureReason={failureReason} adminMarkedAction={adminMarkedAction} reconcileReason={reconcileReason} />
  );

  // Session-detail-only affordance: link a rule-attributed failure back to its analyze rule.
  const ruleId = status === "Failed" && failureSource && failureSource.startsWith("rule:")
    ? failureSource.substring("rule:".length)
    : null;

  if (!ruleId) return badge;

  return (
    <span className="inline-flex items-center gap-1.5">
      {badge}
      <a
        href={`/analyze-rules?highlight=${encodeURIComponent(ruleId)}`}
        className="px-1.5 py-0.5 text-[10px] leading-4 font-semibold rounded border border-red-300 bg-red-50 text-red-700 hover:bg-red-100 transition-colors"
        title={`Failed by analyze rule ${ruleId}${failureReason ? ` — ${failureReason}` : ""}`}
      >
        via rule {ruleId}
      </a>
    </span>
  );
}
