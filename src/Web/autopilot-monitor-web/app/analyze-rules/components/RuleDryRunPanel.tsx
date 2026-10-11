"use client";

import { useState } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { api } from "@/lib/api";
import { apiErrorText, fetchJson, jsonBody } from "@/lib/apiClient";
import { trackEvent } from "@/lib/appInsights";
import { formatInlineMarkdown } from "@/lib/formatInlineMarkdown";
import { isGuid } from "@/lib/inputValidation";
import { interpolateRuleTemplate } from "@/lib/interpolateRuleTemplate";
import type {
  AnalyzeRule as AnalyzeRuleWire,
  DryRunAnalyzeRuleRequest,
  DryRunAnalyzeRuleResponse,
  RuleDryRun,
} from "@/lib/generated/wire-types.generated";
import { confidenceSummary, evidenceText, eventCountText, verdictLabel, type DryRunDraft } from "../ruleDryRunLogic";

interface RuleDryRunPanelProps {
  /** Reads the draft at click time from whichever mode the editor is in (form or JSON). */
  getDraft: () => DryRunDraft;
  /** Keeps the input id unique when several editors are open (create form, edit form of a rule). */
  idPrefix: string;
}

interface Outcome {
  result: RuleDryRun;
  explanation: string;
}

/**
 * "Test against a session" in the analyze-rule editor: the backend's side-effect-free dry-run
 * (POST rules/analyze/dryrun, the route test_analyze_rule uses) for the draft as it stands. Shown where
 * rules can be edited, so to Tenant Admins and Global Admins — both may call the route.
 */
export function RuleDryRunPanel({ getDraft, idPrefix }: RuleDryRunPanelProps) {
  const { getAccessToken } = useAuth();
  const [sessionId, setSessionId] = useState("");
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<Outcome | null>(null);
  const inputId = `${idPrefix}-dryrun-session`;

  const run = async () => {
    const id = sessionId.trim();
    if (!isGuid(id)) {
      setError("Enter a session ID. Copy it from the Session Info card of the session you want to test against.");
      return;
    }
    const draft = getDraft();
    if ("error" in draft) {
      setError(draft.error);
      return;
    }
    setRunning(true);
    setError(null);
    try {
      const data = await fetchJson<DryRunAnalyzeRuleResponse>(api.rules.analyzeDryRun(), getAccessToken, {
        method: "POST",
        // The draft is the editor's loose rule object; the backend validates it and answers 400 with the reasons.
        body: jsonBody<DryRunAnalyzeRuleRequest>({ sessionId: id, rule: draft.rule as unknown as AnalyzeRuleWire }),
      });
      trackEvent("rule_dryrun_run", { verdict: data.result.verdict });
      const explanation = typeof draft.rule.explanation === "string"
        ? interpolateRuleTemplate(draft.rule.explanation, data.result.matchedConditions)
        : "";
      setOutcome({ result: data.result, explanation });
    } catch (e) {
      setOutcome(null);
      setError(apiErrorText(e, "The test could not run."));
    } finally {
      setRunning(false);
    }
  };

  return (
    <div className="mt-5 rounded-lg border border-gray-200 bg-gray-50 p-4">
      <div className="flex flex-wrap items-end gap-3">
        <label htmlFor={inputId} className="flex min-w-0 flex-1 flex-col gap-1 text-xs font-medium text-gray-700">
          Test against a session
          <input
            id={inputId}
            value={sessionId}
            onChange={(e) => setSessionId(e.target.value)}
            placeholder="Session ID"
            spellCheck={false}
            className="w-full max-w-md rounded-md border border-gray-300 bg-white px-3 py-1.5 font-mono text-xs text-gray-900"
          />
        </label>
        <button
          type="button"
          onClick={run}
          disabled={running}
          className="px-3 py-1.5 text-sm font-medium bg-sky-600 text-white rounded-lg hover:bg-sky-700 disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {running ? "Testing…" : "Run test"}
        </button>
      </div>
      <p className="mt-1.5 text-[11px] text-gray-500">
        Replays the draft as it would be saved against the session&apos;s stored events. Nothing is saved. Test a session where
        the rule should fire and one where it should stay silent.
      </p>
      {error && <p role="alert" className="mt-3 text-sm text-red-700">{error}</p>}
      {outcome && <DryRunResult outcome={outcome} />}
    </div>
  );
}

function DryRunResult({ outcome }: { outcome: Outcome }) {
  const { result, explanation } = outcome;
  const fired = result.verdict === "fired";
  return (
    <div className="mt-4">
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm">
        <span className={`px-2 py-0.5 rounded-full text-xs font-semibold ${fired ? "bg-sky-100 text-sky-800 dark:text-sky-200" : "bg-gray-100 text-gray-700"}`}>
          {verdictLabel(result.verdict)}
        </span>
        <span className="text-gray-700">Confidence {confidenceSummary(result)}</span>
        <span className="text-gray-400" aria-hidden="true">·</span>
        <span className="text-gray-500 tabular-nums">{result.eventCount} events</span>
        {fired && (
          <>
            <span className="text-gray-400" aria-hidden="true">·</span>
            <span className="text-gray-500">
              {result.wouldMarkSessionAsFailed ? "Would mark the session as failed" : "Does not mark the session as failed"}
            </span>
          </>
        )}
      </div>

      {result.preconditions.length > 0 && (
        <ul className="mt-3 space-y-1 text-xs text-gray-600">
          {result.preconditions.map((p, i) => (
            <li key={i}>
              Precondition <span className="font-mono">{[p.eventType, p.dataField, p.operator, p.value].filter(Boolean).join(" ")}</span>
              {p.passed ? " — passed" : " — failed, the rule was skipped"}
            </li>
          ))}
        </ul>
      )}

      <ul className="mt-3 divide-y divide-gray-200 rounded-md border border-gray-200 bg-white text-xs">
        {result.conditions.map((c) => {
          const evidence = evidenceText(c);
          return (
            <li key={c.signal} className="flex flex-wrap items-baseline gap-x-3 gap-y-1 px-3 py-2">
              <span className={`font-bold ${c.matched ? "text-green-700" : "text-gray-400"}`} aria-hidden="true">
                {c.matched ? "✓" : "✗"}
              </span>
              <span className="sr-only">{c.matched ? "Matched:" : "Not matched:"}</span>
              <span className="font-mono font-medium text-gray-900">{c.signal}</span>
              <span className="text-gray-500">
                {[c.eventType ?? c.source, c.required ? "required" : "optional", eventCountText(c)].filter(Boolean).join(" · ")}
              </span>
              {evidence && <span className="basis-full pl-5 text-gray-600 break-words">{evidence}</span>}
            </li>
          );
        })}
      </ul>

      {fired && explanation && (
        <div className="mt-3">
          <p className="text-xs font-medium text-gray-700">Explanation preview</p>
          <p className="mt-1 text-sm text-gray-700 leading-relaxed">{formatInlineMarkdown(explanation)}</p>
        </div>
      )}
    </div>
  );
}
