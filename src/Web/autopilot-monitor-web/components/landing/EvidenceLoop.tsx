import Link from "next/link";
import { Reveal } from "./Reveal";
import { ArrowRightIcon, RepeatIcon } from "../ai/icons";

interface Station {
  title: string;
  tag: string;
  text: string;
  /** The half of the loop most visitors don't expect from a monitoring tool — drawn in the accent. */
  accent: boolean;
}

/**
 * The four stations in loop order. Every claim carries its own qualifier: the raw logs exist only
 * with diagnostics upload on, and only clients that work with files open them.
 */
const STATIONS: Station[] = [
  {
    title: "Capture",
    tag: "agent",
    text: "Phases, apps, errors and timings stream in while the device enrolls. With diagnostics upload on, failed enrollments also keep their IME and agent logs.",
    accent: false,
  },
  {
    title: "Detect",
    tag: "analyze rules",
    text: "Built-in, community and your own analyze rules name known problems as they happen.",
    accent: false,
  },
  {
    title: "Investigate",
    tag: "MCP",
    text: "No rule fits? Your AI assistant reads the session through MCP. Clients that work with files, such as Claude Code, go on into the raw IME logs.",
    accent: true,
  },
  {
    title: "Learn",
    tag: "new rule",
    text: "The assistant drafts a rule and dry-runs it against your sessions. A Tenant Admin switches it on, or submits it to the community pool.",
    accent: true,
  },
];

function StationCard({ station, index }: { station: Station; index: number }) {
  const frame = station.accent
    ? "border-[var(--lp-accent-line)] bg-[var(--lp-accent-soft)]"
    : "border-[var(--lp-line)] bg-[var(--lp-bg)]";
  const label = station.accent ? "text-[var(--lp-accent-ink)]" : "text-[var(--lp-ink-faint)]";
  return (
    <div className={`h-full rounded-2xl border p-4 ${frame}`}>
      <p className={`flex items-center justify-between gap-3 text-[11px] font-semibold uppercase tracking-[0.16em] ${label}`}>
        <span>
          {index + 1} · {station.title}
        </span>
        <span className="font-mono normal-case tracking-normal">{station.tag}</span>
      </p>
      <p className="mt-2 text-[13.5px] leading-relaxed text-[var(--lp-ink-soft)]">{station.text}</p>
    </div>
  );
}

/** A connector of the wide loop; `turn` rotates the right-pointing arrow into the loop's direction. */
function Connector({ turn, cell }: { turn: string; cell: string }) {
  return (
    <li aria-hidden="true" className={`flex items-center justify-center text-[var(--lp-ink-faint)] ${cell}`}>
      <ArrowRightIcon className={`w-5 h-5 ${turn}`} />
    </li>
  );
}

function NextTime({ className = "" }: { className?: string }) {
  return (
    <span className={`inline-flex items-center gap-1.5 font-semibold text-[var(--lp-accent-ink)] ${className}`}>
      <RepeatIcon className="w-3.5 h-3.5 shrink-0" />
      Next time: detected automatically
    </span>
  );
}

/**
 * "Evidence first": the loop behind every enrollment — capture, detect, investigate with AI, learn a
 * rule — told right after the stats, where most visitors still are. The terminal demo below ("Then
 * just ask") plays the investigate-and-learn half for real.
 */
export function EvidenceLoop() {
  const [capture, detect, investigate, learn] = STATIONS;
  return (
    <section
      id="evidence"
      data-track-section="evidence_loop"
      className="py-16 sm:py-20 px-6 scroll-mt-20 border-y border-[var(--lp-line-soft)] bg-[var(--lp-surface)]"
    >
      <div className="max-w-7xl mx-auto grid lg:grid-cols-[minmax(0,5fr)_minmax(0,7fr)] gap-10 lg:gap-14 items-center">
        <Reveal className="min-w-0">
          <p className="text-xs font-semibold uppercase tracking-[0.22em] text-[var(--lp-accent-ink)]">Evidence first</p>
          <h2 className="mt-3 text-3xl sm:text-4xl font-bold tracking-tight text-[var(--lp-ink)] text-balance">
            Never debug the same failure twice.
          </h2>
          <p className="mt-4 text-[16px] text-[var(--lp-ink-soft)] leading-relaxed max-w-xl">
            Every enrollment is recorded while it runs, so the evidence is still there after someone resets the
            device. Known problems are flagged by analyze rules. Unknown ones you investigate with your AI
            assistant — and what you find becomes a rule that catches the next one.
          </p>
          <p className="mt-5 text-[16px] font-semibold text-[var(--lp-ink)]">
            The first failure becomes evidence. The next one is a known problem.
          </p>
          <Link
            href="/ai#rules"
            prefetch={false}
            data-track="evidence_loop_ai"
            className="mt-5 inline-flex items-center gap-1.5 text-[15px] font-semibold text-[var(--lp-accent-ink)] hover:opacity-80 transition-opacity"
          >
            How the AI digs in
            <ArrowRightIcon className="w-4 h-4" />
          </Link>
        </Reveal>

        <Reveal delayMs={120} className="min-w-0">
          {/* Wide: a 2×2 loop. DOM order follows the loop for screen readers; grid placement draws the circle. */}
          <ol className="hidden sm:grid grid-cols-[minmax(0,1fr)_2.5rem_minmax(0,1fr)] grid-rows-[auto_2.75rem_auto]">
            <li className="col-start-1 row-start-1"><StationCard station={capture} index={0} /></li>
            <Connector turn="" cell="col-start-2 row-start-1" />
            <li className="col-start-3 row-start-1"><StationCard station={detect} index={1} /></li>
            <Connector turn="rotate-90" cell="col-start-3 row-start-2" />
            <li className="col-start-3 row-start-3"><StationCard station={investigate} index={2} /></li>
            <Connector turn="rotate-180" cell="col-start-2 row-start-3" />
            <li className="col-start-1 row-start-3"><StationCard station={learn} index={3} /></li>
            <Connector turn="-rotate-90" cell="col-start-1 row-start-2" />
            <li className="col-start-2 row-start-2 self-center justify-self-center whitespace-nowrap text-[11px]">
              <NextTime />
            </li>
          </ol>
          {/* Phones: the same loop as a column, closed by the way back. */}
          <ol className="sm:hidden flex flex-col gap-2">
            {STATIONS.map((station, i) => (
              <li key={station.title} className="flex flex-col gap-2">
                {i > 0 && (
                  <span aria-hidden="true" className="flex justify-center text-[var(--lp-ink-faint)]">
                    <ArrowRightIcon className="w-5 h-5 rotate-90" />
                  </span>
                )}
                <StationCard station={station} index={i} />
              </li>
            ))}
            <li className="flex justify-center pt-1 text-[12px]">
              <NextTime />
            </li>
          </ol>
        </Reveal>
      </div>
    </section>
  );
}
