"use client";

import { useEffect, useRef, useState } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { ModalPortal } from "./ModalPortal";
import { useAuth } from "@/contexts/AuthContext";
import { useWhatsNew } from "@/hooks/useWhatsNew";
import { useLatestVersions } from "@/lib/useLatestVersions";
import { closeWhatsNew } from "@/lib/whatsNewStore";
import { formatInlineMarkdown } from "@/lib/formatInlineMarkdown";
import { isPublicPath } from "@/lib/hostRouting";
import { route } from "@/lib/routes";
import { trackEvent } from "@/lib/appInsights";
import {
  formatBadgeCount,
  unseenEntries,
  WHATS_NEW_CHANNELS,
  type WhatsNewChannel,
  type WhatsNewEntry,
  type WhatsNewSeen,
} from "@/lib/whatsNew";

const CHANNEL_LABEL: Record<WhatsNewChannel, string> = { platform: "Platform", agent: "Agent" };

/** Tenant settings section where a channel's "What's new" toggle lives. */
const NOTIFICATION_SETTINGS_HREF = route("/settings/tenant/notifications");

/**
 * The red unread counter used on the navbar icons, the menu rows and the panel tabs —
 * same size and cap as the notification bell's badge.
 */
export function WhatsNewCountBadge({ count, className = "" }: { count: number; className?: string }) {
  if (count <= 0) return null;
  return (
    <span
      className={`inline-flex items-center justify-center min-w-4 h-4 px-1 text-[10px] font-bold leading-none text-white bg-red-600 rounded-full ${className}`}
      aria-label={`${count} new`}
    >
      {formatBadgeCount(count)}
    </span>
  );
}

/** The badge pinned to the top-right corner of an icon button. */
export function WhatsNewIconBadge({ count }: { count: number }) {
  if (count <= 0) return null;
  return <WhatsNewCountBadge count={count} className="absolute top-0.5 right-0.5" />;
}

function BellIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
      <path d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9" />
    </svg>
  );
}

function ExternalIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
      <path d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" />
    </svg>
  );
}

const dateFormatter = new Intl.DateTimeFormat("en-US", { month: "short", day: "numeric", year: "numeric" });

const MARKDOWN_OPTIONS = { links: true, emphasis: true, linkClassName: "text-[var(--lp-accent-ink)] hover:underline" } as const;

function EntryRow({ entry, isNew }: { entry: WhatsNewEntry; isNew: boolean }) {
  const onLink = () => trackEvent("whats_new_link_clicked", { entryId: entry.id });
  return (
    <li className="py-4 border-b border-[var(--lp-line-soft)] last:border-b-0">
      <div className="flex items-center gap-2 text-xs text-[var(--lp-ink-faint)]">
        <time dateTime={entry.addedUtc}>{dateFormatter.format(new Date(entry.addedUtc))}</time>
        {isNew && (
          <span className="inline-flex items-center gap-1 rounded-full bg-[var(--lp-accent-soft)] px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-[var(--lp-accent-ink)]">
            New
          </span>
        )}
      </div>
      {entry.title ? (
        <>
          <h3 className="mt-1 text-[15px] font-semibold leading-snug text-[var(--lp-ink)]">{formatInlineMarkdown(entry.title, { emphasis: true })}</h3>
          <p className="mt-1.5 text-sm leading-relaxed text-[var(--lp-ink-soft)]">
            {formatInlineMarkdown(entry.body, MARKDOWN_OPTIONS)}
          </p>
        </>
      ) : (
        <p className="mt-1 text-[15px] leading-relaxed text-[var(--lp-ink)]">
          {formatInlineMarkdown(entry.body, MARKDOWN_OPTIONS)}
        </p>
      )}
      {entry.link && (
        <a
          href={entry.link}
          target="_blank"
          rel="noopener noreferrer"
          onClick={onLink}
          className="mt-2 inline-flex items-center gap-1 text-sm font-semibold text-[var(--lp-accent-ink)] hover:underline"
        >
          Read update
          <ExternalIcon className="w-3.5 h-3.5" />
        </a>
      )}
    </li>
  );
}

/**
 * The currently published agent version, quiet under the header line. Rendered only for
 * signed-in portal users (the endpoint is AuthenticatedUser, and the public site has no
 * session list to compare against), so the mount itself is the fetch gate. Same plain,
 * monospaced string as the "Agent Version" column of the session list — that match is the
 * whole point: the reader sees at a glance whether their fleet runs the latest agent.
 */
function LatestAgentVersionLine() {
  const { getAccessToken } = useAuth();
  const { latestAgentVersion } = useLatestVersions(getAccessToken);
  if (!latestAgentVersion) return null;
  return (
    <p className="mt-1 text-xs text-[var(--lp-ink-faint)]">
      Latest agent version <span className="font-mono">{latestAgentVersion}</span>
    </p>
  );
}

function SkeletonRows() {
  return (
    <ul className="animate-pulse" aria-hidden="true">
      {[0, 1, 2].map(i => (
        <li key={i} className="py-4 border-b border-[var(--lp-line-soft)] last:border-b-0">
          <div className="h-3 w-20 rounded bg-[var(--lp-surface-2)]" />
          <div className="mt-2 h-4 w-3/4 rounded bg-[var(--lp-surface-2)]" />
          <div className="mt-2 h-3 w-full rounded bg-[var(--lp-surface-2)]" />
          <div className="mt-1 h-3 w-5/6 rounded bg-[var(--lp-surface-2)]" />
        </li>
      ))}
    </ul>
  );
}

/**
 * The What's new flyout: a floating card on the right, over the page. Rendered only while
 * open (the host unmounts it), so closing leaves nothing behind. Signed-in users see "New"
 * labels and tab counters against their server-side mark; anonymous visitors see the list
 * with dates only. Colors come from the landing tokens (`--lp-*`), which are defined globally and follow `.dark` in the portal;
 * the host passes `landing-v2` as the root class on the public site so the card stays
 * light there (the class must sit INSIDE the portal — CSS variables do not cross it).
 */
export function WhatsNewPanel({ rootClassName }: { rootClassName?: string }) {
  const wn = useWhatsNew();
  const { user } = useAuth();
  const pathname = usePathname();
  const closeRef = useRef<HTMLButtonElement>(null);
  // Marks as they were when the panel opened: the "New" labels must not vanish under the
  // reader's eyes when the channel is marked seen a moment later.
  const [seenAtOpen] = useState<WhatsNewSeen>(() => wn.seen);
  const [openedAt] = useState(() => new Date());

  // The portal shows things the public site cannot: /settings/tenant/… does not exist there,
  // and the latest-agent line needs a signed-in caller plus a session list to compare against.
  const onPortal = !isPublicPath(pathname);
  // Only whoever can actually flip the channel toggle gets the "subscribe" link.
  const canConfigureNotifications =
    wn.tracksUnseen && (user?.isTenantAdmin === true || user?.isGlobalAdmin === true) && onPortal;

  const channel = wn.panelChannel;
  const entries = wn.entries(channel);
  const newIds = new Set(wn.tracksUnseen ? unseenEntries(entries, seenAtOpen[channel], openedAt).map(e => e.id) : []);

  useEffect(() => {
    closeRef.current?.focus();
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") closeWhatsNew();
    };
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.body.style.overflow = previousOverflow;
      document.removeEventListener("keydown", onKeyDown);
    };
  }, []);

  // Viewing a tab marks it seen up to the newest loaded entry.
  const { status, markSeen } = wn;
  useEffect(() => {
    if (status !== "ready") return;
    void markSeen(channel);
  }, [status, channel, markSeen]);

  const switchChannel = (next: WhatsNewChannel) => {
    if (next === channel) return;
    trackEvent("whats_new_tab_changed", { channel: next });
    wn.setChannel(next);
  };

  const docsUrl = wn.docsUrl(channel);

  return (
    <ModalPortal>
      <div className={`fixed inset-0 z-[60] ${rootClassName ?? ""}`} role="presentation">
        <div className="absolute inset-0 bg-black/20 backdrop-blur-[2px]" onClick={wn.close} aria-hidden="true" />
        <div
          role="dialog"
          aria-modal="true"
          aria-labelledby="whats-new-title"
          className="whats-new-card absolute top-2 right-2 bottom-2 sm:top-4 sm:right-4 sm:bottom-4 w-[calc(100vw-1rem)] sm:w-[28rem] flex flex-col rounded-2xl border border-[var(--lp-line-soft)] bg-[var(--lp-surface)] shadow-2xl overflow-hidden"
        >
          <header className="px-5 pt-5 pb-4 border-b border-[var(--lp-line-soft)]">
            <div className="flex items-start gap-3">
              <span className="inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-[var(--lp-accent-soft)] text-[var(--lp-accent-ink)]">
                <BellIcon className="w-5 h-5" />
              </span>
              <div className="min-w-0 flex-1">
                <h2 id="whats-new-title" className="text-lg font-bold leading-tight text-[var(--lp-ink)]">What&apos;s new</h2>
                <p className="mt-0.5 text-sm text-[var(--lp-ink-soft)]">News and improvements from Autopilot Monitor.</p>
                {onPortal && wn.tracksUnseen && <LatestAgentVersionLine />}
              </div>
              <button
                ref={closeRef}
                type="button"
                onClick={wn.close}
                aria-label="Close"
                className="-mr-2 -mt-1 p-2 rounded-lg text-[var(--lp-ink-faint)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors"
              >
                <svg className="w-5 h-5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round">
                  <path d="M6 6l12 12M18 6L6 18" />
                </svg>
              </button>
            </div>

            <div className="mt-4 flex rounded-lg bg-[var(--lp-surface-2)] p-1" role="tablist" aria-label="Channel">
              {WHATS_NEW_CHANNELS.map(c => {
                const active = c === channel;
                return (
                  <button
                    key={c}
                    type="button"
                    role="tab"
                    aria-selected={active}
                    onClick={() => switchChannel(c)}
                    className={`flex-1 inline-flex items-center justify-center gap-1.5 rounded-md px-3 py-1.5 text-sm font-medium transition-colors ${
                      active
                        ? "bg-[var(--lp-surface)] text-[var(--lp-ink)] shadow-sm"
                        : "text-[var(--lp-ink-soft)] hover:text-[var(--lp-ink)]"
                    }`}
                  >
                    {CHANNEL_LABEL[c]}
                    <WhatsNewCountBadge count={wn.unseen[c]} />
                  </button>
                );
              })}
            </div>
          </header>

          <div className="flex-1 overflow-y-auto px-5" role="tabpanel">
            {wn.status === "loading" || wn.status === "idle" ? (
              <SkeletonRows />
            ) : wn.status === "error" || entries.length === 0 ? (
              <div className="py-8 text-center">
                <p className="text-sm text-[var(--lp-ink-soft)]">
                  {wn.status === "error" ? "The update list could not be loaded right now." : "No recent entries."}
                </p>
                <p className="mt-1 text-sm text-[var(--lp-ink-soft)]">The full changelog is always available in the documentation.</p>
              </div>
            ) : (
              <ul>
                {entries.map((entry, i) => {
                  const showPeriod = i === 0 || entries[i - 1].period !== entry.period;
                  return (
                    <li key={entry.id} className="list-none">
                      {showPeriod && (
                        <p className={`text-[11px] font-semibold uppercase tracking-[0.18em] text-[var(--lp-ink-faint)] ${i === 0 ? "pt-4" : "pt-6"}`}>
                          {entry.period}
                        </p>
                      )}
                      <ul>
                        <EntryRow entry={entry} isNew={newIds.has(entry.id)} />
                      </ul>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>

          <footer className="px-5 py-4 border-t border-[var(--lp-line-soft)] bg-[var(--lp-surface)]">
            {docsUrl && (
              <a
                href={docsUrl}
                target="_blank"
                rel="noopener noreferrer"
                onClick={() => trackEvent("whats_new_link_clicked", { entryId: `all-${channel}` })}
                className="flex w-full items-center justify-center gap-2 rounded-xl bg-[var(--lp-accent-ink)] px-4 py-2.5 text-sm font-semibold text-white hover:brightness-110 transition-all"
              >
                View all {CHANNEL_LABEL[channel].toLowerCase()} updates
                <ExternalIcon className="w-4 h-4" />
              </a>
            )}
            {canConfigureNotifications && (
              <p className="mt-3 text-center text-xs text-[var(--lp-ink-faint)]">
                <Link
                  href={NOTIFICATION_SETTINGS_HREF}
                  prefetch={false}
                  onClick={() => {
                    trackEvent("whats_new_notification_settings_clicked", { channel });
                    closeWhatsNew();
                  }}
                  className="inline-flex items-center gap-1 font-medium text-[var(--lp-ink-soft)] hover:text-[var(--lp-accent-ink)] hover:underline"
                >
                  <BellIcon className="w-3.5 h-3.5" />
                  Get these updates in Teams, Slack or Discord
                </Link>
              </p>
            )}
          </footer>
        </div>
      </div>
    </ModalPortal>
  );
}
