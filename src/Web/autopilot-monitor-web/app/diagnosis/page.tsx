"use client";

import { Suspense, useEffect, useMemo, useRef, useState } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { ProtectedRoute } from "@/components/ProtectedRoute";
import { SessionStatusBadge } from "@/components/SessionStatusBadge";
import { DocsLink } from "@/components/DocsLink";
import { InvestigateWithAiHint } from "@/components/InvestigateWithAiHint";
import { CardSkeleton } from "@/components/skeletons/PageSkeleton";
import { useSignalR } from "@/contexts/SignalRContext";
import { useTenant } from "@/contexts/TenantContext";
import { useAuth } from "@/contexts/AuthContext";
import { useNotifications } from "@/contexts/NotificationContext";
import { useAdminMode } from "@/hooks/useAdminMode";
import { useCopy } from "@/hooks/useCopy";
import { trackEvent } from "@/lib/appInsights";
import { DOCS_PATHS } from "@/lib/docsPaths";
import { asGuidOrUndefined, isGuid } from "@/lib/inputValidation";
import { isTerminalStatus } from "@/lib/sessionStatus";
import { dashboardUrl, diagnosisUrl, sessionUrl } from "@/lib/routes";
import { hasTenantReadScope } from "@/lib/tenantScope";
import { useSessionDetail } from "../sessions/hooks/useSessionDetail";
import { useSessionAnalysis } from "../sessions/hooks/useSessionAnalysis";
import { useSessionEvents } from "../sessions/hooks/useSessionEvents";
import { useSessionSignalR } from "../sessions/hooks/useSessionSignalR";
import { useSessionTimeAttribution } from "../sessions/hooks/useSessionTimeAttribution";
import { useRuleFireStats } from "../sessions/hooks/useRuleFireStats";
import { useSessionDiagnosis } from "./hooks/useSessionDiagnosis";
import { useAppFleetContext } from "./hooks/useAppFleetContext";
import VerdictCard from "./components/VerdictCard";
import ContextSection from "./components/ContextSection";
import StepsSection from "./components/StepsSection";
import TimeSection from "./components/TimeSection";
import FindingsSection from "./components/FindingsSection";
import NotesSection from "./components/NotesSection";
import ProblemsSection from "./components/ProblemsSection";
import { verdictText } from "./lib/verdictText";
import { groupFindings } from "./lib/findingGroups";
import { contextItems } from "./lib/contextItems";
import { copySummary, stepsFrom, stepsPlain } from "./lib/steps";
import { timeLanes } from "./lib/timeLanes";
import { keyEvents, problemEvents } from "./lib/eventLists";
import { fleetSessionsHref, fleetView } from "./lib/fleetContext";

/** How long after the end a session without findings may still be in its queued analysis. */
const ANALYSIS_GRACE_MS = 5 * 60_000;

export default function DiagnosisPage() {
  // useSearchParams() requires a Suspense boundary for the static prerender.
  return (
    <Suspense fallback={null}>
      <DiagnosisRoute />
    </Suspense>
  );
}

/**
 * Identity from the query (`?id=&tenantId=`). The content is keyed by the session: the session hooks
 * keep their state across an id change, and the verdict links to other sessions (superseded).
 */
function DiagnosisRoute() {
  const searchParams = useSearchParams();
  // Percent-decoded deep-link values: only GUIDs may reach the API builders.
  const raw = searchParams.get("id") ?? "";
  const sessionId = isGuid(raw) ? raw.trim() : "";
  const tenantIdOverride = asGuidOrUndefined(searchParams.get("tenantId"));
  return <DiagnosisContent key={sessionId} requestedSessionId={sessionId} tenantIdOverride={tenantIdOverride} />;
}

function DiagnosisContent({ requestedSessionId, tenantIdOverride }: { requestedSessionId: string; tenantIdOverride?: string }) {
  const { getAccessToken, user, hasGlobalScope } = useAuth();
  // A member without a tenant role sees ProtectedRoute's notice; the empty id keeps the hooks inert.
  const sessionId = !user || hasTenantReadScope(user) ? requestedSessionId : "";
  const { globalAdminMode } = useAdminMode();
  const { on, off, isConnected, joinGroup, leaveGroup } = useSignalR();
  const { tenantId } = useTenant();
  const { addNotification } = useNotifications();
  const { copied, copy } = useCopy();

  const detail = useSessionDetail({ sessionId, tenantId, globalAdminMode, tenantIdOverride, user, getAccessToken, addNotification });
  const analysis = useSessionAnalysis(sessionId, detail.sessionTenantId, getAccessToken, { withVulnerabilityReport: false });
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
  const diagnosis = useSessionDiagnosis(sessionId, detail.sessionTenantId, getAccessToken);
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
    fetchSessionDetails: detail.fetchSessionDetails,
    fetchAnalysisResults: analysis.fetchAnalysisResults,
    fetchVulnerabilityReport: analysis.fetchVulnerabilityReport,
    onActivity: diagnosis.scheduleRefetch,
  });

  const session = detail.session;
  const sessionTenant = detail.sessionTenantId || tenantIdOverride;
  const breakdown = useSessionTimeAttribution(sessionId, session?.status, sessionTenant, getAccessToken);
  const headline = diagnosis.headline;
  const culprit = headline?.culprits?.[0];
  const fleetContext = useAppFleetContext(culprit, sessionId, sessionTenant, getAccessToken);

  // Links keep the tenant of a cross-tenant view (Global Admin, MSP drill-in).
  const viewedTenantId = (session?.tenantId ?? detail.sessionTenantId ?? tenantIdOverride ?? "").toLowerCase();
  const crossTenant = !!viewedTenantId && !!tenantId && viewedTenantId !== tenantId.toLowerCase();
  const linkTenant = crossTenant ? viewedTenantId : undefined;
  const eventHref = (eventId: string) => sessionUrl(sessionId, { tenantId: linkTenant, hash: `event-${eventId}` });

  const results = analysis.analysisResults;
  const fireStats = useRuleFireStats(sessionTenant || undefined, results.length);
  const ruleFires = {
    counts: fireStats.recentFires,
    href: (ruleId: string) => dashboardUrl({ ruleId, tenant: fireStats.crossTenant ? sessionTenant : undefined }),
  };
  const fleet =
    culprit?.kind === "app"
      ? fleetView(
          fleetContext,
          fleetSessionsHref({
            appName: culprit.name,
            windowDays: fleetContext?.windowDays ?? 7,
            viewedTenantId,
            ownTenantId: tenantId,
            globalAdminMode: globalAdminMode && hasGlobalScope,
            delegatedScope: !!user?.isDelegated && !hasGlobalScope,
          })
        )
      : null;
  const groups = useMemo(() => groupFindings(results, headline?.outcome ?? "unknown", headline?.culprits), [results, headline]);
  const verdict = useMemo(() => (headline ? verdictText(headline) : null), [headline]);
  const steps = useMemo(() => stepsFrom(groups), [groups]);
  const context = useMemo(() => contextItems(groups.context, headline?.caveats), [groups, headline]);
  const lanes = useMemo(
    () =>
      timeLanes(breakdown, {
        failedCategory: headline?.failedStep?.category,
        failure: headline?.failedStep ? { at: headline.failedStep.at, label: headline.failedStep.category ? "ESP failed" : "Failed" } : null,
        culprits: headline?.culprits,
      }),
    [breakdown, headline]
  );
  const key = useMemo(() => keyEvents(eventsApi.events, headline?.culprits), [eventsApi.events, headline]);
  const problems = useMemo(() => problemEvents(eventsApi.events), [eventsApi.events]);

  // The end-of-enrollment analysis runs from a queue, and a run without findings pushes nothing:
  // for a few minutes after the end "no findings" may still mean "not analyzed yet".
  const completedAt = session?.completedAt;
  const [analysisMayRun, setAnalysisMayRun] = useState(false);
  useEffect(() => {
    if (!completedAt) return;
    const left = Date.parse(completedAt) + ANALYSIS_GRACE_MS - Date.now();
    const start = setTimeout(() => setAnalysisMayRun(left > 0), 0);
    const end = left > 0 ? setTimeout(() => setAnalysisMayRun(false), left) : undefined;
    return () => {
      clearTimeout(start);
      if (end) clearTimeout(end);
    };
  }, [completedAt]);

  const reported = useRef(false);
  useEffect(() => {
    if (!headline || reported.current) return;
    reported.current = true;
    trackEvent("diagnosis_opened", { status: session?.status ?? "", outcome: headline.outcome, cause: headline.cause });
  }, [headline, session?.status]);

  useEffect(() => {
    const label = session?.deviceName || session?.serialNumber;
    document.title = label ? `Diagnosis · ${label} | Autopilot Monitor` : "Diagnosis | Autopilot Monitor";
    return () => {
      document.title = "Autopilot Monitor";
    };
  }, [session?.deviceName, session?.serialNumber]);

  const openCount = groups.outcome.length + groups.primary.items.length + groups.context.length + groups.other.length;
  const analysisNote = !analysis.analysisLoaded
    ? "Loading findings…"
    : openCount > 0
      ? null
      : !isTerminalStatus(session?.status)
        ? "Findings appear here while the enrollment runs."
        : analysisMayRun
          ? "The analysis is still running; findings appear here when it finishes."
          : "No analyze rule fired for this session.";

  const timelineEventId = headline?.failedStep?.eventId ?? headline?.culprits?.find((c) => c.eventId)?.eventId;
  const device = session?.deviceName || session?.serialNumber || null;

  const onCopySummary = () => {
    if (!headline || !verdict) return;
    trackEvent("diagnosis_action", { action: "copy_summary" });
    void copy(copySummary({ verdict, headline, device, steps, url: window.location.href }), "summary");
  };
  const onCopySteps = () => {
    if (!steps) return;
    trackEvent("diagnosis_action", { action: "copy_steps" });
    void copy(stepsPlain(steps), "steps");
  };

  return (
    <ProtectedRoute requireTenantReadScope>
      <div className="min-h-screen bg-gray-50">
        <header className="bg-white shadow">
          <div className="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-x-4 gap-y-3 px-4 py-6 sm:px-6 lg:px-8">
            <div className="min-w-0">
              {sessionId && (
                <Link href={sessionUrl(sessionId, { tenantId: linkTenant })} className="text-sm text-gray-600 hover:text-gray-900">
                  ← Session details
                </Link>
              )}
              <h1 className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-2xl font-normal text-gray-900">
                Diagnosis
                {session && (
                  <SessionStatusBadge
                    status={session.status}
                    failureReason={session.failureReason}
                    adminMarkedAction={session.adminMarkedAction}
                    reconcileReason={session.reconcileReason}
                    espSoftFailure={session.espSoftFailure}
                  />
                )}
              </h1>
              {session && (
                <p className="mt-1 break-words text-sm text-gray-500">
                  {[device, [session.manufacturer, session.model].filter(Boolean).join(" ")].filter(Boolean).join(" · ")}
                </p>
              )}
            </div>
            <DocsLink path={DOCS_PATHS.guidedDiagnosis} label="Docs" />
          </div>
        </header>

        <main className="mx-auto max-w-5xl space-y-6 px-4 py-6 sm:px-6 lg:px-8">
          {!requestedSessionId ? (
            <p className="rounded-lg bg-white p-6 text-sm text-gray-600 shadow">This link does not name a valid session.</p>
          ) : (
            <>
              {diagnosis.state === "loading" && (
                <div aria-busy="true">
                  <CardSkeleton />
                </div>
              )}
              {diagnosis.state === "error" && (
                <div role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-white p-6 text-sm text-gray-700 shadow">
                  The verdict could not be loaded.
                  <button
                    type="button"
                    onClick={() => void diagnosis.refetch()}
                    className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50"
                  >
                    Try again
                  </button>
                </div>
              )}
              {headline && verdict && (
                <VerdictCard
                  headline={headline}
                  verdict={verdict}
                  basedOn={openCount}
                  timelineHref={timelineEventId ? eventHref(timelineEventId) : null}
                  supersededHref={headline.supersededBySessionId ? diagnosisUrl(headline.supersededBySessionId, { tenantId: linkTenant }) : null}
                  onCopySummary={onCopySummary}
                  copied={copied === "summary"}
                />
              )}

              <ContextSection items={context} outcome={headline?.outcome ?? ""} fleet={fleet} />
              {steps && <StepsSection steps={steps} onCopy={onCopySteps} copied={copied === "steps"} />}
              <TimeSection
                lanes={lanes}
                keyEvents={key.items}
                keyEventTotal={key.total}
                eventHref={eventHref}
                timelineHref={sessionUrl(sessionId, { tenantId: linkTenant })}
              />
              <FindingsSection groups={groups} analysisNote={analysisNote} eventHref={eventHref} ruleFires={ruleFires} />

              {session?.status === "Failed" && analysis.analysisLoaded && results.length === 0 && !analysisMayRun && (
                <InvestigateWithAiHint
                  sessionId={sessionId}
                  tenantId={linkTenant}
                  mcpAccess={user?.hasMcpAccess ?? false}
                  canDryRun={(user?.isGlobalAdmin ?? false) || (user?.isGlobalReader ?? false) || ((user?.isTenantAdmin ?? false) && !crossTenant)}
                  canReport={!crossTenant && ((user?.isTenantAdmin ?? false) || user?.role === "Operator" || (user?.isGlobalAdmin ?? false))}
                  source="diagnosis"
                />
              )}

              {sessionId && <NotesSection sessionId={sessionId} tenantId={sessionTenant} user={user} getAccessToken={getAccessToken} />}
              <ProblemsSection events={problems} eventHref={eventHref} />
            </>
          )}
        </main>
      </div>
    </ProtectedRoute>
  );
}
