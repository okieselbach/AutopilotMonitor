import { DOCS_URL } from "@/utils/config";

export function FinalCta() {
  return (
    <section className="py-20 sm:py-24 px-6 border-t border-[var(--lp-line-soft)]">
      <div className="max-w-7xl mx-auto flex flex-col lg:flex-row lg:items-center lg:justify-between gap-8">
        <h2 className="text-3xl sm:text-4xl font-bold tracking-tight text-[var(--lp-ink)] max-w-2xl text-balance">
          Your next enrollment doesn&apos;t have to be a black box.
        </h2>
        <div className="shrink-0 lg:text-right">
          <a
            href="/get-started"
            className="inline-block px-7 py-3 rounded-lg bg-[var(--lp-accent-ink)] hover:brightness-110 hover:shadow-lg text-white font-semibold shadow-md transition-all"
          >
            Start monitoring now
          </a>
          <p className="mt-3 text-sm text-[var(--lp-ink-faint)] lg:text-right">
            Free, open source, deployed in minutes.
            <span className="block mt-0.5">
              Need SLAs, support, or MSP delegation?{" "}
              <a
                href={`${DOCS_URL}/plans`}
                target="_blank"
                rel="noopener noreferrer"
                className="text-[var(--lp-accent-ink)] hover:opacity-80 underline"
              >
                There&apos;s an Enterprise plan
              </a>
              .
            </span>
          </p>
        </div>
      </div>
    </section>
  );
}
