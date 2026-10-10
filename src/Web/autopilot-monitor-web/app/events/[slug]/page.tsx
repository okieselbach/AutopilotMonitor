import type { Metadata } from "next";
import type { ReactNode } from "react";
import Link from "next/link";
import { notFound } from "next/navigation";
import { BrandMark } from "@/components/BrandMark";
import { GitHubStarLink } from "@/components/GitHubStarLink";
import { SiteFooter } from "@/components/SiteFooter";
import { CUSTOMER_QUOTE } from "@/components/landing/customerQuote";
import { DOCS_URL, sitePageUrl } from "@/lib/config";
import {
  COMMUNITY_COMPANIONS,
  EVENT_PAGE_ROBOTS,
  FOLLOWUP_EVENTS,
  WALKTHROUGH_EMAIL,
  findFollowupEvent,
  walkthroughMailto,
  type EventArt,
} from "../events";
import { EventClickTracker } from "./EventClickTracker";

// Static export: prerender exactly the listed events; any other slug is a build-time 404.
export const dynamicParams = false;

export function generateStaticParams() {
  return FOLLOWUP_EVENTS.map((event) => ({ slug: event.slug }));
}

type PageProps = { params: Promise<{ slug: string }> };

export async function generateMetadata({ params }: PageProps): Promise<Metadata> {
  const event = findFollowupEvent((await params).slug);
  if (!event) return {};
  const url = sitePageUrl(`/events/${event.slug}`);
  const description = `Your follow-up from ${event.name}: try Autopilot Monitor for free, get a walkthrough, or read the docs.`;
  return {
    title: event.name,
    description,
    robots: EVENT_PAGE_ROBOTS,
    openGraph: { title: `${event.name} – Autopilot Monitor`, description, url },
    alternates: { canonical: url },
  };
}

const PRIMARY_BUTTON =
  "inline-flex items-center justify-center min-h-[44px] px-5 rounded-lg bg-[var(--lp-accent-ink)] hover:brightness-110 text-white text-[15px] font-semibold shadow-sm transition-all";
const SECONDARY_BUTTON =
  "inline-flex items-center justify-center min-h-[44px] px-5 rounded-lg border border-[var(--lp-line)] bg-[var(--lp-surface)] hover:bg-[var(--lp-surface-2)] text-[var(--lp-ink)] text-[15px] font-semibold transition-colors";

export default async function EventFollowupPage({ params }: PageProps) {
  const event = findFollowupEvent((await params).slug);
  if (!event) notFound();

  return (
    <div className="landing-v2 min-h-screen bg-[var(--lp-bg)] flex flex-col">
      <EventClickTracker event={event.slug} className="flex-1 flex flex-col">
        <header className="border-b border-[var(--lp-line-soft)] bg-[var(--lp-surface)]">
          <div className="max-w-6xl mx-auto px-4 sm:px-6 h-16 flex items-center justify-between gap-4">
            <Link href="/" prefetch={false} data-track="logo" className="flex items-center gap-2.5">
              <BrandMark className="w-6 h-6" />
              <span className="text-[15px] font-bold tracking-tight text-[var(--lp-ink)] whitespace-nowrap">
                Autopilot Monitor
              </span>
            </Link>
            <Link
              href="/get-started"
              prefetch={false}
              data-track="header_get_started"
              className="px-4 py-2 rounded-lg bg-[var(--lp-accent-ink)] hover:brightness-110 text-white text-sm font-semibold shadow-sm transition-all whitespace-nowrap"
            >
              Get started
            </Link>
          </div>
        </header>

        <main className="flex-1">
          <section className="px-4 sm:px-6 pt-12 sm:pt-16 pb-10">
            <div className="max-w-6xl mx-auto flex items-center gap-6">
              <div className="flex-1 min-w-0">
                <p className="text-xs font-semibold uppercase tracking-[0.22em] text-[var(--lp-accent-ink)]">{event.name}</p>
                <h1 className="mt-4 text-[34px] sm:text-5xl lg:text-6xl font-bold tracking-tight leading-[1.05] text-[var(--lp-ink)] text-balance">
                  Thanks for stopping by.
                </h1>
                <p className="mt-4 max-w-xl text-[17px] sm:text-xl leading-relaxed text-[var(--lp-ink-soft)]">
                  Three ways to keep going. Pick the one that fits your week.
                </p>
              </div>
              {event.art && <ArtImage art={event.art.hero} className="w-[104px] sm:w-[180px] lg:w-[250px]" />}
            </div>
          </section>

          <section className="px-4 sm:px-6 pb-16 sm:pb-20">
            <div className="max-w-6xl mx-auto grid gap-5 md:grid-cols-3">
              <ActionCard icon={<ArrowIcon />} title="Try it today">
                <p className={CARD_TEXT}>
                  The Community plan is the full product, free. Sign in, assign one script in Intune, and watch your first
                  enrollment live.
                </p>
                <Link href="/get-started" prefetch={false} data-track="get_started" className={PRIMARY_BUTTON}>
                  Get started
                </Link>
              </ActionCard>

              <ActionCard icon={<MailIcon />} title="Get a walkthrough">
                <p className={CARD_TEXT}>
                  30 minutes with {event.walkthroughWith}. Your scenarios, your questions.
                </p>
                <a href={walkthroughMailto(event)} data-track="walkthrough_email" className={SECONDARY_BUTTON}>
                  Request a walkthrough
                </a>
                {/* mailto: does nothing without a mail client, so the address stays readable and copyable */}
                <span className="-mt-1 text-center text-[13px] text-[var(--lp-ink-soft)] select-all">{WALKTHROUGH_EMAIL}</span>
              </ActionCard>

              <ActionCard icon={<BookIcon />} title="Read the docs">
                <p className={CARD_TEXT}>
                  From the first deployment to analyze rules and connecting your AI assistant, step by step.
                </p>
                <a href={DOCS_URL} target="_blank" rel="noopener noreferrer" data-track="docs" className={SECONDARY_BUTTON}>
                  Read the docs
                </a>
              </ActionCard>
            </div>
          </section>

          <section className="px-4 sm:px-6 py-14 bg-[var(--lp-surface)] border-y border-[var(--lp-line-soft)]">
            <div className="max-w-6xl mx-auto flex flex-wrap items-center justify-between gap-x-12 gap-y-8">
              <figure className="flex-1 basis-[420px] min-w-0 max-w-2xl">
                <blockquote className="text-2xl sm:text-[28px] font-semibold tracking-tight leading-snug text-[var(--lp-ink)] text-balance">
                  &ldquo;{CUSTOMER_QUOTE.text}&rdquo;
                </blockquote>
                <figcaption className="mt-3 text-sm text-[var(--lp-ink-soft)]">{CUSTOMER_QUOTE.attribution}</figcaption>
              </figure>
              <div>
                <p className="text-sm text-[var(--lp-ink-soft)]">Seeing the same in your tenant? Leave a star.</p>
                <div className="mt-2.5 flex items-start gap-1.5">
                  {event.art && <ArtImage art={event.art.starPointer} className="w-[58px] sm:w-[72px]" />}
                  {/* The offset puts the button's middle on the pointing finger at both art widths */}
                  <span data-track="github_star" className={event.art ? "sm:mt-[5px]" : undefined}>
                    <GitHubStarLink label="Star on GitHub" />
                  </span>
                </div>
              </div>
            </div>
          </section>

          <section className="px-4 sm:px-6 py-14">
            <div className="max-w-6xl mx-auto">
              <h2 className="text-2xl font-bold tracking-tight text-[var(--lp-ink)]">Looking for other cool Intune companions?</h2>
              <p className="mt-2 text-[15px] text-[var(--lp-ink-soft)]">Check out these free community editions as well.</p>
              <div className="mt-6 grid gap-4 sm:grid-cols-2">
                {COMMUNITY_COMPANIONS.map((companion) => (
                  <a
                    key={companion.id}
                    href={companion.href}
                    target="_blank"
                    rel="noopener noreferrer"
                    data-track={companion.id}
                    className="flex items-center gap-4 rounded-xl border border-[var(--lp-line)] bg-[var(--lp-surface)] px-5 py-4 hover:border-[var(--lp-accent-line)] transition-colors"
                  >
                    <span className="flex-1 min-w-0">
                      <span className="block font-semibold text-[var(--lp-ink)]">{companion.name}</span>
                      <span className="mt-1 block text-sm leading-relaxed text-[var(--lp-ink-soft)]">{companion.description}</span>
                    </span>
                    <svg className="w-[18px] h-[18px] shrink-0 text-[var(--lp-accent-ink)]" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                      <path d="M7 17L17 7M9 7h8v8" />
                    </svg>
                  </a>
                ))}
              </div>
            </div>
          </section>
        </main>
      </EventClickTracker>
      <SiteFooter />
    </div>
  );
}

const CARD_TEXT = "flex-1 text-[15px] leading-relaxed text-[var(--lp-ink-soft)]";

function ActionCard({ icon, title, children }: { icon: ReactNode; title: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-3.5 rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-surface)] p-7 shadow-xl shadow-black/[0.04]">
      <span className="inline-flex w-10 h-10 items-center justify-center rounded-[10px] bg-[var(--lp-accent-soft)] text-[var(--lp-accent-ink)]">
        {icon}
      </span>
      <h2 className="text-xl font-bold text-[var(--lp-ink)]">{title}</h2>
      {children}
    </div>
  );
}

/** Event artwork is decorative: the eyebrow already names the event. */
function ArtImage({ art, className }: { art: EventArt; className: string }) {
  return (
    // Static export: next/image is not configured, plain img is intentional.
    // eslint-disable-next-line @next/next/no-img-element
    <img src={art.src} width={art.width} height={art.height} alt="" decoding="async" className={`${className} h-auto shrink-0`} />
  );
}

function CardIcon({ children }: { children: ReactNode }) {
  return (
    <svg className="w-5 h-5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      {children}
    </svg>
  );
}

function ArrowIcon() {
  return (
    <CardIcon>
      <path d="M5 12h14M13 6l6 6-6 6" />
    </CardIcon>
  );
}

function MailIcon() {
  return (
    <CardIcon>
      <rect x="3" y="5" width="18" height="14" rx="2" />
      <path d="M3 7l9 6 9-6" />
    </CardIcon>
  );
}

function BookIcon() {
  return (
    <CardIcon>
      <path d="M4 5a2 2 0 0 1 2-2h13v16H6a2 2 0 0 0-2 2z" />
      <path d="M4 21V5M8 7h7" />
    </CardIcon>
  );
}
