"use client";

import { useEffect, useMemo, useState } from "react";
import { useParams, useRouter, useSearchParams } from "next/navigation";
import { useSignalR } from "../../../contexts/SignalRContext";
import { useTenant } from "../../../contexts/TenantContext";
import { useAuth } from "../../../contexts/AuthContext";
import { useNotifications } from "../../../contexts/NotificationContext";
import { ProtectedRoute } from '../../../components/ProtectedRoute';
import PerformanceChart from '../../../components/PerformanceChart';
import DownloadProgress from '../../../components/DownloadProgress';
import InstallProgress from '../../../components/InstallProgress';
import ScriptExecutions from '../../../components/ScriptExecutions';
import { useLatestVersions } from '@/lib/useLatestVersions';
import { useScriptDisplayNames } from '@/lib/scriptDisplayNames';
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

import { useSessionAnalysis } from "./hooks/useSessionAnalysis";
import { useAutoScroll } from "./hooks/useAutoScroll";
import { useSessionDetail } from "./hooks/useSessionDetail";
import { useSessionEvents } from "./hooks/useSessionEvents";
import { useSessionSignalR } from "./hooks/useSessionSignalR";
import { useSessionDerivedData } from "./hooks/useSessionDerivedData";
import { useSessionTenantConfig } from "./hooks/useSessionTenantConfig";

import SessionInfoCard from "./components/SessionInfoCard";
import PhaseTimeline from "./components/PhaseTimeline";
import EventTimeline from "./components/EventTimeline";
import AnalysisResultsSection from "./components/AnalysisResultsSection";
import VulnerabilityReportSection from "./components/VulnerabilityReportSection";
import IntegrityBypassSection from "./components/IntegrityBypassSection";
import AdminOverrideModal from "./components/AdminOverrideModal";
import ReportSessionModal from "./components/ReportSessionModal";
import { usePageSections } from "../../../hooks/usePageSections";
import { PageSectionItem } from "../../../contexts/SidebarContext";
import { InformationCircleIcon, ComputerDesktopIcon, PlayCircleIcon, SparklesIcon, ChartBarIcon, CodeBracketIcon, ArrowDownTrayIcon, ListBulletIcon, ClockIcon, ShieldCheckIcon } from "../../../lib/sidebarIcons";
import DeviceDetailsCard from "./components/DeviceDetailsCard";
import { generateUiExport, generateCsvExport, generateSessionCsvExport, generateRuleResultsCsvExport, SessionExportEvent } from "@/utils/sessionExportUtils";
import { trackEvent } from "@/lib/appInsights";
import { useAdminMode } from "@/hooks/useAdminMode";

export default function SessionDetailPage() {
  const params = useParams();
  const router = useRouter();
  const searchParams = useSearchParams();
  const sessionId = params?.sessionId as string;
  // Explicit target tenant from the fleet drill-in (`?tenantId=`). Drives the cross-tenant reads for a
  // delegated ("MSP") admin viewing a managed tenant's session, and flips the page into read-only mode.
  const tenantIdOverride = searchParams?.get("tenantId") || undefined;

  // UI-only state (modals, expand/collapse, severity filters)
  const [severityFilters, setSeverityFilters] = useState<Set<string>>(new Set(["Info", "Warning", "Error", "Critical"]));
  const [showMarkFailedConfirm, setShowMarkFailedConfirm] = useState(false);
  const [showMarkSucceededConfirm, setShowMarkSucceededConfirm] = useState(false);
  const [showReportModal, setShowReportModal] = useState(false);
  const [reportSubmitting, setReportSubmitting] = useState(false);
  const [analysisExpanded, setAnalysisExpanded] = useState(true);
  const [vulnerabilityReportExpanded, setVulnerabilityReportExpanded] = useState(false);
  const [integrityBypassExpanded, setIntegrityBypassExpanded] = useState(false);
  const [phaseTimelineExpanded, setPhaseTimelineExpanded] = useState(true);
  const [perfExpanded, setPerfExpanded] = useState(true);
  const [timelineExpanded, setTimelineExpanded] = useState(true);
  const [expandedPhases, setExpandedPhases] = useState<Set<string>>(new Set());

  const { adminMode, globalAdminMode } = useAdminMode();

  // Global contexts
  const { on, off, isConnected, joinGroup, leaveGroup } = useSignalR();
  const { tenantId } = useTenant();
  const { getAccessToken, user } = useAuth();
  const { addNotification } = useNotifications();
  const { latestAgentVersion, latestBootstrapVersion } = useLatestVersions(getAccessToken);

  // Session-scoped hooks (data/SignalR/derivations)
  const detail = useSessionDetail({ sessionId, tenantId, globalAdminMode, tenantIdOverride, user, getAccessToken, addNotification });
  const analysis = useSessionAnalysis(sessionId, detail.sessionTenantId, getAccessToken);
  const tenantConfig = useSessionTenantConfig(detail.sessionTenantId, getAccessToken);
  const eventsApi = useSessionEvents({
    sessionId,
    sessionTenantId: detail.sessionTenantId,
    sessionStatus: detail.session?.status ?? null,
    resolveEffectiveTenantId: detail.resolveEffectiveTenantId,
    sessionRef: detail.sessionRef,
    fetchSessionDetails: detail.fetchSessionDetails,
    setLoading: detail.setLoading,
    isConnected,
    getAccessToken,
    addNotification,
  });
  useSessionSignalR({
    sessionId,
    sessionTenantId: detail.sessionTenantId,
    tenantId,
    sessionTenantIdFromSession: detail.session?.tenantId,
    globalAdminMode,
    sessionIdRef: detail.sessionIdRef,
    sessionRef: detail.sessionRef,
    resolveEffectiveTenantId: detail.resolveEffectiveTenantId,
    signalR: { on, off, isConnected, joinGroup, leaveGroup },
    scheduleFetchEvents: eventsApi.scheduleFetchEvents,
    setSession: detail.setSession,
    setSessionTenantId: detail.setSessionTenantId,
    fetchAnalysisResults: analysis.fetchAnalysisResults,
    fetchVulnerabilityReport: analysis.fetchVulnerabilityReport,
  });
  const derived = useSessionDerivedData(eventsApi.events, detail.session, severityFilters);
  const { autoScroll, handleAutoScrollToggle } = useAutoScroll(eventsApi.events);

  // Convenience local aliases (keeps the JSX below readable, matches previous names)
  const { session, setSession, sessionTenantId, loading } = detail;
  const events = eventsApi.events;

  // Cross-tenant read-only view: a delegated ("MSP") admin viewing a MANAGED tenant's session. The backend
  // permits the reads (MemberRead + ?tenantId=, rescued by the delegated scope) but rejects the mutations
  // (mark-failed/succeeded/report are TenantAdminOrGA). A Global Admin keeps write rights cross-tenant, and
  // own-tenant viewers are unaffected. `tenantIdOverride` seeds this before the session object has loaded so
  // the write controls never flash in. Inspector is already gated to Global Admins separately.
  const viewedTenantId = (session?.tenantId ?? sessionTenantId ?? tenantIdOverride ?? "").toLowerCase();
  const isCrossTenantView = !!viewedTenantId && !!tenantId && viewedTenantId !== tenantId.toLowerCase();
  const isReadOnlyView = isCrossTenantView && !user?.isGlobalAdmin;

  // Browser-tab title: lead with the device identifier so multiple session tabs stay distinguishable when
  // compared side by side (tabs truncate to the first chars). En-dash separator matches the root layout
  // title. Falls back to the serial number, then to the bare app name while the session is still loading.
  useEffect(() => {
    const label = session?.deviceName || session?.serialNumber;
    document.title = label ? `${label} – Autopilot Monitor` : "Autopilot Monitor";
    return () => {
      document.title = "Autopilot Monitor";
    };
  }, [session?.deviceName, session?.serialNumber]);

  // Resolves Intune script display names via the optional Graph add-on permission.
  // Returns an empty map when the tenant hasn't granted DeviceManagementScripts.Read.All.
  // Map keys are "{Kind}:{Id}" -- the renderer uses lookupScriptDisplayName to read them.
  const scriptDisplayNamesByRefKey = useScriptDisplayNames(sessionTenantId ?? tenantId, events, getAccessToken);
  const { analysisResults, loadingAnalysis, vulnerabilityReport, fetchAnalysisResults, fetchVulnerabilityReport, persistFailureRuleIds } = analysis;
  const { showScriptOutput, enableSoftwareInventoryAnalyzer, enableIntegrityBypassAnalyzer } = tenantConfig;
  const {
    filteredEvents,
    appSummaryStats,
    ntpOffset,
    configMgrDetected,
    isGatherRulesSession,
    displayStatus,
    enrollmentDurationFromEvents,
    isSkipUserStatusPage,
    isWhiteGloveSession,
    whiteGloveSplitSequence,
    userEnrollEvents,
    whiteGloveDurations,
    eventsByPhase,
    orderedPhases,
    preProvGrouped,
    userEnrollGrouped,
  } = derived;

  // Auto-expand new phases as they appear (keeps existing expanded/collapsed state).
  // For WhiteGlove sessions we use prefixed keys (pre-X, user-X) to avoid collisions.
  useEffect(() => {
    setExpandedPhases(prev => {
      const newExpanded = new Set(prev);
      let hasChanges = false;

      const allPhases = isWhiteGloveSession
        ? [
            ...preProvGrouped.orderedPhases.map(p => `pre-${p}`),
            ...userEnrollGrouped.orderedPhases.map(p => `user-${p}`),
          ]
        : orderedPhases;

      for (const phase of allPhases) {
        if (!prev.has(phase)) {
          newExpanded.add(phase);
          hasChanges = true;
        }
      }

      return hasChanges ? newExpanded : prev;
    });
  }, [orderedPhases, preProvGrouped.orderedPhases, userEnrollGrouped.orderedPhases, isWhiteGloveSession]);

  const markAsFailed = () => setShowMarkFailedConfirm(true);
  const markAsSucceeded = () => setShowMarkSucceededConfirm(true);
  const cancelMarkFailed = () => setShowMarkFailedConfirm(false);
  const cancelMarkSucceeded = () => setShowMarkSucceededConfirm(false);

  const confirmMarkFailed = async () => {
    const effectiveTenantId = sessionTenantId || tenantId;
    try {
      const response = await authenticatedFetch(
        api.sessions.markFailed(sessionId, effectiveTenantId),
        getAccessToken,
        { method: 'POST' }
      );
      if (response.ok) {
        setShowMarkFailedConfirm(false);
        if (session) setSession({ ...session, status: 'Failed' });
      } else {
        console.error('Failed to mark session as failed');
      }
    } catch (error) {
      console.error('Error marking session as failed:', error);
    }
  };

  const confirmMarkSucceeded = async () => {
    const effectiveTenantId = sessionTenantId || tenantId;
    try {
      const response = await authenticatedFetch(
        api.sessions.markSucceeded(sessionId, effectiveTenantId),
        getAccessToken,
        { method: 'POST' }
      );
      if (response.ok) {
        setShowMarkSucceededConfirm(false);
        if (session) setSession({ ...session, status: 'Succeeded' });
      } else {
        console.error('Failed to mark session as succeeded');
      }
    } catch (error) {
      console.error('Error marking session as succeeded:', error);
    }
  };

  const handleSubmitReport = async (
    comment: string, email: string,
    screenshotBase64: string | null, screenshotFileName: string | null,
    agentLogBase64: string | null, agentLogFileName: string | null
  ) => {
    const effectiveTenantId = sessionTenantId || tenantId;
    try {
      setReportSubmitting(true);

      // Generate TXT and CSV exports from the events currently loaded
      const exportEvents: SessionExportEvent[] = events.map(e => ({
        ...e,
        tenantId: effectiveTenantId || '',
      }));
      const timelineExportTxt = generateUiExport(exportEvents, sessionId, effectiveTenantId || '', session?.status);
      const eventsCsv = generateCsvExport(exportEvents);
      const sessionCsv = session ? generateSessionCsvExport(session) : '';
      const ruleResultsCsv = generateRuleResultsCsvExport(analysisResults);

      const response = await authenticatedFetch(
        api.sessions.report(sessionId, effectiveTenantId),
        getAccessToken,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            tenantId: effectiveTenantId,
            sessionId,
            comment,
            email,
            sessionCsv,
            eventsCsv,
            ruleResultsCsv,
            timelineExportTxt,
            screenshotBase64,
            screenshotFileName,
            agentLogBase64,
            agentLogFileName
          })
        }
      );

      if (response.ok) {
        trackEvent("session_report_submitted", { sessionId });
        addNotification('success', 'Report Submitted', 'Session report has been submitted for analysis.', 'report-success');
      } else {
        const data = await response.json().catch(() => null);
        const message = data?.message || 'Failed to submit report.';
        addNotification('error', 'Report Failed', message, 'report-error');
        throw new Error(message);
      }
    } catch (err: unknown) {
      // Re-throw so the modal can show inline error feedback.
      // Only log unexpected errors (not the ones we threw ourselves above).
      const errMessage = err instanceof Error ? err.message : '';
      if (!errMessage.includes('Failed to submit report') && !errMessage.includes('Failed to get access token')) {
        console.error('Error submitting report:', err);
      }
      throw err;
    } finally {
      setReportSubmitting(false);
    }
  };

  const toggleSeverityFilter = (severity: string) => {
    setSeverityFilters(prev => {
      const next = new Set(prev);
      if (next.has(severity)) next.delete(severity);
      else next.add(severity);
      return next;
    });
  };

  const expandAll = () => {
    if (isWhiteGloveSession) {
      setExpandedPhases(new Set([
        ...preProvGrouped.orderedPhases.map(p => `pre-${p}`),
        ...userEnrollGrouped.orderedPhases.map(p => `user-${p}`),
      ]));
    } else {
      setExpandedPhases(new Set(orderedPhases));
    }
  };

  const collapseAll = () => setExpandedPhases(new Set());

  const scrollToPhase = (phaseName: string) => {
    const id = `phase-${phaseName.replace(/[^a-zA-Z0-9]/g, '-')}`;
    const el = document.getElementById(id);
    if (el) {
      setExpandedPhases(prev => {
        const newExpanded = new Set(prev);
        if (isWhiteGloveSession) {
          newExpanded.add(`pre-${phaseName}`);
          newExpanded.add(`user-${phaseName}`);
        } else {
          newExpanded.add(phaseName);
        }
        return newExpanded;
      });
      setTimeout(() => {
        el.scrollIntoView({ behavior: 'smooth', block: 'center' });
      }, 50);
    }
  };

  // Handle #event-{eventId} hash fragment (e.g. from diagnosis page links)
  useEffect(() => {
    if (loading || events.length === 0) return;
    const hash = window.location.hash;
    if (!hash.startsWith("#event-")) return;
    // Expand all phases so the event element is in the DOM
    expandAll();
    setTimeout(() => {
      const el = document.getElementById(hash.slice(1));
      if (el) {
        el.scrollIntoView({ behavior: "smooth", block: "center" });
        el.classList.add("ring-2", "ring-amber-400", "ring-offset-1");
        setTimeout(() => el.classList.remove("ring-2", "ring-amber-400", "ring-offset-1"), 3000);
      }
    }, 100);
    window.history.replaceState(null, "", window.location.pathname + window.location.search);
  }, [loading, events.length]); // eslint-disable-line react-hooks/exhaustive-deps

  const togglePhase = (phaseName: string) => {
    setExpandedPhases(prev => {
      const newExpanded = new Set(prev);
      if (newExpanded.has(phaseName)) newExpanded.delete(phaseName);
      else newExpanded.add(phaseName);
      return newExpanded;
    });
  };

  const sessionSections: PageSectionItem[] = useMemo(() => {
    const s: PageSectionItem[] = [];
    if (session) s.push({ id: "section-session-info", label: "Session Info", icon: <InformationCircleIcon /> });
    if (!isGatherRulesSession) s.push({ id: "section-device-details", label: "Device Details", icon: <ComputerDesktopIcon /> });
    if (!isGatherRulesSession && session) s.push({ id: "section-enrollment-progress", label: "Enrollment Progress", icon: <PlayCircleIcon /> });
    if (!isGatherRulesSession) s.push({ id: "section-analysis", label: "Analysis", icon: <SparklesIcon /> });
    if (!isGatherRulesSession && enableSoftwareInventoryAnalyzer) s.push({ id: "section-vulnerability-report", label: "Vulnerability Report", icon: <ShieldCheckIcon /> });
    if (!isGatherRulesSession) s.push({ id: "section-performance", label: "Performance", icon: <ChartBarIcon /> });
    if (!isGatherRulesSession) s.push({ id: "section-scripts", label: "Script Executions", icon: <CodeBracketIcon /> });
    if (!isGatherRulesSession) s.push({ id: "section-downloads", label: "Downloads", icon: <ArrowDownTrayIcon /> });
    if (!isGatherRulesSession) s.push({ id: "section-install-progress", label: "Install Progress", icon: <ListBulletIcon /> });
    s.push({ id: "section-event-timeline", label: "Event Timeline", icon: <ClockIcon /> });
    return s;
  }, [session, isGatherRulesSession, enableSoftwareInventoryAnalyzer]);

  usePageSections(sessionSections, "Sections", "scroll-spy");

  // Only show full-page loading spinner on the very first load (no data yet).
  // Subsequent refreshes (SignalR, 30s poll) keep the existing UI visible.
  // NOTE: this early-return MUST stay wrapped in <ProtectedRoute>. On a fresh
  // direct navigation (new tab, bookmark, shared link) the auth cache is empty,
  // so `loading` never flips and we'd render this branch forever — bypassing the
  // auth gate and hanging on "Loading session details..." without ever triggering
  // the MSAL login redirect. Wrapping here lets ProtectedRoute drive re-auth.
  if (loading && !session && events.length === 0) {
    return (
      <ProtectedRoute>
        <div className="min-h-screen bg-gray-50 flex items-center justify-center">
          <div className="text-gray-600">Loading session details...</div>
        </div>
      </ProtectedRoute>
    );
  }

  return (
<ProtectedRoute>
    <div className="min-h-screen bg-gray-50">
      {/* Header */}
      <header className="bg-white shadow">
        <div className="py-6 px-4 sm:px-6 lg:px-8 flex flex-wrap items-center justify-between gap-y-3 gap-x-4">
          <div>
            <h1 className="text-2xl font-normal text-gray-900">
              Session Details
            </h1>
          </div>
          <div className="flex flex-wrap items-center gap-3">
            {session?.diagnosticsBlobName && (
              <button
                onClick={async () => {
                  try {
                    const res = await authenticatedFetch(
                      api.diagnostics.downloadUrl(session.tenantId, session.diagnosticsBlobName!),
                      getAccessToken
                    );
                    if (!res.ok) throw new Error('Failed to download diagnostics package');
                    const blob = await res.blob();
                    const a = document.createElement('a');
                    a.href = URL.createObjectURL(blob);
                    a.download = session.diagnosticsBlobName!;
                    a.click();
                    URL.revokeObjectURL(a.href);
                  } catch (err) {
                    if (err instanceof TokenExpiredError) {
                      addNotification('error', 'Session Expired', err.message, 'session-expired-error');
                    } else {
                      console.error('Diagnostics download failed:', err);
                    }
                  }
                }}
                className="px-4 py-2 bg-white border border-gray-200 text-gray-700 rounded-md hover:bg-gray-50 transition-colors flex items-center gap-2 text-sm"
              >
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 10v6m0 0l-3-3m3 3l3-3m2 8H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                </svg>
                Download Diagnostics
              </button>
            )}
            {session?.status === 'Failed' && !isReadOnlyView && (
              <button
                onClick={() => router.push(`/diagnosis/${sessionId}`)}
                className="px-4 py-2 bg-amber-500 text-white rounded-md hover:bg-amber-600 transition-colors flex items-center gap-2 text-sm"
              >
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                </svg>
                Diagnosis
              </button>
            )}
            {adminMode && !isReadOnlyView && (session?.status === 'InProgress' || session?.status === 'Pending' || session?.status === 'Stalled') && (
              <>
                <button
                  onClick={markAsSucceeded}
                  className="px-4 py-2 bg-green-600 text-white rounded-md hover:bg-green-700 transition-colors flex items-center gap-2 text-sm"
                >
                  <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                  Succeed
                </button>
                <button
                  onClick={markAsFailed}
                  className="px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 transition-colors flex items-center gap-2 text-sm"
                >
                  <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                  </svg>
                  Fail
                </button>
              </>
            )}
            {!isReadOnlyView && (
              <button
                onClick={() => setShowReportModal(true)}
                className="px-4 py-2 bg-white border border-blue-300 text-blue-700 rounded-md hover:bg-blue-50 transition-colors flex items-center gap-2 text-sm"
              >
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
                </svg>
                Report Session
              </button>
            )}
            {user?.isGlobalAdmin && sessionId && (
              <a
                href={`/sessions/${sessionId}/inspector`}
                className="px-4 py-2 bg-white border border-purple-300 text-purple-700 rounded-md hover:bg-purple-50 transition-colors flex items-center gap-2 text-sm"
                title="Open Decision Inspector (Global Admin only — Plan §M6)"
              >
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 17V7m0 10a2 2 0 01-2 2H5a2 2 0 01-2-2V7a2 2 0 012-2h2a2 2 0 012 2m0 10a2 2 0 002 2h2a2 2 0 002-2M9 7a2 2 0 012-2h2a2 2 0 012 2m0 10V7m0 10a2 2 0 002 2h2a2 2 0 002-2V7a2 2 0 00-2-2h-2a2 2 0 00-2 2" />
                </svg>
                Inspector
              </a>
            )}
          </div>
        </div>
      </header>

      <main className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
        <div className="px-4 py-6 sm:px-0">
          {/* Session Info Card */}
          {session && (
            <div id="section-session-info">
            <SessionInfoCard
              session={session}
              enrollmentDuration={isWhiteGloveSession && whiteGloveDurations.combinedDuration ? whiteGloveDurations.combinedDuration : enrollmentDurationFromEvents}
              displayStatus={displayStatus}
              isGatherRulesSession={isGatherRulesSession}
              ntpOffset={ntpOffset}
              configMgrDetected={configMgrDetected}
            />
            </div>
          )}

          {/* Device Details Card (from enrollment tracker events) */}
          {!isGatherRulesSession && <div id="section-device-details"><DeviceDetailsCard events={events} latestAgentVersion={latestAgentVersion} /></div>}

          {/* Phase Timeline */}
          {!isGatherRulesSession && session && (
            <div id="section-enrollment-progress" className="bg-white shadow rounded-lg p-6 mb-6">
              <button
                onClick={() => setPhaseTimelineExpanded(!phaseTimelineExpanded)}
                className="flex items-center justify-between w-full text-left"
              >
                <h2 className="text-xl font-semibold text-gray-900">Enrollment Progress</h2>
                <svg className={`w-5 h-5 text-gray-400 transition-transform duration-200 ${phaseTimelineExpanded ? 'rotate-90' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
                </svg>
              </button>
              {phaseTimelineExpanded && (
                <PhaseTimeline
                  currentPhase={session.currentPhase}
                  completedPhases={session.status === 'Succeeded' ? [7] : []}
                  events={events}
                  sessionStatus={session.status}
                  enrollmentType={session.enrollmentType}
                  isPreProvisioned={isWhiteGloveSession}
                  isSkipUserStatusPage={isSkipUserStatusPage}
                  onPhaseClick={scrollToPhase}
                />
              )}
            </div>
          )}

          {/* Analysis Results */}
          {!isGatherRulesSession && (
            <div id="section-analysis">
            <AnalysisResultsSection
              analysisResults={analysisResults}
              loadingAnalysis={loadingAnalysis}
              analysisExpanded={analysisExpanded}
              setAnalysisExpanded={setAnalysisExpanded}
              onReanalyze={() => { trackEvent("analyze_now_clicked", { sessionId: sessionId ?? "" }); fetchAnalysisResults(true); }}
              canReanalyze={!isReadOnlyView}
              persistFailureRuleIds={persistFailureRuleIds}
            />
            </div>
          )}

          {/* Vulnerability Report — only when analyzer is enabled for this tenant */}
          {!isGatherRulesSession && enableSoftwareInventoryAnalyzer && (
            <div id="section-vulnerability-report">
              <VulnerabilityReportSection
                events={events}
                vulnerabilityReport={vulnerabilityReport}
                expanded={vulnerabilityReportExpanded}
                setExpanded={setVulnerabilityReportExpanded}
                onRescan={isReadOnlyView ? undefined : () => fetchVulnerabilityReport(true)}
              />
            </div>
          )}

          {/* Integrity Bypass — only when analyzer is enabled for this tenant and the session has produced at least one event */}
          {!isGatherRulesSession && enableIntegrityBypassAnalyzer && (
            <div id="section-integrity-bypass">
              <IntegrityBypassSection
                events={events}
                expanded={integrityBypassExpanded}
                setExpanded={setIntegrityBypassExpanded}
              />
            </div>
          )}

          {/* Performance Metrics (from performance_snapshot events) */}
          {!isGatherRulesSession && (
            <div id="section-performance">
            <PerformanceChart
              events={events.filter(e => e.eventType === "performance_snapshot")}
              expanded={perfExpanded}
              setExpanded={setPerfExpanded}
            />
            </div>
          )}

          {/* Script Executions (from script_started, script_completed, script_failed events) */}
          {!isGatherRulesSession && (
            <div id="section-scripts">
            <ScriptExecutions
              events={events.filter(
                e => e.eventType === "script_started"
                  || e.eventType === "script_completed"
                  || e.eventType === "script_failed"
              )}
              showScriptOutput={showScriptOutput}
              latestBootstrapVersion={latestBootstrapVersion}
              displayNamesByRefKey={scriptDisplayNamesByRefKey}
            />
            </div>
          )}

          {/* Download Progress (from download_progress, app_download_started, app_install_skipped events) */}
          {!isGatherRulesSession && (
            <div id="section-downloads">
            <DownloadProgress
              events={events.filter(
                e => e.eventType === "download_progress" || e.eventType === "app_download_started" || e.eventType === "app_install_skipped"
              )}
              summaryStats={appSummaryStats}
            />
            </div>
          )}

          {/* Install Progress (from app_install_* events, plus the office_install_* lifecycle — Office
              C2R is not an IME app but is rendered here as a first-class install with a live timer +
              duration via its started/completed/failed events — and the realmjoin_package_* lifecycle,
              rendered with an "RJ: " name prefix to keep mixed Intune/RealmJoin installs apart). */}
          {!isGatherRulesSession && (
            <div id="section-install-progress">
            <InstallProgress
              events={events.filter(
                e => e.eventType === "app_install_started" || e.eventType === "app_install_completed" || e.eventType === "app_install_failed" || e.eventType === "app_install_postponed" || e.eventType === "app_install_skipped"
                  || e.eventType === "office_install_started" || e.eventType === "office_install_completed" || e.eventType === "office_install_failed" || e.eventType === "office_preinstalled_detected"
                  || e.eventType === "realmjoin_package_started" || e.eventType === "realmjoin_package_completed"
              )}
              summaryStats={appSummaryStats}
            />
            </div>
          )}

          {/* Event Timeline (with severity filters, expand/collapse, WhiteGlove split) */}
          <div id="section-event-timeline">
          {eventsApi.isStreamingMore && (
            <div
              className="flex items-center gap-2 mb-2 px-3 py-1.5 rounded-md bg-blue-50 dark:bg-blue-900/20 text-blue-700 dark:text-blue-300 text-xs"
              role="status"
              aria-live="polite"
            >
              <svg className="animate-spin w-3.5 h-3.5" fill="none" viewBox="0 0 24 24">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
              </svg>
              <span>Loading more events…</span>
            </div>
          )}
          <EventTimeline
            filteredEvents={filteredEvents}
            events={events}
            session={session}
            severityFilters={severityFilters}
            toggleSeverityFilter={toggleSeverityFilter}
            expandedPhases={expandedPhases}
            togglePhase={togglePhase}
            timelineExpanded={timelineExpanded}
            setTimelineExpanded={setTimelineExpanded}
            expandAll={expandAll}
            collapseAll={collapseAll}
            isWhiteGloveSession={isWhiteGloveSession}
            whiteGloveSplitSequence={whiteGloveSplitSequence}
            orderedPhases={orderedPhases}
            eventsByPhase={eventsByPhase}
            preProvGrouped={preProvGrouped}
            userEnrollGrouped={userEnrollGrouped}
            userEnrollEvents={userEnrollEvents}
            preProvDuration={whiteGloveDurations.preProvDuration}
            userEnrollDuration={whiteGloveDurations.userEnrollDuration}
            showScriptOutput={showScriptOutput}
            autoScroll={autoScroll}
            onAutoScrollToggle={handleAutoScrollToggle}
          />
          </div>
        </div>

        {/* Admin Override Confirmation Modals */}
        <AdminOverrideModal
          show={showMarkFailedConfirm}
          action="failed"
          session={session}
          onConfirm={confirmMarkFailed}
          onCancel={cancelMarkFailed}
        />
        <AdminOverrideModal
          show={showMarkSucceededConfirm}
          action="succeeded"
          session={session}
          onConfirm={confirmMarkSucceeded}
          onCancel={cancelMarkSucceeded}
        />

        {/* Report Session Modal */}
        <ReportSessionModal
          show={showReportModal}
          session={session}
          events={events}
          analysisResults={analysisResults}
          onSubmit={handleSubmitReport}
          onCancel={() => setShowReportModal(false)}
          submitting={reportSubmitting}
        />

      </main>
    </div>
  </ProtectedRoute>
  );
}
