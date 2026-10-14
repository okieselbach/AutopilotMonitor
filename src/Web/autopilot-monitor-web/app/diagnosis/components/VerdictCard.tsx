"use client";

import type { ReactNode } from "react";
import Link from "next/link";
import type { Route } from "next";
import type { DiagnosisCulprit, DiagnosisEspCategory, DiagnosisHeadline, DiagnosisWaitItem } from "@/lib/generated/wire-types.generated";
import type { VerdictText, VerdictTone } from "../lib/verdictText";
import { formatFact, phaseName, shortTime, stepName, timeOfDay, waitName } from "../lib/diagnosisFormat";
import { vocabularyEntry, type CulpritEvidence } from "../lib/vocabulary";
import { humanizeKey } from "@/lib/evidenceChips";

// Literal class strings per tone (Tailwind must see them whole; dark mode comes from the global overrides).
const BAND: Record<VerdictTone, { box: string; text: string; icon: string }> = {
  red: { box: "bg-red-50 border-red-200", text: "text-red-800", icon: "text-red-600" },
  orange: { box: "bg-orange-50 border-orange-200", text: "text-orange-800", icon: "text-orange-600" },
  sky: { box: "bg-sky-50 border-sky-200", text: "text-sky-900", icon: "text-sky-900" },
  slate: { box: "bg-slate-50 border-slate-200", text: "text-slate-800", icon: "text-slate-600" },
  green: { box: "bg-green-50 border-green-200", text: "text-green-800", icon: "text-green-700" },
  amber: { box: "bg-amber-50 border-amber-200", text: "text-amber-900", icon: "text-amber-700" },
  blue: { box: "bg-blue-50 border-blue-200", text: "text-blue-800", icon: "text-blue-700" },
  gray: { box: "bg-gray-50 border-gray-200", text: "text-gray-800", icon: "text-gray-500" },
};

const EVIDENCE_LABELS: Record<CulpritEvidence, string> = {
  install_failed: "Install error",
  presumed_failed: "Installing when the ESP failed",
  presumed_stuck: "Still installing when the ESP gave up",
  in_flight: "Still in progress",
  never_started: "Never started",
  not_completed: "Not completed",
  named_by_esp: "Named by the ESP",
  appx_log: "AppX deployment log",
  provider_stalled: "Policy provider did not finish",
};

const PILL = "rounded-full border border-gray-300 bg-gray-100 px-2 py-0.5 text-[11px] font-medium text-gray-600";

const CLOCK = "M12 6v6h4.5m4.5 0a9 9 0 11-18 0 9 9 0 0118 0z";

// Columns per tile count, so every row is full and no gap shows the grid's background.
const TILE_COLUMNS: Record<number, string> = {
  1: "",
  2: "sm:grid-cols-2",
  3: "sm:grid-cols-3",
  4: "sm:grid-cols-2 lg:grid-cols-4",
  5: "lg:grid-cols-5",
};

export default function VerdictCard({
  headline,
  verdict,
  basedOn,
  timelineHref,
  supersededHref,
  onCopySummary,
  copied,
}: {
  headline: DiagnosisHeadline;
  verdict: VerdictText;
  /** Findings behind the verdict (all open findings); 0 hides the link. */
  basedOn: number;
  timelineHref: Route | null;
  supersededHref: Route | null;
  onCopySummary: () => void;
  copied: boolean;
}) {
  const band = BAND[verdict.tone];
  const meta = [
    timeOfDay(headline.failedStep?.at ?? headline.at),
    headline.failedStep?.errorCode ? `ESP error ${headline.failedStep.errorCode}` : null,
  ].filter(Boolean);

  return (
    <section className="overflow-hidden rounded-lg bg-white shadow" aria-labelledby="verdict-summary">
      <div className={`flex flex-wrap items-center justify-between gap-x-4 gap-y-1 border-b px-5 py-3 sm:px-6 ${band.box}`}>
        <p className={`flex items-center gap-2 text-sm font-semibold ${band.text}`}>
          <BandIcon outcome={headline.outcome} className={`h-5 w-5 shrink-0 ${band.icon}`} />
          {verdict.title}
        </p>
        {meta.length > 0 && <p className={`text-xs tabular-nums ${band.text}`}>{meta.join(" · ")}</p>}
      </div>

      <div className="p-5 sm:p-6">
        <p className="text-xs font-medium uppercase tracking-wide text-gray-500">{verdict.heading}</p>
        <h2 id="verdict-summary" className="mt-1 text-xl font-semibold text-gray-900 text-balance">
          {verdict.summary}
        </h2>
        {verdict.details.length > 0 && <p className="mt-2 max-w-3xl text-gray-600">{verdict.details.join(" ")}</p>}

        {headline.culprits && headline.culprits.length > 0 && <Culprits culprits={headline.culprits} />}
        {headline.waiting && headline.waiting.length > 0 && <Waiting items={headline.waiting} />}
        {headline.esp && headline.esp.length > 0 && <EspPhases phases={headline.esp} headline={headline} />}

        <div className="mt-6 flex flex-wrap items-center gap-2">
          {timelineHref && (
            <Link
              href={timelineHref}
              className="inline-flex items-center gap-2 rounded-md bg-green-600 px-3.5 py-2 text-sm font-medium text-white hover:bg-green-700"
            >
              Show in the timeline
            </Link>
          )}
          {supersededHref && (
            <Link
              href={supersededHref}
              className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3.5 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
            >
              Open the newer enrollment
            </Link>
          )}
          <button
            type="button"
            onClick={onCopySummary}
            className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3.5 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
          >
            {copied ? "Copied" : "Copy summary"}
          </button>
          {basedOn > 0 && (
            <a href="#findings" className="ml-auto inline-flex items-center gap-1 text-sm text-blue-700 hover:underline">
              {basedOn === 1 ? "1 finding" : `${basedOn} findings`} below
              <span aria-hidden="true">↓</span>
            </a>
          )}
        </div>
      </div>
    </section>
  );
}

function Culprits({ culprits }: { culprits: DiagnosisCulprit[] }) {
  const [first, ...rest] = culprits;
  const tiles = [
    <Tile key="subject" label={first.kind === "app" ? "App" : first.kind === "appx_package" ? "Store package" : "Policy provider"}>
      <dd className="mt-1 break-words text-sm font-semibold text-gray-900">{first.name}</dd>
      <dd className="mt-1.5 flex flex-wrap gap-1">
        {[first.appType, first.targeted, first.intent === "Install" ? "Required" : first.intent, first.scope].filter(Boolean).map((p) => (
          <span key={p} className={PILL}>
            {p}
          </span>
        ))}
      </dd>
      <dd className="mt-1 text-xs text-gray-500">{vocabularyEntry(EVIDENCE_LABELS, first.evidence) ?? humanizeKey(first.evidence)}</dd>
    </Tile>,
    (first.errorCode || first.exitCode) && <ErrorTile key="error" code={first.errorCode ?? first.exitCode!} label={first.errorCode ? "Error" : "Exit code"} />,
    first.installStarts != null && (
      <Tile key="starts" label="Install starts">
        <dd className="mt-1 text-sm font-semibold tabular-nums text-gray-900">{first.installStarts}</dd>
        {first.lastStartedAt && <dd className="mt-1 text-xs tabular-nums text-gray-500">Last start {shortTime(first.lastStartedAt)}</dd>}
      </Tile>
    ),
    (first.detectionResult || first.state) && (
      <Tile key="state" label={first.detectionResult ? "Detection" : "State"}>
        <dd className="mt-1 text-sm font-semibold text-gray-900">{humanizeKey(first.detectionResult ?? first.state ?? "")}</dd>
        {first.failedAt && <dd className="mt-1 text-xs tabular-nums text-gray-500">Failed {shortTime(first.failedAt)}</dd>}
      </Tile>
    ),
    first.stalledForMinutes != null && (
      <Tile key="stuck" label="Stuck for">
        <dd className="mt-1 text-sm font-semibold tabular-nums text-gray-900">{Math.round(first.stalledForMinutes)} min</dd>
      </Tile>
    ),
  ].filter(Boolean);
  return (
    <div className="mt-5">
      <dl className={`grid grid-cols-1 gap-px overflow-hidden rounded-lg border border-gray-200 bg-gray-200 ${TILE_COLUMNS[tiles.length] ?? TILE_COLUMNS[4]}`}>
        {tiles}
      </dl>
      {rest.length > 0 && (
        <ul className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-sm text-gray-600">
          {rest.map((c) => (
            <li key={`${c.kind}:${c.key ?? c.name}`}>
              <span className="font-medium text-gray-900">{c.name}</span>
              <span className="text-gray-500"> · {vocabularyEntry(EVIDENCE_LABELS, c.evidence) ?? humanizeKey(c.evidence)}</span>
              {c.errorCode && <span className="font-mono text-xs text-gray-500"> · {c.errorCode}</span>}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function Tile({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="min-w-0 bg-white p-3 sm:p-4">
      <dt className="text-xs text-gray-500">{label}</dt>
      {children}
    </div>
  );
}

function ErrorTile({ code, label }: { code: string; label: string }) {
  const fact = formatFact(code, "errorCode");
  return (
    <Tile label={label}>
      <dd className="mt-1 break-all font-mono text-sm font-semibold text-gray-900">{fact.text}</dd>
      <dd className="mt-1 text-xs text-gray-500">{fact.hint ?? "Not in our error catalog"}</dd>
    </Tile>
  );
}

function Waiting({ items }: { items: DiagnosisWaitItem[] }) {
  return (
    <div className="mt-5">
      <h3 className="text-sm font-semibold text-gray-900">Still waiting for</h3>
      <ul className="mt-2 flex flex-wrap gap-1.5">
        {items.map((item) => (
          <li key={`${item.kind}:${item.name}`} className="inline-flex items-center gap-1 rounded border border-gray-200 bg-gray-50 px-2 py-0.5 text-xs text-gray-700">
            {waitName(item)}
            {item.state && <span className="text-gray-500">· {humanizeKey(item.state).toLowerCase()}</span>}
          </li>
        ))}
      </ul>
    </div>
  );
}

function EspPhases({ phases, headline }: { phases: DiagnosisEspCategory[]; headline: DiagnosisHeadline }) {
  const config = headline.espConfig;
  const profile = [
    config?.continueAnyway === false ? "no “continue anyway”" : config?.continueAnyway ? "“continue anyway” allowed" : null,
    config?.tryAgain ? "retry allowed" : null,
    config?.timeoutMinutes ? `timeout ${config.timeoutMinutes} min` : null,
    config?.blockingAppCount ? `${config.blockingAppCount} blocking ${config.blockingAppCount === 1 ? "app" : "apps"}` : null,
  ].filter(Boolean);
  return (
    <div className="mt-6">
      <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
        <h3 className="text-sm font-semibold text-gray-900">Enrollment Status Page</h3>
        {profile.length > 0 && <p className="text-xs text-gray-500">{profile.join(" · ")}</p>}
      </div>
      <ol className="mt-3 grid grid-cols-1 gap-3 sm:grid-cols-3">
        {phases.map((phase) => {
          const failed = phase.state === "failed";
          return (
            <li key={phase.category} className={`rounded-lg border p-3 ${failed ? "border-red-200 bg-red-50" : "border-gray-200"}`}>
              <p className={`flex items-center gap-1.5 text-sm ${failed ? "font-semibold text-red-800" : "font-medium text-gray-900"}`}>
                <PhaseIcon state={phase.state} />
                {phaseName(phase.category)}
              </p>
              {phase.subcategories.length > 0 && (
                <ul className="mt-2 flex flex-wrap gap-1 text-[11px]">
                  {phase.subcategories.map((s) => (
                    <li
                      key={s.name}
                      title={s.statusText ?? undefined}
                      className={`inline-flex items-center gap-1 rounded border px-1.5 py-0.5 ${
                        s.state === "failed" ? "border-red-200 bg-red-100 font-semibold text-red-800" : "border-gray-200 bg-white text-gray-600"
                      }`}
                    >
                      <StepMark state={s.state} />
                      {stepName(s.name)}
                      {s.errorCode && <span className="font-mono"> · {s.errorCode}</span>}
                    </li>
                  ))}
                </ul>
              )}
            </li>
          );
        })}
      </ol>
    </div>
  );
}

function PhaseIcon({ state }: { state: string }) {
  if (state === "succeeded") return <span aria-label="Completed" className="text-green-700">✓</span>;
  if (state === "failed") return <span aria-label="Failed" className="text-red-600">✕</span>;
  return (
    <svg role="img" aria-label="In progress" className="h-4 w-4 text-gray-500" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.75}>
      <path strokeLinecap="round" strokeLinejoin="round" d={CLOCK} />
    </svg>
  );
}

function StepMark({ state }: { state: string }) {
  const s = state.toLowerCase();
  if (s === "succeeded") return <span aria-hidden="true" className="text-green-700">✓</span>;
  if (s === "failed") return <span aria-hidden="true">✕</span>;
  if (s === "notrequired") return <span aria-hidden="true" className="text-gray-400">–</span>;
  return <span aria-hidden="true" className="text-gray-400">•</span>;
}

function BandIcon({ outcome, className }: { outcome: string; className: string }) {
  const path =
    outcome === "failed"
      ? "M9.75 9.75l4.5 4.5m0-4.5l-4.5 4.5M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
      : outcome === "succeeded"
        ? "M9 12.75L11.25 15 15 9.75M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
        : outcome === "awaiting_user" || outcome === "pending"
          ? "M15.75 6a3.75 3.75 0 11-7.5 0 3.75 3.75 0 017.5 0zM4.501 20.118a7.5 7.5 0 0114.998 0"
          : CLOCK;
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.75} aria-hidden="true">
      <path strokeLinecap="round" strokeLinejoin="round" d={path} />
    </svg>
  );
}
