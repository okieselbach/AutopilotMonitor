import type { MouseEvent } from "react";
import Link from "next/link";
import type { Route } from "next";

/**
 * "Fired in N enrollments in the last 14 days · View sessions →" next to a finding: rule telemetry,
 * counted in enrollments (never devices), and the dashboard filtered to the rule.
 */
export function RecentFiresLine({
  count,
  href,
  linkClassName,
  onLinkClick,
}: {
  count: number;
  href: Route;
  linkClassName: string;
  onLinkClick?: (e: MouseEvent<HTMLAnchorElement>) => void;
}) {
  return (
    <span className="text-xs italic text-gray-500" title="May include this enrollment; based on rule telemetry (enrollments, not distinct devices)">
      Fired in {count} enrollment{count === 1 ? "" : "s"} in the last 14 days
      {" · "}
      <Link href={href} onClick={onLinkClick} className={linkClassName}>
        View sessions →
      </Link>
    </span>
  );
}
