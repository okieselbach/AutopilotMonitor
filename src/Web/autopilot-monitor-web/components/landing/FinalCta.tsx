import { LoginButton } from "./LoginButton";

export function FinalCta() {
  return (
    <section className="py-20 sm:py-24 px-6 border-t border-[var(--lp-line-soft)]">
      <div className="max-w-7xl mx-auto flex flex-col lg:flex-row lg:items-center lg:justify-between gap-8">
        <h2 className="text-3xl sm:text-4xl font-bold tracking-tight text-[var(--lp-ink)] max-w-2xl text-balance">
          Your next enrollment doesn&apos;t have to be a black box.
        </h2>
        <div className="shrink-0">
          <LoginButton className="px-7 py-3 rounded-lg bg-[var(--lp-accent-ink)] hover:brightness-110 hover:shadow-lg text-white font-semibold shadow-md transition-all">
            Start monitoring now
          </LoginButton>
          <p className="mt-3 text-sm text-[var(--lp-ink-faint)] lg:text-right">
            Free, open source, deployed in minutes.
          </p>
        </div>
      </div>
    </section>
  );
}
