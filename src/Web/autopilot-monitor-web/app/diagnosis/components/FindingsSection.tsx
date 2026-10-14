"use client";

import { useState } from "react";
import type { Route } from "next";
import { RuleInlines, RuleText } from "@/components/findings/RuleText";
import { EvidenceChips } from "@/components/findings/EvidenceChips";
import { RecentFiresLine } from "@/components/findings/RecentFiresLine";
import { ruleTextBlocks } from "@/lib/ruleText";
import { safeHttpUrl } from "@/lib/safeDocUrl";
import { trackEvent } from "@/lib/appInsights";
import type { FindingGroups, FindingView, PrimaryTone } from "../lib/findingGroups";
import { formatFact, shortTime } from "../lib/diagnosisFormat";
import { remediationGroups } from "../lib/steps";

const SEVERITY: Record<string, string> = {
  critical: "border-red-200 bg-red-100 text-red-800",
  high: "border-orange-200 bg-orange-100 text-orange-800",
  warning: "border-yellow-200 bg-yellow-100 text-yellow-800",
  info: "border-blue-200 bg-blue-100 text-blue-800",
};

/** The rule telemetry next to each finding: enrollments it fired on in 14 days, and the dashboard filtered to it. */
export interface RuleFires {
  counts: Record<string, number>;
  href: (ruleId: string) => Route;
}

const PRIMARY_TONE: Record<PrimaryTone, string> = {
  red: "text-red-700",
  orange: "text-orange-700",
  amber: "text-amber-800",
};

const CONFIDENCE_WIDTH: Record<string, string> = {
  certain: "w-full",
  "very likely": "w-11/12",
  likely: "w-3/4",
  possible: "w-1/2",
  weak: "w-1/4",
};

/** Every open finding of the session, grouped by what it means for the outcome. */
export default function FindingsSection({
  groups,
  analysisNote,
  eventHref,
  ruleFires,
}: {
  groups: FindingGroups;
  /** Shown instead of rows while there are none (analysis running, no rule fired, read failed). */
  analysisNote: string | null;
  eventHref: (eventId: string) => Route;
  ruleFires: RuleFires;
}) {
  const sections: { label: string; tone: string; items: FindingView[] }[] = [
    { label: groups.primary.label, tone: PRIMARY_TONE[groups.primary.tone], items: groups.primary.items },
    { label: "The outcome", tone: "text-gray-700", items: groups.outcome },
    { label: "Context", tone: "text-gray-600", items: groups.context },
    { label: "Other findings", tone: "text-gray-600", items: groups.other },
  ].filter((s) => s.items.length > 0);
  const count = sections.reduce((n, s) => n + s.items.length, 0);

  return (
    <section id="findings" className="scroll-mt-16 overflow-hidden rounded-lg bg-white shadow" aria-labelledby="findings-title">
      <div className="border-b border-gray-200 px-4 py-4 sm:px-5">
        <h2 id="findings-title" className="text-lg font-semibold text-gray-900">
          Findings <span className="font-normal text-gray-500">· {count}</span>
        </h2>
      </div>
      {count === 0 && <p className="px-4 py-4 text-sm text-gray-500 sm:px-5">{analysisNote ?? "No analyze rule fired for this session."}</p>}
      {sections.map((section) => (
        <div key={section.label}>
          <p className={`border-b border-gray-200 px-4 py-2 text-xs font-semibold sm:px-5 ${section.tone}`}>
            {section.label} · {section.items.length}
          </p>
          <div className="divide-y divide-gray-200 border-b border-gray-200">
            {section.items.map((f) => (
              <FindingRow key={f.result.ruleId} finding={f} eventHref={eventHref} ruleFires={ruleFires} />
            ))}
          </div>
        </div>
      ))}
    </section>
  );
}

function FindingRow({ finding, eventHref, ruleFires }: { finding: FindingView; eventHref: (eventId: string) => Route; ruleFires: RuleFires }) {
  const [open, setOpen] = useState(false);
  const { result } = finding;
  const panelId = `finding-${result.ruleId}`;
  const time = shortTime(result.firstDetectedAt ?? result.detectedAt);

  const toggle = () => {
    if (!open) trackEvent("diagnosis_finding_expanded", { ruleId: result.ruleId, role: finding.role || "none" });
    setOpen(!open);
  };

  return (
    <div>
      <button
        type="button"
        aria-expanded={open}
        aria-controls={panelId}
        onClick={toggle}
        className="grid w-full grid-cols-[5.5rem_minmax(0,1fr)_1rem] items-center gap-x-4 gap-y-1 px-4 py-3 text-left hover:bg-gray-50 sm:px-5 md:grid-cols-[5.5rem_minmax(0,1fr)_11rem_7rem_4.5rem_1rem]"
      >
        <span>
          <span className={`rounded border px-1.5 py-0.5 text-[11px] font-semibold uppercase ${SEVERITY[result.severity] ?? SEVERITY.info}`}>{result.severity}</span>
        </span>
        <span className="min-w-0">
          <span className="block text-sm font-medium text-gray-900">{result.ruleTitle}</span>
          {finding.summary && <span className="block truncate text-xs text-gray-500">{finding.summary}</span>}
          <span className="mt-0.5 block text-xs text-gray-500 md:hidden">
            {finding.about} · {finding.confidence}
            {time && ` · ${time}`}
          </span>
        </span>
        <span className="hidden truncate text-sm text-gray-700 md:block">{finding.about}</span>
        <span className="hidden items-center gap-1.5 text-xs text-gray-500 md:flex" title={`Confidence ${result.confidenceScore}`}>
          <span className="h-1.5 w-10 overflow-hidden rounded-full bg-gray-200">
            <span className={`block h-full bg-gray-500 ${CONFIDENCE_WIDTH[finding.confidence] ?? "w-1/4"}`} />
          </span>
          {finding.confidence}
        </span>
        <span className="hidden whitespace-nowrap text-right text-xs tabular-nums text-gray-500 md:block">{time}</span>
        <svg className={`h-4 w-4 text-gray-400 transition-transform ${open ? "rotate-90" : ""}`} fill="none" viewBox="0 0 24 24" stroke="currentColor" aria-hidden="true">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
        </svg>
      </button>
      {open && (
        <div id={panelId} className="px-4 pb-5 sm:px-5 md:pl-[8.5rem]">
          <FindingDetail finding={finding} eventHref={eventHref} ruleFires={ruleFires} />
        </div>
      )}
    </div>
  );
}

function FindingDetail({ finding, eventHref, ruleFires }: { finding: FindingView; eventHref: (eventId: string) => Route; ruleFires: RuleFires }) {
  const { result, presentation } = finding;
  const facts = presentation?.facts ?? [];
  const fixes = remediationGroups(result);
  const docs = (result.relatedDocs ?? []).map((d) => ({ title: d.title, href: safeHttpUrl(d.url) })).filter((d) => d.href);

  return (
    <div className="grid grid-cols-1 gap-5 lg:grid-cols-3">
      <div className="min-w-0 space-y-4 lg:col-span-2">
        {facts.length > 0 && (
          <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1 text-sm">
            {facts.map((fact) => {
              const shown = formatFact(fact.value, fact.format);
              return (
                <FactRow key={fact.label + fact.value} label={fact.label} text={shown.text} mono={shown.mono} hint={shown.hint} />
              );
            })}
          </dl>
        )}
        <RuleText blocks={ruleTextBlocks(result.explanation, result.matchedConditions)} className="space-y-2 text-sm text-gray-600" />
        {(ruleFires.counts[result.ruleId] ?? 0) > 0 && (
          <p>
            <RecentFiresLine
              count={ruleFires.counts[result.ruleId]}
              href={ruleFires.href(result.ruleId)}
              linkClassName="not-italic font-medium text-blue-700 hover:underline"
            />
          </p>
        )}
      </div>
      <div className="min-w-0 space-y-4">
        <div>
          <p className="text-[11px] font-medium uppercase tracking-wide text-gray-500">Evidence</p>
          <EvidenceChips matchedConditions={result.matchedConditions} href={eventHref} className="mt-2 flex flex-wrap gap-2" />
        </div>
        {fixes.length > 0 && (
          <div>
            <p className="text-[11px] font-medium uppercase tracking-wide text-gray-500">Fix</p>
            <div className="mt-2 space-y-3">
              {fixes.map((group, i) => (
                <div key={i}>
                  <RuleText blocks={group.title} className="text-sm font-medium text-gray-900" />
                  <ul className="mt-1 list-disc space-y-1 pl-5 text-sm text-gray-700">
                    {group.steps.map((step, j) => (
                      <li key={j}>
                        <RuleInlines inlines={step} />
                      </li>
                    ))}
                  </ul>
                </div>
              ))}
            </div>
          </div>
        )}
        {docs.length > 0 && (
          <ul className="space-y-1">
            {docs.map((d) => (
              <li key={d.href}>
                <a href={d.href!} target="_blank" rel="noopener noreferrer" className="text-sm text-blue-700 hover:underline">
                  {d.title}
                </a>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

function FactRow({ label, text, mono, hint }: { label: string; text: string; mono: boolean; hint?: string }) {
  return (
    <>
      <dt className="text-gray-500">{label}</dt>
      <dd className={`break-words text-gray-900 ${mono ? "font-mono" : ""}`} title={hint}>
        {text}
        {hint && <span className="ml-2 font-sans text-xs text-gray-500">{hint}</span>}
      </dd>
    </>
  );
}
