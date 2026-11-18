/**
 * Download speed for the live download panel, averaged over the last window instead of the last
 * pair of ticks: Delivery Optimization reports bytes in 1 MiB steps, so a speed from one 3 s pair
 * swings by about a third around the real rate. Bars and byte counts still update on every tick;
 * only the speed figure is averaged.
 */

export const DOWNLOAD_RATE_WINDOW_MS = 20_000;

export interface RateSample {
  tMs: number;
  bytes: number;
}

/**
 * Appends a tick and drops the ones older than the window, always keeping the previous tick
 * (throttled downloads report less often than the window). A falling byte count means the
 * download restarted: the window starts over.
 */
export function pushRateSample(
  samples: readonly RateSample[],
  sample: RateSample,
  windowMs = DOWNLOAD_RATE_WINDOW_MS,
): RateSample[] {
  const last = samples[samples.length - 1];
  if (last && sample.bytes < last.bytes) return [sample];
  const next = [...samples, sample];
  let first = 0;
  while (first < next.length - 2 && sample.tMs - next[first].tMs > windowMs) first++;
  return next.slice(first);
}

/**
 * Bytes per second from the oldest tick inside the window (at least the previous tick) to the
 * newest. Null when there is no earlier tick or the bytes did not grow — the caller keeps its
 * last speed then.
 */
export function windowedRateBps(
  samples: readonly RateSample[],
  windowMs = DOWNLOAD_RATE_WINDOW_MS,
): number | null {
  if (samples.length < 2) return null;
  const end = samples[samples.length - 1];
  let start = samples.length - 2;
  for (let i = samples.length - 3; i >= 0; i--) {
    if (end.tMs - samples[i].tMs > windowMs) break;
    start = i;
  }
  const seconds = (end.tMs - samples[start].tMs) / 1000;
  const delta = end.bytes - samples[start].bytes;
  if (!(seconds > 0) || !(delta > 0)) return null;
  return delta / seconds;
}
