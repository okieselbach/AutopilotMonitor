import { Reveal } from "./Reveal";
import { PushWatchDemo } from "./PushWatchDemo";
import { ArrowRightIcon, CheckChip } from "../ai/icons";
import { DOCS_URL } from "@/lib/config";
import { DOCS_PATHS } from "@/lib/docsPaths";

/**
 * Customer-facing claims: each one holds against the push channel's code (per-device RFC 8291
 * encryption, sign-in-free receiver paired from the portal, lock-screen-only payload).
 */
const POINTS = [
  {
    title: "Encrypted end to end",
    text: "Every alert is encrypted for exactly one paired device. Apple, Google, Microsoft and Mozilla only pass the ciphertext along.",
  },
  {
    title: "Works on locked-down phones",
    text: "You pair the phone once by QR code from your signed-in portal. The phone itself never signs in, so it gets your alerts even where Conditional Access keeps it out of the portal.",
  },
  {
    title: "Only what fits a lock screen",
    text: "What happened and on which device, with the serial masked. One tap opens the details, behind your sign-in.",
  },
];

/**
 * Push notifications: the Story ends with "no dashboard-watching required", this section hands the
 * enrollment to the admin's phone. Wide: text left, the demo spanning both rows on the right.
 */
export function PushSection() {
  return (
    <section
      id="push"
      data-track-section="push"
      className="py-20 sm:py-24 px-6 scroll-mt-20 border-t border-[var(--lp-line-soft)]"
    >
      <div className="max-w-7xl mx-auto grid gap-10 lg:grid-cols-[minmax(0,5fr)_minmax(0,7fr)] lg:grid-rows-[auto_1fr] lg:gap-x-14 lg:gap-y-8">
        <Reveal className="min-w-0">
          <p className="text-xs font-semibold uppercase tracking-[0.22em] text-[var(--lp-accent-ink)]">Push notifications</p>
          <h2 className="mt-3 text-3xl sm:text-4xl font-bold tracking-tight text-[var(--lp-ink)] text-balance">
            Watch it live. Or walk away.
          </h2>
          <p className="mt-4 text-[16px] text-[var(--lp-ink-soft)] leading-relaxed max-w-xl">
            Click{" "}
            <span className="font-semibold text-[var(--lp-ink)]">Notify me when done</span>{" "}
            on a running enrollment and close the laptop. When it finishes, your phone tells you — encrypted end to end,
            no app store app, no Teams or Slack setup.
          </p>
        </Reveal>

        <Reveal delayMs={120} className="min-w-0 lg:col-start-2 lg:row-start-1 lg:row-span-2 lg:self-center">
          <PushWatchDemo />
        </Reveal>

        <Reveal className="min-w-0 lg:col-start-1 lg:row-start-2">
          <ul className="space-y-5">
            {POINTS.map(point => (
              <li key={point.title} className="flex items-start gap-3">
                <CheckChip />
                <div>
                  <h3 className="text-[15px] font-semibold text-[var(--lp-ink)]">{point.title}</h3>
                  <p className="mt-1 text-sm leading-relaxed text-[var(--lp-ink-soft)]">{point.text}</p>
                </div>
              </li>
            ))}
          </ul>
          <a
            href={`${DOCS_URL}${DOCS_PATHS.pushNotifications}`}
            target="_blank"
            rel="noopener noreferrer"
            data-track="push_docs"
            className="mt-6 inline-flex items-center gap-1.5 text-[15px] font-semibold text-[var(--lp-accent-ink)] hover:opacity-80 transition-opacity"
          >
            How push notifications work
            <ArrowRightIcon className="w-4 h-4" />
          </a>
        </Reveal>
      </div>
    </section>
  );
}
