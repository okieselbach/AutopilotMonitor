import { LandingPage } from "../components/landing/LandingPage";
import { loadPlatformStatsSnapshot } from "../lib/platformStats";

// The stats read below runs once per `next build` and bypasses Next's fetch cache (`no-store`,
// see lib/platformStats.ts). force-static keeps that legal in the static export; the landing
// tree reads no request-time API (cookies, headers, search params) that it would blank out.
export const dynamic = "force-static";

/**
 * The landing page with the platform stats of the build baked into its static HTML, so
 * crawlers and LLM fetchers that run no JS see the numbers too; browsers then load the live
 * ones (components/landing/StatsBand.tsx). Awaited here, not under a Suspense boundary: React
 * would move a resolved boundary of a page this size into a hidden div at the end of the body.
 */
export default async function Page() {
  return <LandingPage statsSnapshot={await loadPlatformStatsSnapshot()} />;
}
