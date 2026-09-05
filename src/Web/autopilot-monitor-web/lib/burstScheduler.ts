/**
 * Burst scheduler for real-time refetches: leading edge + trailing debounce + max wait.
 *
 * Telemetry arrives in bursts (several agent batches within a second, then seconds of
 * silence) and every batch raises a SignalR signal. A plain trailing debounce either
 * fires once per batch (window shorter than the batch spacing) or hides the whole burst
 * until it pauses (window longer than the spacing, timer keeps resetting). This scheduler
 * keeps the live feel and still coalesces:
 *
 *  - leading: the first trigger after a quiet period runs immediately (0 ms delay);
 *  - trailing: further triggers inside a burst run once, `trailingMs` after the last one,
 *    so the refetch follows the END of the burst instead of a fixed tick;
 *  - max wait: a burst that never pauses still runs at least every `maxWaitMs`
 *    (spool replay after a reconnect or reboot).
 *
 * A steady stream of triggers spaced closer than `trailingMs` therefore runs exactly once
 * per `maxWaitMs`; isolated triggers run at once. Pure timers, no React — the hooks own
 * the "what to run" via a ref so the callback always sees the latest closure.
 */
export interface BurstSchedulerOptions {
  /** Quiet time after the last trigger before the trailing run (ms). */
  trailingMs: number;
  /** Upper bound between two runs while triggers keep arriving (ms); >= trailingMs. */
  maxWaitMs: number;
}

export interface BurstScheduler {
  /** Request a run; coalesced per the rules above. */
  trigger: () => void;
  /** Drop a pending trailing run (unmount). Does not reset the leading-edge cool-down. */
  cancel: () => void;
}

export function createBurstScheduler(run: () => void, options: BurstSchedulerOptions): BurstScheduler {
  const { trailingMs, maxWaitMs } = options;
  if (!(trailingMs >= 0) || !(maxWaitMs >= trailingMs)) {
    throw new Error(`createBurstScheduler: need 0 <= trailingMs <= maxWaitMs, got ${trailingMs}/${maxWaitMs}`);
  }

  let timer: ReturnType<typeof setTimeout> | null = null;
  // Time of the last run; the max-wait window for the next trailing run starts here.
  let lastRunAt = Number.NEGATIVE_INFINITY;

  const fire = () => {
    timer = null;
    lastRunAt = Date.now();
    run();
  };

  const scheduleTrailing = (now: number) => {
    if (timer) clearTimeout(timer);
    const fireAt = Math.min(now + trailingMs, lastRunAt + maxWaitMs);
    timer = setTimeout(fire, Math.max(0, fireAt - now));
  };

  return {
    trigger: () => {
      const now = Date.now();
      if (timer) {
        // Inside a burst: push the trailing run out, but never past the max-wait bound.
        scheduleTrailing(now);
        return;
      }
      if (now - lastRunAt >= trailingMs) {
        // Quiet long enough: leading edge, run right away.
        fire();
        return;
      }
      // Just ran: cool down into a trailing run so a burst cannot run twice per trailingMs.
      scheduleTrailing(now);
    },
    cancel: () => {
      if (timer) {
        clearTimeout(timer);
        timer = null;
      }
    },
  };
}
