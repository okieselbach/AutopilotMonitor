"use client";

import { useCopy } from "@/hooks/useCopy";
import { MCP_SERVER_URL } from "@/utils/config";
import { CopyIcon } from "./icons";

export function CopyServerUrl() {
  const { copied, copy } = useCopy();
  return (
    <div className="mt-3 flex items-center gap-2.5 rounded-xl border border-[var(--lp-line)] bg-[var(--lp-surface)] py-1.5 pl-3.5 pr-1.5">
      <code className="min-w-0 flex-1 overflow-x-auto whitespace-nowrap font-mono text-[13px] text-[var(--lp-ink)]">{MCP_SERVER_URL}</code>
      <button
        type="button"
        data-track="copy_mcp_url"
        onClick={() => void copy(MCP_SERVER_URL, "mcp-url")}
        className="inline-flex shrink-0 items-center gap-1.5 rounded-lg border border-[var(--lp-line)] bg-[var(--lp-surface)] px-2.5 py-2 text-xs font-semibold text-[var(--lp-ink-soft)] transition-colors hover:border-[var(--lp-ink-faint)] hover:text-[var(--lp-ink)]"
      >
        <CopyIcon className="h-3.5 w-3.5" />
        {copied === "mcp-url" ? "Copied" : "Copy"}
      </button>
    </div>
  );
}
