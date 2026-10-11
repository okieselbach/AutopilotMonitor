import { DOCS_PATHS } from "@/lib/docsPaths";
import { DOCS_URL } from "@/lib/config";
import { ArrowRightIcon } from "./icons";
import { Step } from "./Step";

type TraceTone = "ok" | "faint" | "ink";

interface TraceLine {
  text: string;
  tone: TraceTone;
  /** Space above the line: separates the three tool calls. */
  gap?: boolean;
  /** Free text that may wrap; the aligned trace columns never do. */
  wrap?: boolean;
}

const TONE: Record<TraceTone, string> = {
  ok: "text-[var(--lp-term-ok)]",
  faint: "text-[var(--lp-term-faint)]",
  ink: "text-[var(--lp-term-ink)]",
};

/**
 * The trace of a real dry-run (2026-10-11) against the two attempts the landing demo talks about —
 * the same sessions, the same rule, anonymized like the demo. Field names and verdicts are the
 * test_analyze_rule wire values; the counts are matchingEventCount per condition.
 */
const TRACE: TraceLine[] = [
  { text: "validate_rule({ rule })", tone: "faint" },
  { text: "✓ valid · 0 errors · 0 warnings", tone: "ok" },
  { text: 'test_analyze_rule({ sessionId: "e6a1…f43b", rule })   # attempt 1, failed', tone: "faint", gap: true },
  { text: "verdict  fired · confidence 70 + 15 = 85 (threshold 70)", tone: "ok" },
  { text: "✓ unmapped_exit_code    app_install_failed          1 event", tone: "ink" },
  { text: "    Contoso CRM App: [Win32App] Admin did NOT set mapping for lpExitCode: 60001", tone: "faint" },
  { text: "✓ esp_terminal_failure  enrollment_failed           1 event", tone: "ink" },
  { text: "✓ esp_hresult           esp_failure_settle_started  1 event  +15", tone: "ink" },
  { text: "    errorCode 0x8007ea61", tone: "faint" },
  {
    text: "explanation  Contoso CRM App exited with code 60001, which has no entry in the app's return-code table. The IME reported the install as failed and the ESP stopped the enrollment. The ESP HRESULT carries the exit code's low 16 bits in hex: 0x8007ea61.",
    tone: "ink",
    gap: true,
    wrap: true,
  },
  { text: 'test_analyze_rule({ sessionId: "98db…16af", rule })   # retry, succeeded', tone: "faint", gap: true },
  { text: "verdict  required_condition_not_met · stays silent", tone: "faint" },
  { text: "✗ unmapped_exit_code    app_install_failed          0 events", tone: "faint" },
  { text: "✗ esp_terminal_failure  enrollment_failed           0 events", tone: "faint" },
  { text: "✗ esp_hresult           esp_failure_settle_started  0 events", tone: "faint" },
];

/**
 * "From finding to rule": the last mile of an AI investigation. Backed by the rule-authoring tools —
 * validate_rule for every role, test_analyze_rule for Tenant Admins (TenantAdminOrGlobalReader route);
 * neither persists anything, so the "Read-only tools" guardrail below stays true.
 */
export function RuleLoopSection() {
  return (
    <section
      id="rules"
      data-track-section="rules"
      className="scroll-mt-20 border-t border-[var(--lp-line-soft)] bg-[var(--lp-surface)] px-6 py-20 sm:py-24"
    >
      <div className="mx-auto grid max-w-7xl grid-cols-1 items-start gap-10 lg:grid-cols-[minmax(0,5fr)_minmax(0,7fr)] lg:gap-14">
        <div className="min-w-0">
          <p className="text-xs font-semibold uppercase tracking-[0.22em] text-[var(--lp-accent-ink)]">Close the loop</p>
          <h2 className="mt-3 text-balance text-3xl font-bold tracking-tight text-[var(--lp-ink)] sm:text-4xl">From finding to rule.</h2>
          <p className="mt-4 text-lg leading-relaxed text-[var(--lp-ink-soft)]">
            When your assistant finds a cause that no rule knows yet, it can write the rule. It checks the draft against
            the rule schema and dry-runs it against real sessions of your tenant: it should fire on the failed one and
            stay silent on a healthy one. Nothing is saved until a Tenant Admin creates the rule in the portal.
          </p>
          <ol className="mt-8 grid grid-cols-1 gap-4">
            <Step n={1} title="Investigate">
              The built-in <span className="font-mono text-[12.5px]">debug-session</span> prompt walks your assistant from the
              session summary to the diagnostics package and the IME logs. Downloading the package needs a client that
              works with files.
            </Step>
            <Step n={2} title="Draft and dry-run">
              <span className="font-mono text-[12.5px]">validate_rule</span> checks the draft.{" "}
              <span className="font-mono text-[12.5px]">test_analyze_rule</span> replays it against real sessions and saves
              nothing. Dry-runs need the Tenant Admin role.
            </Step>
            <Step n={3} title="Switch it on, or share it">
              Paste the JSON under Rules → Analyze Rules → Create Custom Rule and test it there against a session. Submit
              it to the community pool; once accepted after review, it ships to every tenant.
            </Step>
          </ol>
          <a
            href={`${DOCS_URL}${DOCS_PATHS.aiRuleAuthoring}`}
            target="_blank"
            rel="noopener noreferrer"
            data-track="rules_docs"
            className="mt-6 inline-flex items-center gap-2 text-[15px] font-semibold text-[var(--lp-accent-ink)] hover:opacity-80"
          >
            AI-assisted rule authoring guide
            <ArrowRightIcon />
          </a>
        </div>
        <figure className="m-0 min-w-0 lg:sticky lg:top-24">
          <div className="overflow-hidden rounded-2xl border border-[var(--lp-term-line)] bg-[var(--lp-term-bg)] shadow-2xl shadow-black/20">
            <div className="border-b border-[var(--lp-term-line)] bg-[var(--lp-term-deep)] px-4 py-2.5">
              <span className="block truncate font-mono text-[11px] text-[var(--lp-term-faint)]">
                ANALYZE-CUSTOM-110 · Unmapped installer exit code stopped the ESP
              </span>
            </div>
            <div className="overflow-x-auto p-4 font-mono text-[11.5px] leading-[1.7] sm:p-5">
              {TRACE.map((line, i) => (
                <p
                  key={i}
                  className={`m-0 ${line.wrap ? "whitespace-pre-wrap" : "whitespace-pre"} ${line.gap ? "mt-3" : ""} ${TONE[line.tone]}`}
                >
                  {line.text}
                </p>
              ))}
            </div>
          </div>
          <figcaption className="mt-3 text-[13px] text-[var(--lp-ink-faint)]">From a real dry-run, anonymized.</figcaption>
        </figure>
      </div>
    </section>
  );
}
