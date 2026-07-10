"use client";

import { useState, useEffect } from "react";
import { RuleResult } from "@/types";
import { formatInlineMarkdown } from "@/lib/formatInlineMarkdown";
import { interpolateRuleTemplate } from "@/lib/interpolateRuleTemplate";
import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";
import { useAuth } from "@/contexts/AuthContext";
import { useTenant } from "@/contexts/TenantContext";

interface AnalysisResultsSectionProps {
  analysisResults: RuleResult[];
  loadingAnalysis: boolean;
  analysisExpanded: boolean;
  setAnalysisExpanded: (expanded: boolean) => void;
  onReanalyze: () => void;
  /**
   * Rule IDs whose results failed to persist during the most recent reanalyze.
   * Empty (or omitted) when everything stored cleanly. When non-empty the section
   * renders a yellow warning banner — the rules that DID persist still render
   * below as normal cards. Backend log line "On-demand re-analysis ... failed to
   * persist" carries the underlying Storage error for diagnosis.
   */
  persistFailureRuleIds?: string[];
  /** Hides the "Analyze Now" control when false — a read-only cross-tenant (delegated/MSP) viewer must not
   * trigger a re-analysis. Defaults to true, so own-tenant / Global Admin behavior is unchanged. */
  canReanalyze?: boolean;
}

export default function AnalysisResultsSection({
  analysisResults,
  loadingAnalysis,
  analysisExpanded,
  setAnalysisExpanded,
  onReanalyze,
  persistFailureRuleIds,
  canReanalyze = true,
}: AnalysisResultsSectionProps) {
  const { getAccessToken } = useAuth();
  const { tenantId } = useTenant();
  const [ruleHitRates, setRuleHitRates] = useState<Record<string, number>>({});

  useEffect(() => {
    if (!tenantId || analysisResults.length === 0) return;
    const fetchStats = async () => {
      try {
        const response = await authenticatedFetch(
          api.metrics.ruleStats(undefined, undefined, "analyze"),
          getAccessToken
        );
        if (response.ok) {
          const data = await response.json();
          const map: Record<string, number> = {};
          if (data.rules && Array.isArray(data.rules)) {
            for (const r of data.rules) {
              if (r.hitRate > 0) map[r.ruleId] = r.hitRate;
            }
          }
          setRuleHitRates(map);
        }
      } catch {
        // Non-critical
      }
    };
    fetchStats();
  }, [tenantId, analysisResults.length, getAccessToken]);

  return (
    <div className="bg-white shadow rounded-lg p-6 mb-6">
      <div
        onClick={() => setAnalysisExpanded(!analysisExpanded)}
        className="flex items-start justify-between gap-2 w-full text-left mb-4 cursor-pointer"
      >
        <div className="flex items-center flex-wrap gap-x-2 gap-y-1 min-w-0">
          <svg className="w-6 h-6 shrink-0 text-amber-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9.663 17h4.673M12 3v1m6.364 1.636l-.707.707M21 12h-1M4 12H3m3.343-5.657l-.707-.707m2.828 9.9a5 5 0 117.072 0l-.548.547A3.374 3.374 0 0014 18.469V19a2 2 0 11-4 0v-.531c0-.895-.356-1.754-.988-2.386l-.548-.547z" />
          </svg>
          <h2 className="text-xl font-semibold text-gray-900">Analysis Results</h2>
          {analysisResults.length > 0 && (
            <>
              <span className="text-xs text-gray-400">({analysisResults.length} {analysisResults.length === 1 ? 'issue' : 'issues'})</span>
              <div className="flex items-center flex-wrap gap-2 text-xs">
                {analysisResults.filter(r => r.severity === 'critical').length > 0 && (
                  <span className="px-2 py-0.5 rounded-full bg-red-100 text-red-700 font-medium">
                    {analysisResults.filter(r => r.severity === 'critical').length} Critical
                  </span>
                )}
                {analysisResults.filter(r => r.severity === 'high').length > 0 && (
                  <span className="px-2 py-0.5 rounded-full bg-orange-100 text-orange-700 font-medium">
                    {analysisResults.filter(r => r.severity === 'high').length} High
                  </span>
                )}
                {analysisResults.filter(r => r.severity === 'warning').length > 0 && (
                  <span className="px-2 py-0.5 rounded-full bg-yellow-100 text-yellow-700 font-medium">
                    {analysisResults.filter(r => r.severity === 'warning').length} Warning
                  </span>
                )}
              </div>
            </>
          )}
        </div>
        <div className="flex items-center space-x-3 shrink-0">
          {canReanalyze && (
          <button
            onClick={(e) => { e.stopPropagation(); onReanalyze(); }}
            disabled={loadingAnalysis}
            title="Runs all analyze rules (single + correlation) against the current event data. Analysis also runs automatically when enrollment completes or fails."
            className="px-3 py-1.5 text-sm font-medium bg-amber-50 text-amber-700 hover:bg-amber-100 rounded-lg transition-colors disabled:opacity-50 disabled:cursor-not-allowed flex items-center space-x-1.5"
          >
            {loadingAnalysis ? (
              <>
                <svg className="w-4 h-4 animate-spin" fill="none" viewBox="0 0 24 24">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                  <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
                </svg>
                <span>Analyzing...</span>
              </>
            ) : (
              <>
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                </svg>
                <span>Analyze Now</span>
              </>
            )}
          </button>
          )}
          <svg className={`w-5 h-5 text-gray-400 transition-transform duration-200 ${analysisExpanded ? 'rotate-90' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
          </svg>
        </div>
      </div>

      {analysisExpanded && (
        <>
          {persistFailureRuleIds && persistFailureRuleIds.length > 0 && (
            <div
              role="alert"
              className="mb-3 rounded-md border border-yellow-300 bg-yellow-50 p-3 text-sm text-yellow-900"
            >
              <div className="font-medium">
                {persistFailureRuleIds.length === 1
                  ? "1 rule result could not be persisted"
                  : `${persistFailureRuleIds.length} rule results could not be persisted`}
              </div>
              <div className="mt-1 text-xs text-yellow-800">
                The rules below loaded normally, but the following did not save during the last
                re-analysis. Backend logs (severity Error) contain the underlying storage failure.
                A subsequent &quot;Analyze Now&quot; click retries them.
              </div>
              <div className="mt-1 font-mono text-xs">
                {persistFailureRuleIds.join(", ")}
              </div>
            </div>
          )}
          {loadingAnalysis && analysisResults.length === 0 ? (
            <div className="text-center py-4 text-gray-500">Running analysis...</div>
          ) : analysisResults.length === 0 ? (
            <div className="text-center py-4 text-gray-400 text-sm">
              No issues detected yet. Click &quot;Analyze Now&quot; to run analysis on the current events, or wait for enrollment to complete for automatic analysis.
            </div>
          ) : (
            <div className="space-y-3">
              {analysisResults.map((result) => (
                <AnalysisResultCard key={result.ruleId} result={result} hitRate={ruleHitRates[result.ruleId]} />
              ))}
            </div>
          )}
        </>
      )}
    </div>
  );
}

function AnalysisResultCard({ result, hitRate }: { result: RuleResult; hitRate?: number }) {
  const [expanded, setExpanded] = useState(false);

  const severityColors: Record<string, string> = {
    critical: "border-l-red-600 bg-red-50",
    high: "border-l-orange-500 bg-orange-50",
    warning: "border-l-yellow-500 bg-yellow-50",
    info: "border-l-blue-500 bg-blue-50",
  };

  const severityBadgeColors: Record<string, string> = {
    critical: "bg-red-100 text-red-800",
    high: "bg-orange-100 text-orange-800",
    warning: "bg-yellow-100 text-yellow-800",
    info: "bg-blue-100 text-blue-800",
  };

  const cardColor = severityColors[result.severity] || severityColors.info;
  const badgeColor = severityBadgeColors[result.severity] || severityBadgeColors.info;

  return (
    <div className={`border-l-4 rounded-lg p-4 ${cardColor}`}>
      <div className="flex items-start justify-between cursor-pointer" onClick={() => setExpanded(!expanded)}>
        <div className="flex-1">
          <div className="flex items-center space-x-2 mb-1">
            <span className={`px-2 py-0.5 rounded text-xs font-medium ${badgeColor}`}>
              {result.severity.toUpperCase()}
            </span>
            <span className="text-xs font-mono text-gray-500">{result.ruleId}</span>
            <span className="text-xs px-2 py-0.5 rounded-full bg-gray-200 text-gray-600">{result.category}</span>
          </div>
          <h3 className="font-medium text-gray-900">{result.ruleTitle}</h3>
          <div className="flex items-center mt-1 space-x-3">
            <div className="flex items-center space-x-1">
              <span className="text-xs text-gray-500">Confidence:</span>
              <div className="w-24 h-2 bg-gray-200 rounded-full overflow-hidden">
                <div
                  className={`h-full rounded-full ${
                    result.confidenceScore >= 80 ? 'bg-red-500' :
                    result.confidenceScore >= 60 ? 'bg-orange-500' :
                    result.confidenceScore >= 40 ? 'bg-yellow-500' : 'bg-blue-500'
                  }`}
                  style={{ width: `${result.confidenceScore}%` }}
                />
              </div>
              <span className="text-xs font-medium text-gray-700">{result.confidenceScore}%</span>
            </div>
            {hitRate != null && hitRate > 0 && (
              <span className="text-xs text-gray-500 italic" title="Based on rule telemetry from the last 30 days">
                Fires on {hitRate}% of enrollments in your tenant
              </span>
            )}
          </div>
        </div>
        <svg className={`w-5 h-5 text-gray-400 ml-2 transition-transform duration-200 flex-shrink-0 ${expanded ? 'rotate-90' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
        </svg>
      </div>

      {expanded && (
        <div className="mt-4 space-y-3">
          {result.explanation && (
            <div>
              <h4 className="text-sm font-medium text-gray-700 mb-1">Explanation</h4>
              <p className="text-sm text-gray-600">
                {formatInlineMarkdown(interpolateRuleTemplate(result.explanation, result.matchedConditions))}
              </p>
            </div>
          )}

          {result.remediation && result.remediation.length > 0 && (
            <div>
              <h4 className="text-sm font-medium text-gray-700 mb-1">Remediation</h4>
              {result.remediation.map((rem, i) => (
                <div key={i} className="mb-2">
                  <p className="text-sm font-medium text-gray-600">
                    {interpolateRuleTemplate(rem.title, result.matchedConditions)}
                  </p>
                  <ul className="list-disc list-inside text-sm text-gray-600 ml-2">
                    {rem.steps.map((step, j) => (
                      <li key={j}>{interpolateRuleTemplate(step, result.matchedConditions)}</li>
                    ))}
                  </ul>
                </div>
              ))}
            </div>
          )}

          {result.relatedDocs && result.relatedDocs.length > 0 && (
            <div>
              <h4 className="text-sm font-medium text-gray-700 mb-1">Related Documentation</h4>
              <div className="flex flex-wrap gap-2">
                {result.relatedDocs.map((doc, i) => (
                  <a
                    key={i}
                    href={doc.url}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="text-sm text-blue-600 hover:text-blue-800 underline"
                  >
                    {doc.title}
                  </a>
                ))}
              </div>
            </div>
          )}

          {result.matchedConditions && Object.keys(result.matchedConditions).length > 0 && (
            <div>
              <h4 className="text-sm font-medium text-gray-700 mb-1">Evidence</h4>
              <EvidenceEventLinks matchedConditions={result.matchedConditions} />
              <div className="p-2 bg-gray-900 rounded text-xs text-gray-100 font-mono overflow-x-auto">
                <pre>{JSON.stringify(result.matchedConditions, null, 2)}</pre>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function scrollToEvent(eventId: string) {
  const el = document.getElementById(`event-${eventId}`);
  if (el) {
    el.scrollIntoView({ behavior: "smooth", block: "center" });
    el.classList.add("ring-2", "ring-amber-400", "ring-offset-1");
    setTimeout(() => el.classList.remove("ring-2", "ring-amber-400", "ring-offset-1"), 3000);
  }
}

function EvidenceEventLinks({ matchedConditions }: { matchedConditions: Record<string, any> }) {
  const eventLinks: { signal: string; eventId: string; eventType?: string }[] = [];
  const seenEventIds = new Set<string>();

  for (const [signal, evidence] of Object.entries(matchedConditions)) {
    if (signal.startsWith("factor_")) continue;
    if (evidence && typeof evidence === "object" && evidence.eventId) {
      // Multiple matched conditions can extract different fields from the
      // same event — one chip per distinct event, not per condition.
      if (seenEventIds.has(evidence.eventId)) continue;
      seenEventIds.add(evidence.eventId);
      eventLinks.push({ signal, eventId: evidence.eventId, eventType: evidence.eventType });
    }
  }

  if (eventLinks.length === 0) return null;

  return (
    <div className="flex flex-wrap gap-2 mb-2">
      {eventLinks.map(({ signal, eventId, eventType }) => (
        <button
          key={signal}
          onClick={() => scrollToEvent(eventId)}
          className="inline-flex items-center space-x-1 px-2 py-1 text-xs font-medium text-amber-700 bg-amber-50 hover:bg-amber-100 border border-amber-200 rounded transition-colors"
          title={`Jump to event ${eventId}`}
        >
          <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 7l5 5m0 0l-5 5m5-5H6" />
          </svg>
          <span>{eventType || signal}</span>
        </button>
      ))}
    </div>
  );
}
