"use client";

import { useEffect, useState, useRef } from "react";
import { useParams, useRouter } from "next/navigation";
import { useSignalR } from "../../../contexts/SignalRContext";
import { useTenant } from "../../../contexts/TenantContext";
import { useAuth } from "../../../contexts/AuthContext";
import { useNotifications } from "../../../contexts/NotificationContext";
import { ProtectedRoute } from "../../../components/ProtectedRoute";
import { api } from "@/lib/api";
import { formatInlineMarkdown } from "@/lib/formatInlineMarkdown";
import { interpolateRuleTemplate } from "@/lib/interpolateRuleTemplate";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { ConfidenceBadge, SeverityBadge } from "./components/DiagnosisBadges";
import { Session, EnrollmentEvent, RuleResult } from "@/types";
import { useAdminMode } from "@/hooks/useAdminMode";
import { isGuid } from "@/utils/inputValidation";

export default function DiagnosisPage() {
  const params = useParams();
  const router = useRouter();
  const sessionId = params?.sessionId as string;

  const [session, setSession] = useState<Session | null>(null);
  const [sessionTenantId, setSessionTenantId] = useState<string | null>(null);
  const [events, setEvents] = useState<EnrollmentEvent[]>([]);
  const [analysisResults, setAnalysisResults] = useState<RuleResult[]>([]);
  const [loading, setLoading] = useState(true);
  const [expandedResult, setExpandedResult] = useState<string | null>(null);
  const [showEvidence, setShowEvidence] = useState<string | null>(null);
  const [copiedId, setCopiedId] = useState<string | null>(null);

  const hasInitialFetch = useRef(false);
  const lastFetchedSessionId = useRef<string | null>(null);
  const hasJoinedGroups = useRef(false);
  const sessionIdRef = useRef(sessionId);
  // Eager tenant hydration lets the session + analysis fetches run in parallel,
  // so the loading gate must wait for BOTH — otherwise a fast analysis response
  // (or a slow/failed session fetch) would clear loading while `session` is
  // still null and the page would flash a wrong IN PROGRESS / empty state.
  const sessionFetchDone = useRef(false);
  const analysisFetchDone = useRef(false);

  const finishLoadingWhenSettled = () => {
    if (sessionFetchDone.current && analysisFetchDone.current) {
      setLoading(false);
    }
  };

  const { on, off, isConnected, joinGroup, leaveGroup } = useSignalR();
  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();

  const { globalAdminMode } = useAdminMode();

  useEffect(() => {
    if (!sessionId) return;
    if (!globalAdminMode && !tenantId) return; // wait for real tenant ID
    sessionIdRef.current = sessionId;
    if (lastFetchedSessionId.current !== sessionId) {
      hasInitialFetch.current = false;
      lastFetchedSessionId.current = sessionId;
      sessionFetchDone.current = false;
      analysisFetchDone.current = false;
      setSessionTenantId(null);
      setLoading(true);
    }
    if (hasInitialFetch.current) return;
    hasInitialFetch.current = true;

    // Performance: eager-set sessionTenantId from TenantContext when known, so
    // the events/analysis effect fires in parallel with fetchSessionDetails
    // instead of waiting for its roundtrip — eliminates the fetch waterfall.
    // Global Admins in all-tenant view fall through with null (backend resolves
    // the tenant from the session itself).
    if (!globalAdminMode && isGuid(tenantId)) {
      setSessionTenantId(tenantId);
    }

    fetchSessionDetails();
  }, [sessionId, tenantId, globalAdminMode]);

  useEffect(() => {
    if (sessionTenantId && sessionId) {
      Promise.all([fetchEvents(), fetchAnalysisResults()]);
    }
  }, [sessionTenantId, sessionId]);

  // SignalR groups - subscribe-then-fetch pattern
  useEffect(() => {
    const effectiveTenantId = sessionTenantId || tenantId;
    if (!sessionId || !isConnected || !effectiveTenantId) return;
    if (!hasJoinedGroups.current) {
      const joinAndCatchUp = async () => {
        await joinGroup(`session-${effectiveTenantId}-${sessionId}`);
        hasJoinedGroups.current = true;
        // Re-fetch after group join to catch any missed during join
        Promise.all([fetchEvents(), fetchAnalysisResults()]);
      };
      joinAndCatchUp();
    }
    return () => {
      if (hasJoinedGroups.current && effectiveTenantId) {
        leaveGroup(`session-${effectiveTenantId}-${sessionId}`);
        hasJoinedGroups.current = false;
      }
    };
  }, [sessionId, isConnected, sessionTenantId, tenantId]);

  // Real-time analysis updates
  useEffect(() => {
    const handleEventStream = (data: {
      sessionId: string;
      session: Session;
      newRuleResults?: RuleResult[];
    }) => {
      if (data.sessionId !== sessionIdRef.current) return;
      if (data.session) setSession(data.session);
      if (data.newRuleResults?.length) {
        setAnalysisResults((prev) => {
          const existingIds = new Set(prev.map((r) => r.ruleId));
          const newResults = data.newRuleResults!.filter(
            (r) => !existingIds.has(r.ruleId)
          );
          return [...prev, ...newResults].sort(
            (a, b) => b.confidenceScore - a.confidenceScore
          );
        });
      }
    };
    on("eventStream", handleEventStream);
    return () => {
      off("eventStream", handleEventStream);
    };
  }, [on, off]);

  const fetchSessionDetails = async () => {
    try {
      // Fetch the single session directly instead of pulling the entire session
      // list and .find()-ing client-side. The backend resolves the tenant via
      // FindSessionTenantIdAsync for global admins when tenantId is omitted.
      const knownTenantId = sessionTenantId || (!globalAdminMode ? tenantId : null);
      const endpoint =
        knownTenantId && isGuid(knownTenantId)
          ? api.sessions.get(sessionId, knownTenantId)
          : api.sessions.get(sessionId);
      const response = await authenticatedFetch(endpoint, getAccessToken);
      if (response.ok) {
        const data = await response.json();
        const found =
          data.session ??
          data.sessions?.find((s: Session) => s.sessionId === sessionId);
        if (found) {
          setSession(found);
          setSessionTenantId(found.tenantId);
        }
      } else {
        addNotification('error', 'Backend Error', `Failed to load session: ${response.statusText}`, 'diagnosis-fetch-error');
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error("Failed to fetch session details:", error);
        addNotification('error', 'Backend Not Reachable', 'Unable to load session details. Please check your connection.', 'diagnosis-fetch-error');
      }
    } finally {
      sessionFetchDone.current = true;
      finishLoadingWhenSettled();
    }
  };

  const fetchEvents = async () => {
    const effectiveTenantId = sessionTenantId || tenantId;
    try {
      const response = await authenticatedFetch(
        api.sessions.events(sessionId, effectiveTenantId),
        getAccessToken
      );
      if (response.ok) {
        const data = await response.json();
        setEvents(data.events || []);
      } else {
        addNotification('error', 'Backend Error', `Failed to load events: ${response.statusText}`, 'diagnosis-events-error');
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error("Failed to fetch events:", error);
        addNotification('error', 'Backend Not Reachable', 'Unable to load session events. Please check your connection.', 'diagnosis-events-error');
      }
    }
  };

  const fetchAnalysisResults = async () => {
    const effectiveTenantId = sessionTenantId || tenantId;
    try {
      const response = await authenticatedFetch(
        api.sessions.analysis(sessionId, effectiveTenantId),
        getAccessToken
      );
      if (response.ok) {
        const data = await response.json();
        if (data.results) {
          setAnalysisResults(
            data.results.sort(
              (a: RuleResult, b: RuleResult) =>
                b.confidenceScore - a.confidenceScore
            )
          );
        }
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error("Failed to fetch analysis results:", error);
      }
    } finally {
      analysisFetchDone.current = true;
      finishLoadingWhenSettled();
    }
  };

  const copyRemediation = (result: RuleResult) => {
    const text = result.remediation
      .map((r) => {
        const title = interpolateRuleTemplate(r.title, result.matchedConditions);
        const steps = r.steps
          .map((s) => `  - ${interpolateRuleTemplate(s, result.matchedConditions)}`)
          .join("\n");
        return `${title}\n${steps}`;
      })
      .join("\n\n");
    navigator.clipboard.writeText(text).then(() => {
      setCopiedId(result.ruleId);
      setTimeout(() => setCopiedId(null), 2000);
    });
  };

  // Derive error/warning events for evidence
  const errorEvents = events.filter(
    (e) => e.severity === "Error" || e.severity === "Critical"
  );
  const warningEvents = events.filter((e) => e.severity === "Warning");

  // Keep this early-return wrapped in <ProtectedRoute>: on a fresh direct
  // navigation (new tab, bookmark, shared link) the auth cache is empty, so the
  // tenant-gated fetch never runs and `loading` stays true forever. Without the
  // gate here we'd hang on "Loading diagnosis..." and never trigger the MSAL
  // login redirect. See the matching note in app/sessions/[sessionId]/page.tsx.
  if (loading) {
    return (
      <ProtectedRoute>
        <div className="min-h-screen bg-gray-50 flex items-center justify-center">
          <div className="text-gray-600">Loading diagnosis...</div>
        </div>
      </ProtectedRoute>
    );
  }

  const primaryResult = analysisResults[0] || null;
  const otherResults = analysisResults.slice(1);

  const statusLabel =
    session?.status === "Failed"
      ? "FAILED"
      : session?.status === "Succeeded"
      ? "SUCCEEDED"
      : "IN PROGRESS";

  const statusColor =
    session?.status === "Failed"
      ? "text-red-600"
      : session?.status === "Succeeded"
      ? "text-green-600"
      : "text-blue-600";

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        {/* Header */}
        <header className="bg-white shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <div className="flex items-center justify-between">
              <div>
                <button
                  onClick={() => router.back()}
                  className="text-sm text-gray-600 hover:text-gray-900 mb-2 flex items-center"
                >
                  &larr; Back
                </button>
                <div className="flex items-center space-x-3">
                  <svg
                    className="w-8 h-8 text-amber-500"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth={2}
                      d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
                    />
                  </svg>
                  <div>
                    <h1 className="text-2xl font-normal text-gray-900">
                      Diagnosis
                    </h1>
                    <p className="text-sm text-gray-500">
                      Session{" "}
                      <span className="font-mono">
                        {sessionId.split("-")[0]?.toUpperCase()}
                      </span>{" "}
                      <span className={`font-semibold ${statusColor}`}>
                        {statusLabel}
                      </span>
                      {session && (
                        <span className="ml-2 text-gray-400">
                          | {session.deviceName || session.serialNumber} |{" "}
                          {session.manufacturer} {session.model}
                        </span>
                      )}
                    </p>
                  </div>
                </div>
              </div>
              <div className="flex items-center space-x-3">
                <button
                  onClick={() =>
                    router.push(`/sessions/${sessionId}`)
                  }
                  className="px-4 py-2 text-sm bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors"
                >
                  Full Details
                </button>
              </div>
            </div>
          </div>
        </header>

        <main className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
          {analysisResults.length === 0 ? (
            <div className="bg-white shadow rounded-lg p-12 text-center">
              <svg
                className="w-16 h-16 mx-auto text-gray-300 mb-4"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={1.5}
                  d="M9.663 17h4.673M12 3v1m6.364 1.636l-.707.707M21 12h-1M4 12H3m3.343-5.657l-.707-.707m2.828 9.9a5 5 0 117.072 0l-.548.547A3.374 3.374 0 0014 18.469V19a2 2 0 11-4 0v-.531c0-.895-.356-1.754-.988-2.386l-.548-.547z"
                />
              </svg>
              <h2 className="text-xl font-semibold text-gray-900 mb-2">
                No Diagnosis Available
              </h2>
              <p className="text-gray-500 mb-4">
                {session?.status === "InProgress"
                  ? "Analysis results will appear here as they are generated during the enrollment."
                  : session?.status === "Succeeded"
                  ? "This session completed successfully. No issues were detected."
                  : "No analysis rules matched for this session. Check the event timeline for more details."}
              </p>
              {errorEvents.length > 0 && (
                <div className="mt-6 text-left max-w-lg mx-auto">
                  <h3 className="text-sm font-medium text-gray-700 mb-2">
                    Errors found in events ({errorEvents.length}):
                  </h3>
                  <div className="space-y-1">
                    {errorEvents.slice(0, 5).map((e) => (
                      <div
                        key={e.eventId}
                        className="text-sm text-red-600 bg-red-50 p-2 rounded"
                      >
                        {e.message}
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </div>
          ) : (
            <div className="space-y-6">
              {/* Primary Suspect */}
              {primaryResult && (
                <div
                  className={`bg-white shadow-lg rounded-lg overflow-hidden border-l-4 ${
                    primaryResult.severity === "critical"
                      ? "border-l-red-600"
                      : primaryResult.severity === "high"
                      ? "border-l-orange-500"
                      : primaryResult.severity === "warning"
                      ? "border-l-yellow-500"
                      : "border-l-blue-500"
                  }`}
                >
                  <div className="p-6">
                    <div className="flex items-start justify-between mb-4">
                      <div className="flex items-center space-x-3">
                        <span className="text-2xl">
                          {primaryResult.severity === "critical"
                            ? "\uD83C\uDFAF"
                            : "\uD83D\uDD0D"}
                        </span>
                        <div>
                          <div className="text-sm text-gray-500 uppercase font-medium tracking-wide">
                            Primary Suspect
                          </div>
                        </div>
                      </div>
                      <div className="flex items-center space-x-2">
                        <ConfidenceBadge
                          score={primaryResult.confidenceScore}
                        />
                        <SeverityBadge severity={primaryResult.severity} />
                      </div>
                    </div>

                    <h2 className="text-2xl font-bold text-gray-900 mb-3">
                      {primaryResult.ruleTitle}
                    </h2>

                    <p className="text-gray-600 leading-relaxed mb-6">
                      {interpolateRuleTemplate(primaryResult.explanation, primaryResult.matchedConditions)}
                    </p>

                    {/* Evidence Summary */}
                    {primaryResult.matchedConditions &&
                      Object.keys(primaryResult.matchedConditions).length >
                        0 && (
                        <div className="mb-6">
                          <h3 className="text-sm font-semibold text-gray-700 mb-2">
                            Evidence Found:
                          </h3>
                          <div className="space-y-1">
                            {Object.entries(
                              primaryResult.matchedConditions
                            ).map(([key, value]) => (
                              <div
                                key={key}
                                className="flex items-start space-x-2 text-sm"
                              >
                                <span className="text-gray-400 mt-0.5">
                                  &#x2022;
                                </span>
                                <span className="text-gray-600">
                                  <span className="font-medium">{key}:</span>{" "}
                                  {typeof value === "object"
                                    ? JSON.stringify(value)
                                    : String(value)}
                                </span>
                              </div>
                            ))}
                          </div>
                        </div>
                      )}

                    {/* Quick Fix Box */}
                    {primaryResult.remediation &&
                      primaryResult.remediation.length > 0 && (
                        <div className="bg-green-50 border border-green-200 rounded-lg p-4">
                          <div className="flex items-center justify-between mb-3">
                            <h3 className="text-sm font-semibold text-green-800">
                              Quick Fix
                            </h3>
                            <button
                              onClick={() =>
                                copyRemediation(primaryResult)
                              }
                              className="text-xs px-3 py-1 bg-green-100 text-green-700 rounded hover:bg-green-200 transition-colors"
                            >
                              {copiedId === primaryResult.ruleId
                                ? "Copied!"
                                : "Copy All"}
                            </button>
                          </div>
                          {primaryResult.remediation.map((rem, i) => (
                            <div key={i} className="mb-3 last:mb-0">
                              <p className="text-sm font-medium text-green-700 mb-1">
                                {interpolateRuleTemplate(rem.title, primaryResult.matchedConditions)}
                              </p>
                              <ul className="space-y-1">
                                {rem.steps.map((step, j) => (
                                  <li
                                    key={j}
                                    className="text-sm text-green-600 flex items-start space-x-2"
                                  >
                                    <span className="text-green-400 mt-0.5">
                                      &#x2022;
                                    </span>
                                    <span>{interpolateRuleTemplate(step, primaryResult.matchedConditions)}</span>
                                  </li>
                                ))}
                              </ul>
                            </div>
                          ))}
                        </div>
                      )}

                    {/* Action Buttons */}
                    <div className="flex items-center space-x-3 mt-4">
                      {primaryResult.relatedDocs?.length > 0 && (
                        <a
                          href={primaryResult.relatedDocs[0].url}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="inline-flex items-center space-x-1 text-sm text-blue-600 hover:text-blue-800"
                        >
                          <svg
                            className="w-4 h-4"
                            fill="none"
                            viewBox="0 0 24 24"
                            stroke="currentColor"
                          >
                            <path
                              strokeLinecap="round"
                              strokeLinejoin="round"
                              strokeWidth={2}
                              d="M12 6.253v13m0-13C10.832 5.477 9.246 5 7.5 5S4.168 5.477 3 6.253v13C4.168 18.477 5.754 18 7.5 18s3.332.477 4.5 1.253m0-13C13.168 5.477 14.754 5 16.5 5c1.747 0 3.332.477 4.5 1.253v13C19.832 18.477 18.247 18 16.5 18c-1.746 0-3.332.477-4.5 1.253"
                            />
                          </svg>
                          <span>Learn More</span>
                        </a>
                      )}
                      <button
                        onClick={() =>
                          setShowEvidence(
                            showEvidence === primaryResult.ruleId
                              ? null
                              : primaryResult.ruleId
                          )
                        }
                        className="inline-flex items-center space-x-1 text-sm text-gray-600 hover:text-gray-800"
                      >
                        <svg
                          className="w-4 h-4"
                          fill="none"
                          viewBox="0 0 24 24"
                          stroke="currentColor"
                        >
                          <path
                            strokeLinecap="round"
                            strokeLinejoin="round"
                            strokeWidth={2}
                            d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
                          />
                        </svg>
                        <span>
                          {showEvidence === primaryResult.ruleId
                            ? "Hide Evidence"
                            : "View Evidence"}
                        </span>
                      </button>
                    </div>

                    {/* Evidence Detail */}
                    {showEvidence === primaryResult.ruleId && (
                      <div className="mt-4">
                        <EvidenceEventLinks matchedConditions={primaryResult.matchedConditions} sessionId={sessionId} />
                        <div className="p-4 bg-gray-900 rounded-lg">
                          <pre className="text-xs text-gray-100 font-mono overflow-x-auto whitespace-pre-wrap">
                            {JSON.stringify(
                              primaryResult.matchedConditions,
                              null,
                              2
                            )}
                          </pre>
                        </div>
                      </div>
                    )}
                  </div>
                </div>
              )}

              {/* Other Possibilities */}
              {otherResults.length > 0 && (
                <div className="bg-white shadow rounded-lg overflow-hidden">
                  <div className="px-6 py-4 border-b border-gray-200">
                    <h2 className="text-lg font-semibold text-gray-900">
                      Other Possibilities
                    </h2>
                  </div>
                  <div className="divide-y divide-gray-100">
                    {otherResults.map((result) => (
                      <div key={result.ruleId} className="px-6 py-4">
                        <div
                          className="flex items-center justify-between cursor-pointer"
                          onClick={() =>
                            setExpandedResult(
                              expandedResult === result.ruleId
                                ? null
                                : result.ruleId
                            )
                          }
                        >
                          <div className="flex items-center space-x-4">
                            <ConfidenceBadge
                              score={result.confidenceScore}
                              compact
                            />
                            <div>
                              <div className="text-sm font-medium text-gray-900">
                                {result.ruleTitle}
                              </div>
                              <div className="text-xs text-gray-500">
                                {result.category}
                              </div>
                            </div>
                          </div>
                          <div className="flex items-center space-x-3">
                            <SeverityBadge
                              severity={result.severity}
                              compact
                            />
                            <span className="text-gray-400">
                              {expandedResult === result.ruleId
                                ? "\u25BC"
                                : "\u25B6"}
                            </span>
                          </div>
                        </div>

                        {expandedResult === result.ruleId && (
                          <div className="mt-4 pl-16 space-y-3">
                            <p className="text-sm text-gray-600">
                              {formatInlineMarkdown(interpolateRuleTemplate(result.explanation, result.matchedConditions))}
                            </p>

                            {result.remediation?.length > 0 && (
                              <div className="bg-gray-50 rounded-lg p-3">
                                <div className="flex items-center justify-between mb-2">
                                  <h4 className="text-xs font-semibold text-gray-700">
                                    Remediation
                                  </h4>
                                  <button
                                    onClick={(e) => {
                                      e.stopPropagation();
                                      copyRemediation(result);
                                    }}
                                    className="text-xs text-blue-600 hover:text-blue-800"
                                  >
                                    {copiedId === result.ruleId
                                      ? "Copied!"
                                      : "Copy"}
                                  </button>
                                </div>
                                {result.remediation.map((rem, i) => (
                                  <div key={i} className="mb-2 last:mb-0">
                                    <p className="text-xs font-medium text-gray-700">
                                      {interpolateRuleTemplate(rem.title, result.matchedConditions)}
                                    </p>
                                    <ul className="list-disc list-inside text-xs text-gray-600 ml-2">
                                      {rem.steps.map((step, j) => (
                                        <li key={j}>{interpolateRuleTemplate(step, result.matchedConditions)}</li>
                                      ))}
                                    </ul>
                                  </div>
                                ))}
                              </div>
                            )}

                            {result.matchedConditions &&
                              Object.keys(result.matchedConditions).length >
                                0 && (
                                <div>
                                  <button
                                    onClick={() =>
                                      setShowEvidence(
                                        showEvidence === result.ruleId
                                          ? null
                                          : result.ruleId
                                      )
                                    }
                                    className="text-xs text-gray-500 hover:text-gray-700"
                                  >
                                    {showEvidence === result.ruleId
                                      ? "Hide Evidence"
                                      : "Show Evidence"}
                                  </button>
                                  {showEvidence === result.ruleId && (
                                    <div className="mt-2">
                                      <EvidenceEventLinks matchedConditions={result.matchedConditions} sessionId={sessionId} />
                                      <div className="p-3 bg-gray-900 rounded text-xs text-gray-100 font-mono overflow-x-auto">
                                        <pre>
                                          {JSON.stringify(
                                            result.matchedConditions,
                                            null,
                                            2
                                          )}
                                        </pre>
                                      </div>
                                    </div>
                                  )}
                                </div>
                              )}

                            {result.relatedDocs?.length > 0 && (
                              <div className="flex flex-wrap gap-2">
                                {result.relatedDocs.map((doc, i) => (
                                  <a
                                    key={i}
                                    href={doc.url}
                                    target="_blank"
                                    rel="noopener noreferrer"
                                    className="text-xs text-blue-600 hover:text-blue-800 underline"
                                  >
                                    {doc.title}
                                  </a>
                                ))}
                              </div>
                            )}
                          </div>
                        )}
                      </div>
                    ))}
                  </div>
                </div>
              )}

              {/* Error Events Summary */}
              {errorEvents.length > 0 && (
                <div className="bg-white shadow rounded-lg p-6">
                  <h2 className="text-lg font-semibold text-gray-900 mb-4">
                    Error Events ({errorEvents.length})
                  </h2>
                  <div className="space-y-2 max-h-64 overflow-y-auto">
                    {errorEvents.map((e) => (
                      <div
                        key={e.eventId}
                        className="flex items-start space-x-3 text-sm bg-red-50 rounded-lg p-3"
                      >
                        <span className="text-red-400 mt-0.5 flex-shrink-0">
                          &#x2716;
                        </span>
                        <div className="flex-1 min-w-0">
                          <div className="flex items-center space-x-2 mb-0.5">
                            <span className="text-xs text-gray-500 font-mono">
                              {new Date(e.timestamp).toLocaleTimeString()}
                            </span>
                            <span className="text-xs text-gray-400">
                              {e.source}
                            </span>
                          </div>
                          <p className="text-red-700">{e.message}</p>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              )}

              {/* Warning Events Summary */}
              {warningEvents.length > 0 && (
                <div className="bg-white shadow rounded-lg p-6">
                  <h2 className="text-lg font-semibold text-gray-900 mb-4">
                    Warnings ({warningEvents.length})
                  </h2>
                  <div className="space-y-2 max-h-48 overflow-y-auto">
                    {warningEvents.map((e) => (
                      <div
                        key={e.eventId}
                        className="flex items-start space-x-3 text-sm bg-yellow-50 rounded-lg p-3"
                      >
                        <span className="text-yellow-500 mt-0.5 flex-shrink-0">
                          &#x26A0;
                        </span>
                        <div className="flex-1 min-w-0">
                          <div className="flex items-center space-x-2 mb-0.5">
                            <span className="text-xs text-gray-500 font-mono">
                              {new Date(e.timestamp).toLocaleTimeString()}
                            </span>
                          </div>
                          <p className="text-yellow-700">{e.message}</p>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </div>
          )}
        </main>
      </div>
    </ProtectedRoute>
  );
}

function EvidenceEventLinks({ matchedConditions, sessionId }: { matchedConditions: Record<string, any>; sessionId: string }) {
  const eventLinks: { signal: string; eventId: string; eventType?: string }[] = [];

  for (const [signal, evidence] of Object.entries(matchedConditions)) {
    if (signal.startsWith("factor_")) continue;
    if (evidence && typeof evidence === "object" && evidence.eventId) {
      eventLinks.push({ signal, eventId: evidence.eventId, eventType: evidence.eventType });
    }
  }

  if (eventLinks.length === 0) return null;

  return (
    <div className="flex flex-wrap gap-2 mb-2">
      {eventLinks.map(({ signal, eventId, eventType }) => (
        <a
          key={signal}
          href={`/sessions/${sessionId}#event-${eventId}`}
          className="inline-flex items-center space-x-1 px-2 py-1 text-xs font-medium text-amber-700 bg-amber-50 hover:bg-amber-100 border border-amber-200 rounded transition-colors"
          title={`View event in timeline: ${eventId}`}
        >
          <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 7l5 5m0 0l-5 5m5-5H6" />
          </svg>
          <span>{eventType || signal}</span>
        </a>
      ))}
    </div>
  );
}

