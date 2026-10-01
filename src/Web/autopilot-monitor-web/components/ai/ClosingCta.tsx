import Link from "next/link";

export function ClosingCta() {
  return (
    <section data-track-section="closing" className="px-6 py-20 sm:py-24">
      <div className="mx-auto flex max-w-7xl flex-col justify-between gap-8 lg:flex-row lg:items-center">
        <h2 className="max-w-2xl text-balance text-3xl font-bold tracking-tight text-[var(--lp-ink)] sm:text-4xl">
          Your enrollment data has the answers. Start asking.
        </h2>
        <div className="lg:text-right">
          <Link
            href="/get-started"
            prefetch={false}
            data-track="closing_get_started"
            className="inline-block rounded-lg bg-[var(--lp-accent-ink)] px-7 py-3 font-semibold text-white shadow-md transition-all hover:shadow-lg hover:brightness-110"
          >
            Get started
          </Link>
          <p className="mt-3 text-sm leading-relaxed text-[var(--lp-ink-faint)]">
            Free, open source, deployed in minutes.
            <br />
            {"Need SLAs, support, or MSP delegation? "}
            <a href="/plans/" data-track="closing_pro_plan" className="text-[var(--lp-accent-ink)] underline hover:opacity-80">
              There&apos;s a Pro plan
            </a>
            .
          </p>
        </div>
      </div>
    </section>
  );
}
