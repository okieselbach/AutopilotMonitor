import { DocsLink } from "@/components/DocsLink";
import { DOCS_PATHS } from "@/lib/docsPaths";
import { BLOCKED_ICON_PATH, formatBlockTime, type SessionBlock } from "@/lib/sessionBlock";

/**
 * Says why a session stopped reporting when the platform blocked its device: the event limit
 * or the platform operator paused its uploads (orange, the Blocked pill's family) or ended
 * monitoring (red). Rendered only for sessions that carry a block record.
 */
export function MonitoringBlockedBlock({ block }: { block: SessionBlock | null }) {
  if (!block) return null;

  const at = formatBlockTime(block.blockedAt);
  const until = block.blockedUntil ? formatBlockTime(block.blockedUntil) : at;
  const count = block.eventCount;
  const limit = block.eventThreshold;

  const title = `${block.isKill ? "Monitoring ended" : "Monitoring paused"}${
    block.byOperator ? " by the platform operator" : " — event limit reached"
  }`;
  const action = block.isKill
    ? `the agent stopped monitoring on ${at} and removed itself.`
    : `uploads from this session are paused from ${at} until ${until}.`;
  const scope = block.isKill
    ? `Until ${until}, new enrollments on this device are not monitored either.`
    : "The enrollment itself is not affected, and the device's next enrollment starts unblocked.";
  const tone = block.isKill
    ? "bg-red-50 border-red-200 text-red-900 dark:bg-red-900/30 dark:border-red-800 dark:text-red-200"
    : "bg-orange-50 border-orange-200 text-orange-900 dark:bg-orange-900/30 dark:border-orange-800 dark:text-orange-200";

  return (
    <div className={`mt-4 flex items-start gap-2 px-3 py-2 rounded-lg border text-sm ${tone}`}>
      <svg className="w-4 h-4 flex-shrink-0 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d={BLOCKED_ICON_PATH} />
      </svg>
      <div className="min-w-0">
        <strong>{title}</strong>
        <p className="mt-1">
          {block.byOperator ? (
            action.charAt(0).toUpperCase() + action.slice(1)
          ) : (
            <>
              {count !== undefined && limit !== undefined && (
                <>
                  This session sent {count.toLocaleString("en-US")} events, above the limit of{" "}
                  {limit.toLocaleString("en-US")}.{" "}
                </>
              )}
              To protect your fleet data and the platform, {action}
            </>
          )}
        </p>
        <p className="mt-1">
          {scope}{" "}
          <DocsLink path={DOCS_PATHS.protectiveLimits} label="Learn more" />
        </p>
      </div>
    </div>
  );
}
