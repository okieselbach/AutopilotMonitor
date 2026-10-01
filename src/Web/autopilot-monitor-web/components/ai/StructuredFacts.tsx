import { ArrowRightIcon, CheckChip } from "./icons";

const IME_LINE =
  '<![LOG[[Win32App] Admin did NOT set mapping for lpExitCode: 60001]LOG]!><time="11:14:58.5124" date="10-1-2026" component="AppWorkload" context="" type="1" thread="18" file="">';

const EVENT_FIELDS: [string, string][] = [
  ["event", "app_install_failed"],
  ["app", "Contoso CRM 4.2"],
  ["exit code", "60001 · no mapping"],
  ["phase", "Device setup"],
  ["attempt", "1"],
  ["install time", "1 min 32 s"],
];

const DOMAINS = [
  { title: "Sessions and phases", text: "Status, duration, phase timeline and reboots of every enrollment." },
  { title: "App installs", text: "Attempts, exit codes and install times per app, plus Delivery Optimization." },
  { title: "Errors, decoded", text: "Error codes with their meaning, IME log matches and rule findings." },
  { title: "Devices and network", text: "Model, TPM, OS build, connection type and, if enabled, location." },
  { title: "Software and CVEs", text: "Optional inventory matched against known CVEs, ranked by KEV and EPSS." },
  { title: "Knowledge built in", text: "Analysis rules, the error-code catalog and the product docs." },
];

function Caption({ label, meta, accent = false }: { label: string; meta: string; accent?: boolean }) {
  const color = accent ? "text-[var(--lp-accent-ink)]" : "text-[var(--lp-ink-faint)]";
  return (
    <figcaption className="flex items-baseline justify-between gap-3">
      <span className={`text-[11px] font-semibold uppercase tracking-[0.2em] ${color}`}>{label}</span>
      <span className={`font-mono text-[11px] ${color}`}>{meta}</span>
    </figcaption>
  );
}

function PipeArrow() {
  return (
    <div className="flex items-center justify-center text-[var(--lp-ink-faint)]" aria-hidden="true">
      <ArrowRightIcon className="h-5 w-5 rotate-90 lg:rotate-0" />
    </div>
  );
}

export function StructuredFacts() {
  return (
    <section
      id="structured"
      data-track-section="structured"
      className="scroll-mt-20 border-y border-[var(--lp-line-soft)] bg-[var(--lp-surface)] px-6 py-20 sm:py-24"
    >
      <div className="mx-auto max-w-7xl">
        <div className="max-w-2xl">
          <p className="text-xs font-semibold uppercase tracking-[0.22em] text-[var(--lp-accent-ink)]">Structured from the start</p>
          <h2 className="mt-3 text-balance text-3xl font-bold tracking-tight text-[var(--lp-ink)] sm:text-4xl">
            Your assistant reads facts, not log files.
          </h2>
          <p className="mt-4 text-lg leading-relaxed text-[var(--lp-ink-soft)]">
            The agent turns each enrollment into typed events while it runs. Your assistant queries them through dedicated
            tools, so its answers rest on data instead of guesswork.
          </p>
        </div>

        <div className="mt-12 grid grid-cols-1 items-stretch gap-3 lg:grid-cols-[minmax(0,1fr)_32px_minmax(0,1fr)_32px_minmax(0,1fr)] lg:gap-4">
          <figure className="flex flex-col gap-3 rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-bg)] p-5">
            <Caption label="On the device" meta="AppWorkload.log" />
            <pre className="m-0 whitespace-pre-wrap break-all rounded-[10px] border border-[var(--lp-term-line)] bg-[var(--lp-term-bg)] p-3 font-mono text-[11.5px] leading-[1.6] text-[var(--lp-term-ink)]">
              {IME_LINE}
            </pre>
            <p className="text-[13px] leading-relaxed text-[var(--lp-ink-soft)]">One line among thousands, spread over several log files.</p>
          </figure>
          <PipeArrow />
          <figure className="flex flex-col gap-3 rounded-2xl border border-[var(--lp-accent-line)] bg-[var(--lp-accent-soft)] p-5">
            <Caption label="In Autopilot Monitor" meta="typed event" accent />
            <dl className="grid grid-cols-[max-content_minmax(0,1fr)] gap-x-4 gap-y-1.5 font-mono text-[13px]">
              {EVENT_FIELDS.map(([key, value]) => (
                <div key={key} className="contents">
                  <dt className="text-[var(--lp-ink-faint)]">{key}</dt>
                  <dd className="m-0 font-medium text-[var(--lp-ink)]">{value}</dd>
                </div>
              ))}
            </dl>
          </figure>
          <PipeArrow />
          <figure className="flex flex-col gap-3 rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-bg)] p-5">
            <Caption label="In your assistant" meta="plain language" />
            <blockquote className="text-[17px] font-medium leading-relaxed text-[var(--lp-ink)]">
              {"“Contoso CRM exited with 60001, a code the app has no mapping for, so the ESP stopped. The retry an hour later installed cleanly.”"}
            </blockquote>
          </figure>
        </div>

        <ul className="mt-14 grid grid-cols-1 gap-x-10 gap-y-7 sm:grid-cols-2 lg:grid-cols-3">
          {DOMAINS.map(domain => (
            <li key={domain.title} className="flex items-start gap-3">
              <CheckChip />
              <div>
                <h3 className="text-[15px] font-semibold text-[var(--lp-ink)]">{domain.title}</h3>
                <p className="mt-1 text-sm leading-relaxed text-[var(--lp-ink-soft)]">{domain.text}</p>
              </div>
            </li>
          ))}
        </ul>
      </div>
    </section>
  );
}
