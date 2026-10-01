"use client";

import { useState, type ReactNode } from "react";
import { BrandMark } from "../BrandMark";
import {
  BuildingIcon,
  ChatIcon,
  ChipIcon,
  CloudIcon,
  CodeIcon,
  LaptopIcon,
  RepeatIcon,
  ServerIcon,
  TerminalIcon,
} from "./icons";
import { SEGMENT_BUTTON, SEGMENT_GROUP, SEGMENT_OFF, SEGMENT_ON } from "./segmented";

type Placement = "vendor" | "devtools" | "own";

const PLACEMENTS: {
  id: Placement;
  label: string;
  clients: string;
  processedBy: string;
  setup: string;
}[] = [
  {
    id: "vendor",
    label: "Your AI vendor",
    clients: "Claude on web, desktop and mobile. ChatGPT.",
    processedBy: "Your AI vendor, under the agreement your organization already has with them.",
    setup: "Add the server URL as a connector and sign in with Microsoft. Nothing to register.",
  },
  {
    id: "devtools",
    label: "Your developer tools",
    clients: "VS Code with GitHub Copilot. Claude Code, Codex and Gemini CLI.",
    processedBy: "The model behind your editor or terminal, under the policy that already covers your developer tools.",
    setup:
      "Add the server to the tool's MCP configuration and sign in. Terminal agents can also download the diagnostics package and search the logs on your machine.",
  },
  {
    id: "own",
    label: "Your own infrastructure",
    clients: "A chat front end your organization hosts, connected to a model you run. Or a scheduled report that runs without a person.",
    processedBy: "Your own systems. The model can run in your cloud subscription or on your own hardware.",
    setup:
      "A Tenant Admin registers the client's callback URL once under Settings → Tenant → AI Integration. Only accounts of your tenant can sign in. Automation uses an app registration instead.",
  },
];

interface Row {
  icon: ReactNode;
  label: string;
  sub: string;
  placements: Placement[];
}

const CLIENT_ROWS: Row[] = [
  { icon: <ChatIcon />, label: "Chat app", sub: "Claude, ChatGPT", placements: ["vendor"] },
  { icon: <CodeIcon />, label: "Code editor", sub: "VS Code with GitHub Copilot", placements: ["devtools"] },
  { icon: <TerminalIcon />, label: "Terminal agent", sub: "Claude Code, Codex, Gemini CLI", placements: ["devtools"] },
  { icon: <ServerIcon />, label: "Self-hosted front end", sub: "registered once by a Tenant Admin", placements: ["own"] },
  { icon: <RepeatIcon />, label: "Automation", sub: "its own app identity", placements: ["own"] },
];

const MODEL_ROWS: Row[] = [
  { icon: <CloudIcon />, label: "Your AI vendor's cloud", sub: "under your agreement with them", placements: ["vendor", "devtools"] },
  { icon: <BuildingIcon />, label: "Your cloud subscription", sub: "a model you deploy yourself", placements: ["own"] },
  { icon: <ServerIcon />, label: "Your own hardware", sub: "a local model on your servers", placements: ["own"] },
];

function Node({ tag, accent = false, children }: { tag: string; accent?: boolean; children: ReactNode }) {
  return (
    <div
      className={`relative rounded-2xl border px-4 pb-3.5 pt-5 ${
        accent ? "border-[var(--lp-accent-line)] bg-[var(--lp-accent-soft)]" : "border-dashed border-[var(--lp-line)] bg-[var(--lp-surface)]"
      }`}
    >
      <span
        className={`absolute -top-[9px] left-3.5 bg-[var(--lp-surface)] px-1.5 text-[10px] font-bold uppercase tracking-[0.16em] ${
          accent ? "text-[var(--lp-accent-ink)]" : "text-[var(--lp-ink-faint)]"
        }`}
      >
        {tag}
      </span>
      {children}
    </div>
  );
}

function NodeTitle({ icon, children }: { icon: ReactNode; children: ReactNode }) {
  return (
    <div className="flex items-center gap-2 text-[15px] font-bold text-[var(--lp-ink)]">
      {icon}
      {children}
    </div>
  );
}

function Rows({ rows, placement }: { rows: Row[]; placement: Placement }) {
  return (
    <div className="mt-2.5 grid gap-0.5">
      {rows.map(row => {
        const on = row.placements.includes(placement);
        return (
          <div
            key={row.label}
            className={`flex items-start gap-2.5 rounded-[10px] px-2 py-[7px] text-[13px] leading-[1.35] transition-colors ${
              on
                ? "bg-[var(--lp-surface-2)] font-semibold text-[var(--lp-ink)] shadow-[inset_0_0_0_1px_var(--lp-accent-line)]"
                : "text-[var(--lp-ink-soft)]"
            }`}
          >
            <span className={`mt-px shrink-0 ${on ? "text-[var(--lp-accent-ink)]" : "text-[var(--lp-ink-faint)]"}`}>{row.icon}</span>
            <span>
              {row.label}
              <small className="block text-[11.5px] font-normal text-[var(--lp-ink-faint)]">{row.sub}</small>
            </span>
          </div>
        );
      })}
    </div>
  );
}

/** One link of the diagram; talk = the conversation, which never passes Autopilot Monitor. */
function FlowLink({ label, talk = false, both = false }: { label: string; talk?: boolean; both?: boolean }) {
  return (
    <div aria-hidden="true" className="flex items-center justify-center gap-3 py-2.5 lg:flex-col lg:gap-2 lg:px-2 lg:py-0">
      <span
        className={`text-[11.5px] font-semibold leading-[1.3] lg:max-w-[100px] lg:text-center ${
          talk ? "text-[var(--lp-ink-faint)]" : "text-[var(--lp-accent-ink)]"
        }`}
      >
        {label}
      </span>
      <span className={`ai-flow-line${talk ? " ai-flow-line--talk" : ""}`}>
        <i className="ai-flow-dot" />
        {both && <i className="ai-flow-dot ai-flow-dot--back" />}
      </span>
    </div>
  );
}

/**
 * Where the model runs: devices → Autopilot Monitor (MCP) → the AI client → the model. Picking a
 * placement highlights the matching clients and models and explains who processes the
 * conversation and what to set up.
 */
export function DataPath() {
  const [placement, setPlacement] = useState<Placement>("vendor");
  const active = PLACEMENTS.find(p => p.id === placement) ?? PLACEMENTS[0];

  return (
    <div className="mt-10 rounded-3xl border border-[var(--lp-line)] bg-[var(--lp-surface)] p-5 shadow-[0_20px_25px_-5px_rgba(0,0,0,0.04),0_8px_10px_-6px_rgba(0,0,0,0.04)] sm:p-8">
      <div className="flex flex-col justify-between gap-4 md:flex-row md:items-center">
        <div>
          <h3 className="text-xl font-bold tracking-tight text-[var(--lp-ink)] sm:text-2xl">Where should the model run?</h3>
          <p className="mt-1 text-sm text-[var(--lp-ink-soft)]">Pick one. The data and the tools stay the same.</p>
        </div>
        <div role="tablist" aria-label="Where the model runs" className={SEGMENT_GROUP}>
          {PLACEMENTS.map(p => (
            <button
              key={p.id}
              type="button"
              role="tab"
              aria-selected={p.id === placement}
              data-track={`model_runs:${p.id}`}
              onClick={() => setPlacement(p.id)}
              className={`${SEGMENT_BUTTON} ${p.id === placement ? SEGMENT_ON : SEGMENT_OFF}`}
            >
              {p.label}
            </button>
          ))}
        </div>
      </div>

      <div className="mt-10 grid grid-cols-1 lg:grid-cols-[minmax(0,0.82fr)_84px_minmax(0,1.12fr)_104px_minmax(0,1.08fr)_104px_minmax(0,1fr)] lg:items-center">
        <Node tag="Yours">
          <NodeTitle icon={<LaptopIcon className="h-[18px] w-[18px] text-[var(--lp-ink-soft)]" />}>Your devices</NodeTitle>
          <p className="mt-1 text-[12.5px] leading-[1.45] text-[var(--lp-ink-soft)]">The agent records each enrollment while it runs.</p>
        </Node>
        <FlowLink label="telemetry" />
        <Node tag="Autopilot Monitor" accent>
          <NodeTitle icon={<BrandMark className="h-[18px] w-[18px]" />}>Autopilot Monitor</NodeTitle>
          <div className="mt-3 flex flex-wrap gap-1.5">
            {["Enrollment data", "Analysis rules", "Error-code catalog", "Product docs"].map(chip => (
              <span
                key={chip}
                className="rounded-full border border-[var(--lp-line-soft)] bg-[var(--lp-surface)] px-2 py-[3px] text-[11.5px] font-medium text-[var(--lp-ink-soft)]"
              >
                {chip}
              </span>
            ))}
          </div>
          <div className="mt-2.5 flex items-center justify-between gap-2 rounded-[10px] bg-[var(--lp-accent-ink)] px-3 py-2 text-[13px] font-bold text-white">
            <span>MCP server</span>
            <span className="font-mono text-[11px] font-medium opacity-85">read-only</span>
          </div>
          <p className="mt-2.5 text-[12.5px] leading-[1.45] text-[var(--lp-ink-soft)]">Answers tool calls. Calls no AI provider itself.</p>
        </Node>
        <FlowLink label="tool calls and results" both />
        <Node tag="Yours">
          <NodeTitle icon={<ChatIcon className="h-[18px] w-[18px] text-[var(--lp-ink-soft)]" />}>Your AI client</NodeTitle>
          <Rows rows={CLIENT_ROWS} placement={placement} />
        </Node>
        <FlowLink label="your conversation" talk both />
        <Node tag="Your choice">
          <NodeTitle icon={<ChipIcon className="h-[18px] w-[18px] text-[var(--lp-ink-soft)]" />}>The model</NodeTitle>
          <Rows rows={MODEL_ROWS} placement={placement} />
        </Node>
      </div>

      <div className="mt-8 flex flex-col gap-3 text-[13px] text-[var(--lp-ink-soft)] md:flex-row md:gap-8">
        <span className="inline-flex items-center gap-2.5">
          <span className="h-0.5 w-7 shrink-0 rounded bg-[var(--lp-accent)]" />
          Reaches Autopilot Monitor: telemetry from your devices, tool calls from your assistant.
        </span>
        <span className="inline-flex items-center gap-2.5">
          <span className="w-7 shrink-0 border-t-2 border-dashed border-[var(--lp-ink-faint)]" />
          Never reaches Autopilot Monitor: your prompts and the model&apos;s answers.
        </span>
      </div>

      <div className="mt-8 grid grid-cols-1 gap-6 border-t border-[var(--lp-line-soft)] pt-8 md:grid-cols-3 md:gap-10">
        <div>
          <p className="text-[11px] font-semibold uppercase tracking-[0.24em] text-[var(--lp-ink-faint)]">Typical clients</p>
          <p className="mt-2 text-[15px] leading-relaxed text-[var(--lp-ink-soft)]">{active.clients}</p>
        </div>
        <div>
          <p className="text-[11px] font-semibold uppercase tracking-[0.24em] text-[var(--lp-ink-faint)]">Who processes the conversation</p>
          <p className="mt-2 text-[15px] leading-relaxed text-[var(--lp-ink-soft)]">{active.processedBy}</p>
        </div>
        <div>
          <p className="text-[11px] font-semibold uppercase tracking-[0.24em] text-[var(--lp-ink-faint)]">What you set up</p>
          <p className="mt-2 text-[15px] leading-relaxed text-[var(--lp-ink-soft)]">{active.setup}</p>
        </div>
      </div>
    </div>
  );
}
