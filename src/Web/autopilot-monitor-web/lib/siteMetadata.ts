import type { Metadata } from "next";

/**
 * Shared link-preview fields for public pages. A page that sets its own `openGraph` or `twitter`
 * replaces the root layout's object instead of merging with it, so every such page spreads these
 * in. Without them its previews lose the image, type and site name, and the Twitter card falls
 * back to "summary".
 */
const OG_IMAGE = { url: "/opengraph-image.png", width: 1200, height: 630, alt: "Autopilot Monitor" };

export const OPEN_GRAPH_BASE = {
  type: "website",
  locale: "en_US",
  siteName: "Autopilot Monitor",
  images: [OG_IMAGE],
} satisfies NonNullable<Metadata["openGraph"]>;

export const TWITTER_BASE = {
  card: "summary_large_image",
  images: [OG_IMAGE.url],
} satisfies NonNullable<Metadata["twitter"]>;
