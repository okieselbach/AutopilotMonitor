import { SHARED_MANIFEST } from "@/lib/generated/shared-manifests.generated";
import type { Session } from "@/types";

/** What a device block does (backend `DeviceBlockActions`). */
export type DeviceBlockAction = (typeof SHARED_MANIFEST.deviceBlockActions)[number];
/** Why the platform blocked a session (backend `SessionBlockCauses`). */
export type SessionBlockCause = (typeof SHARED_MANIFEST.sessionBlockCauses)[number];

const KILL: DeviceBlockAction = "Kill";
const OPERATOR: SessionBlockCause = "operator";

/** The ban icon of the Blocked pill and the session page's block card. */
export const BLOCKED_ICON_PATH =
  "M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636";

/** A session's block record — written when the platform blocked its device, never the live block list. */
export interface SessionBlock {
  blockedAt: string;
  blockedUntil?: string;
  /** The agent ended monitoring and removed itself; the device stays unmonitored until `blockedUntil`. */
  isKill: boolean;
  /** Placed by the platform operator rather than the event limit. */
  byOperator: boolean;
  eventCount?: number;
  eventThreshold?: number;
}

type BlockFields = Pick<
  Session,
  "blockedAt" | "blockAction" | "blockedUntil" | "blockCause" | "blockEventCount" | "blockEventThreshold"
>;

/** The session's block record, or null when the platform never blocked it. */
export function sessionBlockOf(session: BlockFields): SessionBlock | null {
  if (!session.blockedAt) return null;
  return {
    blockedAt: session.blockedAt,
    blockedUntil: session.blockedUntil,
    isKill: session.blockAction === KILL,
    byOperator: session.blockCause === OPERATOR,
    eventCount: session.blockEventCount,
    eventThreshold: session.blockEventThreshold,
  };
}

/** Local short date and time, the format of the session page's other timestamps. */
export function formatBlockTime(iso: string): string {
  return new Date(iso).toLocaleString([], { dateStyle: "short", timeStyle: "short" });
}

/** One line for the session list's Blocked pill. */
export function sessionBlockTooltip(block: SessionBlock): string {
  const verb = block.isKill ? "Monitoring ended" : "Monitoring paused";
  const by = block.byOperator ? "by the platform operator" : "by the event limit";
  return `${verb} ${by} on ${formatBlockTime(block.blockedAt)}`;
}
