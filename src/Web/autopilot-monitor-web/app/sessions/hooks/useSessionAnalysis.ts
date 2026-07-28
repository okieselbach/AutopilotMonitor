"use client";
import { useState, useCallback, useEffect } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";
import { RuleResult } from "@/types";
import { isGuid } from "@/utils/inputValidation";

export function useSessionAnalysis(
  sessionId: string,
  sessionTenantId: string | null,
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>
) {
  const [analysisResults, setAnalysisResults] = useState<RuleResult[]>([]);
  const [loadingAnalysis, setLoadingAnalysis] = useState(false);
  const [vulnerabilityReport, setVulnerabilityReport] = useState<unknown>(null);
  // Rule IDs whose results failed to persist during the most recent reanalyze.
  // Backend reports them in /sessions/{id}/analysis response so the UI can render
  // a warning banner without blocking the rules that DID persist.
  const [persistFailureRuleIds, setPersistFailureRuleIds] = useState<string[]>([]);

  const fetchAnalysisResults = useCallback(async (reanalyze = false) => {
    if (!sessionTenantId || !isGuid(sessionTenantId)) return;
    try {
      setLoadingAnalysis(true);

      const response = await authenticatedFetch(
        api.sessions.analysis(sessionId, sessionTenantId, reanalyze || undefined),
        getAccessToken
      );
      if (response.ok) {
        const data = await response.json();
        if (data.results) {
          setAnalysisResults(data.results.sort((a: RuleResult, b: RuleResult) => b.confidenceScore - a.confidenceScore));
        }
        // persistFailureRuleIds is null/undefined for the happy path and a string[] when one or
        // more StoreRuleResultAsync calls returned false during the reanalyze loop. We always
        // set it so a successful retry clears a previous warning.
        setPersistFailureRuleIds(Array.isArray(data.persistFailureRuleIds) ? data.persistFailureRuleIds : []);
      }
    } catch (error) {
      console.error("Failed to fetch analysis results:", error);
    } finally {
      setLoadingAnalysis(false);
    }
  }, [sessionId, sessionTenantId, getAccessToken]);

  const fetchVulnerabilityReport = useCallback(async (rescan = false) => {
    if (!sessionTenantId || !isGuid(sessionTenantId)) return;
    try {
      const response = await authenticatedFetch(
        api.sessions.vulnerabilityReport(sessionId, sessionTenantId, rescan || undefined),
        getAccessToken
      );
      if (response.ok) {
        const data = await response.json();
        setVulnerabilityReport(data.report ?? null);
      }
    } catch (error) {
      console.error("Failed to fetch vulnerability report:", error);
    }
  }, [sessionId, sessionTenantId, getAccessToken]);

  // Initial fetch on mount / when sessionTenantId resolves. Without these calls the
  // page-reload path shows empty placeholders ("No issues detected yet" / "No vulnerability
  // findings reported") for completed sessions whose results are already stored — the only
  // previous trigger paths were manual "Analyze Now" / "Re-Scan" clicks or live SignalR
  // pushes. fired in parallel via Promise.all so a slow vulnerability fetch does not
  // delay the analysis render.
  useEffect(() => {
    if (!sessionId || !sessionTenantId || !isGuid(sessionTenantId)) return;
    Promise.all([fetchAnalysisResults(), fetchVulnerabilityReport()]);
  }, [sessionId, sessionTenantId, fetchAnalysisResults, fetchVulnerabilityReport]);

  return {
    analysisResults,
    loadingAnalysis,
    vulnerabilityReport,
    setVulnerabilityReport,
    fetchAnalysisResults,
    fetchVulnerabilityReport,
    persistFailureRuleIds,
  };
}
