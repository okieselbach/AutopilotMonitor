import Link from "next/link";
import { LandingNavbar } from "../../components/landing/LandingNavbar";
import { SiteFooter } from "../../components/SiteFooter";
import { DOCS_URL } from "@/utils/config";

/**
 * Purchase page for Pro: hands the buyer off to one of the two purchase channels. Linked from
 * the public /plans page and from the portal's Plan section (absolute www URL — /buy is public
 * surface). It names no price on purpose: the plan cards carry the list price, the channel's
 * checkout the binding one. Plain outbound links, so neither host needs a CSP entry.
 */
const PURCHASE_GUIDE_URL = `${DOCS_URL}/troubleshooting-and-support/how-to-purchase`;

const CHANNELS = [
  {
    label: "Microsoft Marketplace",
    description:
      "Subscribe through the Microsoft commercial marketplace and pay through your organization's existing Azure billing.",
    href: "https://marketplace.microsoft.com/en-us/product/saas/glueckkanja-gabag.autopilot-monitor-transactable-prod?tab=Overview",
    cta: "Buy on Microsoft Marketplace",
    guide: `${PURCHASE_GUIDE_URL}/microsoft-marketplace`,
  },
  {
    label: "Cleverbridge",
    description:
      "Buy Pro online by credit card, PayPal or bank transfer — a fast, self-service checkout for a single organization.",
    href: "https://www.cleverbridge.com/306/purl-Autopilot-Monitor-Buy-Y",
    cta: "Buy with Cleverbridge",
    guide: `${PURCHASE_GUIDE_URL}/cleverbridge`,
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
            Pick the channel that fits how your organization buys software. Both lead to the same
            Pro plan on your existing tenant — your data stays where it is.
          </p>
        </div>
      </header>

      <main className="max-w-5xl mx-auto px-4 sm:px-6 lg:px-8 py-10 space-y-10 text-[15px]">
        {/* Purchase channels */}
        <section className="grid grid-cols-1 sm:grid-cols-2 gap-5">
          {CHANNELS.map((channel) => (
            <div key={channel.label} className="bg-[var(--lp-surface)] border border-[var(--lp-line-soft)] rounded-xl p-8 flex flex-col">
              <h2 className="text-lg font-bold text-gray-900 mb-3">{channel.label}</h2>
              <p className="text-sm text-gray-600 leading-relaxed mb-5 flex-1">{channel.description}</p>
              <a
                href={channel.href}
                target="_blank"
                rel="noopener noreferrer"
                className="block w-full text-center text-sm font-semibold text-white bg-purple-600 rounded-lg px-4 py-2.5 hover:bg-purple-700 transition-colors"
              >
                {channel.cta}
              </a>
              <a
                href={channel.guide}
                target="_blank"
                rel="noopener noreferrer"
                className="mt-3 text-center text-xs text-gray-600 hover:text-gray-900 underline"
              >
                Purchase guide: prerequisites, payment and cancellation
              </a>
            </div>
          ))}
        </section>

        {/* Trial + questions */}
        <section className="bg-[var(--lp-surface)] border border-[var(--lp-line-soft)] rounded-xl p-8">
          <h2 className="text-xl font-bold text-gray-900 mb-4">Try Pro First</h2>
          <p className="text-gray-700 leading-relaxed">
            A tenant administrator can start a one-time, free 30-day Pro trial in the portal under
            Settings → Tenant → Plan. When it ends, the tenant returns to Community automatically.
            Questions about Pro or an MSP scenario? Reach out through any channel on the{" "}
            <Link href="/help" className="text-[var(--lp-accent-ink)] hover:opacity-80 underline">
              Help &amp; Support
            </Link>{" "}
            page, or compare the plans in the{" "}
            <Link href="/plans" className="text-[var(--lp-accent-ink)] hover:opacity-80 underline">
              plans overview
            </Link>
            .
          </p>
        </section>
      </main>
      <SiteFooter />
    </div>
  );
}
