import Link from "next/link";
import { LandingNavbar } from "../../components/landing/LandingNavbar";
import { SiteFooter } from "../../components/SiteFooter";

/**
 * Purchase page for Pro. Linked from the public /plans page and from the
 * portal's Plan section (absolute www URL — /buy is public surface).
 *
 * Both purchase channels are honest "coming soon" placeholders until the
 * checkout links exist. When they do: the provider host must be registered in
 * utils/config.ts, the CSP in staticwebapp.config.json, the swaConfig guard
 * test, and the dev CSP in next.config.ts — see tasks/todo.md follow-up.
 */
const CHANNELS = [
  {
    label: "Microsoft Marketplace",
    description:
      "Purchase through the Microsoft commercial marketplace using your organization's existing Microsoft billing relationship.",
  },
  {
    label: "Cleverbridge",
    description:
      "Buy Pro online with a credit card or on invoice — a fast, self-service checkout for a single organization.",
  },
];

export default function BuyPage() {
  return (
    <div className="landing-v2 min-h-screen bg-[var(--lp-bg)]">
      <LandingNavbar />
      <header className="px-4 sm:px-6 lg:px-8 pt-14 sm:pt-16">
        <div className="max-w-5xl mx-auto">
          <p className="text-[11px] font-semibold uppercase tracking-[0.24em] text-[var(--lp-ink-faint)]">Pro</p>
          <h1 className="mt-3 text-3xl sm:text-4xl font-bold tracking-tight text-[var(--lp-ink)]">
            Get Autopilot Monitor Pro
          </h1>
          <p className="mt-4 max-w-2xl text-[15px] text-gray-600 leading-relaxed">
            Pro will be available through two purchase channels. Neither is open yet — pricing
            and availability will be announced. Until then, Community is the full product, free
            for everyone.
          </p>
        </div>
      </header>

      <main className="max-w-5xl mx-auto px-4 sm:px-6 lg:px-8 py-10 space-y-10 text-[15px]">
        {/* Purchase channels */}
        <section className="grid grid-cols-1 sm:grid-cols-2 gap-5">
          {CHANNELS.map((channel) => (
            <div key={channel.label} className="bg-[var(--lp-surface)] border border-[var(--lp-line-soft)] rounded-xl p-8 flex flex-col">
              <h2 className="text-lg font-bold text-gray-900 mb-3">{channel.label}</h2>
              <p className="text-sm text-gray-600 leading-relaxed mb-4 flex-1">{channel.description}</p>
              <button
                type="button"
                disabled
                title="Available once Pro pricing is announced."
                className="w-full rounded-lg border border-[var(--lp-line-soft)] bg-[var(--lp-surface-2)] px-4 py-2.5 text-sm font-semibold text-[var(--lp-ink-faint)] cursor-not-allowed"
              >
                Coming soon
              </button>
            </div>
          ))}
        </section>

        {/* Interim path */}
        <section className="bg-[var(--lp-surface)] border border-[var(--lp-line-soft)] rounded-xl p-8">
          <h2 className="text-xl font-bold text-gray-900 mb-4">Questions About Pro?</h2>
          <p className="text-gray-700 leading-relaxed">
            Interested in Pro for your organization or an MSP scenario, or want to know when it
            opens? Reach out through any channel on the{" "}
            <Link href="/help" className="text-[var(--lp-accent-ink)] hover:opacity-80 underline">
              Help &amp; Support
            </Link>{" "}
            page — you&apos;ll talk directly to the person building the platform. And if you
            haven&apos;t compared the plans yet, the{" "}
            <Link href="/plans" className="text-[var(--lp-accent-ink)] hover:opacity-80 underline">
              plans overview
            </Link>{" "}
            shows exactly what Pro adds.
          </p>
        </section>
      </main>
      <SiteFooter />
    </div>
  );
}
