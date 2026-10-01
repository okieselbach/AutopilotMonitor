import { GitHubStarLink } from "../GitHubStarLink";
import { Reveal } from "./Reveal";

/**
 * Customer voice before the closing CTA: one quote, shared with permission and attributed by
 * role only. More voices need a different layout (a grid of bubbles), not copies of this one.
 */
export function Testimonials() {
  return (
    <section className="py-20 sm:py-24 px-6 border-t border-[var(--lp-line-soft)] bg-[var(--lp-surface)]">
      <div className="max-w-7xl mx-auto grid lg:grid-cols-12 gap-10 lg:gap-6">
        <Reveal className="lg:col-span-4">
          <p className="text-xs font-semibold uppercase tracking-[0.22em] text-[var(--lp-accent-ink)]">From the field</p>
          <h2 className="mt-3 text-3xl sm:text-4xl font-bold tracking-tight text-[var(--lp-ink)] text-balance">
            What endpoint teams see on day one.
          </h2>
        </Reveal>

        <Reveal className="lg:col-span-8" delayMs={100}>
          <figure>
            <div className="relative rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-bg)] px-6 pt-7 pb-8 sm:px-10 sm:pt-9 sm:pb-10">
              <span aria-hidden="true" className="block h-6 sm:h-8 font-serif text-6xl sm:text-7xl leading-none text-[var(--lp-accent)]">
                &ldquo;
              </span>
              <blockquote className="mt-3 text-2xl sm:text-3xl font-semibold tracking-tight leading-snug text-[var(--lp-ink)] text-balance">
                Wow — for the first time I can actually see all of our enrollments running out there.
              </blockquote>
              {/* Speech-bubble tail: the upper half of the rotated square covers the bubble border */}
              <span
                aria-hidden="true"
                className="absolute left-10 -bottom-[9px] w-4 h-4 rotate-45 bg-[var(--lp-bg)] border-r border-b border-[var(--lp-line)]"
              />
            </div>
            <figcaption className="mt-5 flex items-center gap-3.5 pl-6 sm:pl-8">
              <span
                aria-hidden="true"
                className="inline-flex w-11 h-11 shrink-0 items-center justify-center rounded-full bg-[var(--lp-accent-soft)] text-[var(--lp-accent-ink)]"
              >
                <svg className="w-5 h-5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
                  <path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2" />
                  <circle cx="12" cy="7" r="4" />
                </svg>
              </span>
              <span className="text-[15px] font-semibold text-[var(--lp-ink)]">Head of Endpoint Management</span>
            </figcaption>
          </figure>

          <div className="mt-8 flex flex-wrap items-center gap-x-4 gap-y-3">
            <p className="text-[15px] text-[var(--lp-ink-soft)]">Seeing the same in your tenant? Leave a star.</p>
            <GitHubStarLink label="Star on GitHub" />
          </div>
        </Reveal>
      </div>
    </section>
  );
}
