import { Reveal } from "./Reveal";

const STEPS = [
  {
    title: "Sign in & grant access",
    description: "Authenticate with Microsoft and approve tenant access once.",
  },
  {
    title: "Deploy the bootstrapper via Intune",
    description: "Assign one PowerShell script to your Autopilot scope. That's the whole rollout.",
  },
  {
    title: "Watch your first enrollment live",
    description: "Phases, apps, failures, and diagnostics stream in minutes later.",
  },
];

export function HowItWorks() {
  return (
    <section id="how-it-works" className="py-20 sm:py-24 px-6 scroll-mt-20 border-t border-[var(--lp-line-soft)] bg-[var(--lp-surface)]">
      <div className="max-w-5xl mx-auto">
        <Reveal className="max-w-2xl">
          <p className="text-xs font-semibold uppercase tracking-[0.22em] text-[var(--lp-accent-ink)]">How it works</p>
          <h2 className="mt-3 text-3xl sm:text-4xl font-bold tracking-tight text-[var(--lp-ink)]">
            Live in three steps. No infrastructure on your side.
          </h2>
        </Reveal>

        <div className="mt-10 grid md:grid-cols-3 gap-6">
          {STEPS.map((step, i) => (
            <Reveal key={step.title} delayMs={i * 100}>
              <div className="relative rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-bg)] p-5 h-full">
                <span className="inline-flex w-8 h-8 items-center justify-center rounded-lg bg-[var(--lp-accent)] text-white text-sm font-bold">
                  {i + 1}
                </span>
                <h3 className="mt-3.5 text-[15px] font-semibold text-[var(--lp-ink)]">{step.title}</h3>
                <p className="mt-1.5 text-sm text-[var(--lp-ink-soft)] leading-relaxed">{step.description}</p>
              </div>
            </Reveal>
          ))}
        </div>
      </div>
    </section>
  );
}
