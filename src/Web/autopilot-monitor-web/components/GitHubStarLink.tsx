import { GITHUB_REPO_URL, GITHUB_STARS, formatStarCount } from "@/utils/githubStars";
import { GitHubIcon } from "./GitHubIcon";

/**
 * "Star us" pill for the public surface: [GitHub mark] label │ ★ count.
 * The counter segment only renders when the deploy baked a star count in (utils/githubStars.ts).
 * No hooks, so server components (Testimonials) and client components (LandingNavbar) share it.
 */
export function GitHubStarLink({ label }: { label: string }) {
  return (
    <a
      href={GITHUB_REPO_URL}
      target="_blank"
      rel="noopener noreferrer"
      aria-label={
        GITHUB_STARS !== null
          ? `Star Autopilot Monitor on GitHub (${GITHUB_STARS} stars)`
          : "Star Autopilot Monitor on GitHub"
      }
      className="inline-flex items-stretch h-8 rounded-lg border border-[var(--lp-line)] bg-[var(--lp-surface)] text-[13px] font-medium text-[var(--lp-ink-soft)] hover:text-[var(--lp-ink)] hover:border-[var(--lp-ink-faint)] transition-colors overflow-hidden whitespace-nowrap"
    >
      <span className="inline-flex items-center gap-1.5 px-2.5">
        <GitHubIcon className="w-[15px] h-[15px]" />
        {label}
      </span>
      {GITHUB_STARS !== null && (
        <span className="inline-flex items-center gap-1 px-2.5 border-l border-[var(--lp-line)] bg-[var(--lp-surface-2)] font-semibold text-[var(--lp-ink)]">
          <svg className="w-3 h-3 text-[var(--lp-star)]" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
            <path d="M12 2.5l2.94 5.96 6.56.95-4.75 4.63 1.12 6.54L12 17.5l-5.87 3.08 1.12-6.54L2.5 9.41l6.56-.95z" />
          </svg>
          {formatStarCount(GITHUB_STARS)}
        </span>
      )}
    </a>
  );
}
