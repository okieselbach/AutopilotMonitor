"use client";

import Link from "next/link";
import { useCopy } from "@/hooks/useCopy";
import { trackEvent } from "@/lib/appInsights";
import { buildInvestigationPrompt } from "@/lib/investigationPrompt";
import { route } from "@/lib/routes";

interface InvestigateWithAiHintProps {
  sessionId: string;
  /** The session's tenant when it is not the viewer's own (Global Admin / MSP drill-in). */
  tenantId?: string;
  /** auth/me hasMcpAccess: the platform policy and the tenant's MCP switch allow this person in. */
  mcpAccess: boolean;
  /** Tenant Admin or platform role — their MCP catalog lists test_analyze_rule. */
  canDryRun: boolean;
  /** Report Session is an Admin / Operator action; Viewers are not pointed at a button they lack. */
  canReport: boolean;
  /** Where the hint sits, for the copy telemetry. */
  source: "analysis" | "diagnosis";
}

const CODE = "font-mono text-xs bg-white border border-gray-200 px-1.5 py-0.5 rounded break-all";
const COPY_LINK = "text-xs font-medium text-sky-700 hover:text-sky-900 dark:text-sky-300 dark:hover:text-sky-200 underline";

/**
 * The empty state of a failed session that no analyze rule explains — instead of a dead end, the way
 * to the evidence: the built-in debug-session MCP prompt, or the same investigation as a full prompt
 * for clients without MCP prompts, and Report Session for what the product should learn.
 */
export function InvestigateWithAiHint({ sessionId, tenantId, mcpAccess, canDryRun, canReport, source }: InvestigateWithAiHintProps) {
  const { copied, copy } = useCopy();
  const onCopy = (value: string, kind: "session_id" | "prompt") => {
    trackEvent("session_ai_prompt_copied", { source, kind });
    void copy(value, kind);
  };

  return (
    <div className="rounded-lg border border-gray-200 bg-gray-50 p-4 text-left">
      <p className="text-sm font-semibold text-gray-900">No analyze rule flagged this failure.</p>
      {mcpAccess ? (
        <>
          <p className="mt-1 text-sm text-gray-600">The evidence is still here. Investigate it with your AI assistant:</p>
          <ul className="mt-3 space-y-2 text-sm text-gray-700">
            <li className="flex flex-wrap items-center gap-x-2 gap-y-1">
              <span>
                Clients with MCP prompts: run <code className={CODE}>debug-session</code> for session
              </span>
              <code className={CODE}>{sessionId}</code>
              <button type="button" onClick={() => onCopy(sessionId, "session_id")} className={COPY_LINK}>
                {copied === "session_id" ? "Copied" : "Copy ID"}
              </button>
            </li>
            <li className="flex flex-wrap items-center gap-x-2 gap-y-1">
              <span>Any other client:</span>
              <button
                type="button"
                onClick={() => onCopy(buildInvestigationPrompt({ sessionId, tenantId, canDryRun }), "prompt")}
                className="px-3 py-1.5 text-xs font-medium bg-white border border-sky-300 text-sky-700 dark:text-sky-300 rounded-lg hover:bg-sky-50"
              >
                {copied === "prompt" ? "Copied" : "Copy full prompt"}
              </button>
            </li>
          </ul>
          <p className="mt-3 text-xs text-gray-500">
            {"Not connected yet? "}
            <Link href={route("/settings/tenant/ai-integration")} className="underline hover:no-underline">
              Set up your AI assistant
            </Link>
            .
          </p>
        </>
      ) : (
        <p className="mt-1 text-sm text-gray-600">Check the event timeline for the first error and what led up to it.</p>
      )}
      {canReport && (
        <p className="mt-2 text-xs text-gray-500">
          Something the product should detect on its own? Send it to the maintainers with Report Session.
        </p>
      )}
    </div>
  );
}
