/**
 * Event follow-up pages (/events/<slug>/): unlisted pages reached only through the
 * follow-up email after an event. They stay out of the sitemap, robots.txt and llms.txt
 * and are marked noindex (events.guard.test.ts pins all four).
 */

export interface EventArt {
  src: string;
  width: number;
  height: number;
}

export interface FollowupEvent {
  /** URL segment: /events/<slug>/. It is in sent emails, so never rename or reuse one. */
  slug: string;
  /** The event's name as attendees know it. */
  name: string;
  /** Who runs the walkthrough: a person's name, or "us". */
  walkthroughWith: string;
  /** Event artwork beside the hero and pointing at the GitHub star. */
  art?: { hero: EventArt; starPointer: EventArt };
}

export const WALKTHROUGH_EMAIL = "support@autopilotmonitor.com";

/** Robots metadata of every event page. Not a robots.txt disallow: crawlers would then never see it. */
export const EVENT_PAGE_ROBOTS = {
  index: false,
  follow: false,
  googleBot: { index: false, follow: false },
} as const;

export const FOLLOWUP_EVENTS: readonly FollowupEvent[] = [
  {
    slug: "wpns-2026",
    name: "Workplace Ninja Summit 2026",
    walkthroughWith: "Oliver Kieselbach",
    art: {
      hero: { src: "/events/wpns/ninja-crouch.png", width: 500, height: 482 },
      starPointer: { src: "/events/wpns/ninja-pointing.png", width: 144, height: 121 },
    },
  },
];

/** Other tools from the same team that come in a free community edition. */
export const COMMUNITY_COMPANIONS = [
  {
    id: "scepman_ce",
    name: "SCEPman Community Edition",
    description: "Free certificate authority for Intune, running in your own Azure tenant.",
    href: "https://docs.scepman.com/editions",
  },
  {
    id: "myworkid_ce",
    name: "MyWorkID Community Edition",
    description: "Self-service Temporary Access Pass, password reset and user verification for Entra ID.",
    href: "https://www.glueckkanja.com/en/security/my-work-id",
  },
] as const;

export function findFollowupEvent(slug: string): FollowupEvent | undefined {
  return FOLLOWUP_EVENTS.find((event) => event.slug === slug);
}

/** The mailto: link behind "Request a walkthrough", subject naming the event. */
export function walkthroughMailto(event: FollowupEvent): string {
  return `mailto:${WALKTHROUGH_EMAIL}?subject=${encodeURIComponent(`Walkthrough request - ${event.name}`)}`;
}
