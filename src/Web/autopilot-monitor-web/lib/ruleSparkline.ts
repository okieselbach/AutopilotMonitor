/** Bars of the analyze-rule fire-count sparkline; its width stays the same for every window. */
export const SPARKLINE_BARS = 30;

/**
 * Fire counts per bar, oldest first, for the window ending at `end` (UTC day): one day per bar up to
 * {@link SPARKLINE_BARS} days — a shorter window draws fewer bars — and beyond that
 * ceil(window / {@link SPARKLINE_BARS}) days per bar. Days without a row count as zero.
 */
export function sparklineBars(
  fireCountByDate: ReadonlyMap<string, number>,
  end: Date,
  windowDays: number,
): { values: number[]; daysPerBar: number } {
  const daysPerBar = Math.max(1, Math.ceil(windowDays / SPARKLINE_BARS));
  const barCount = Math.min(SPARKLINE_BARS, Math.ceil(windowDays / daysPerBar));
  const values: number[] = [];
  for (let bar = barCount - 1; bar >= 0; bar--) {
    let sum = 0;
    for (let k = 0; k < daysPerBar; k++) {
      const d = new Date(end);
      d.setUTCDate(d.getUTCDate() - (bar * daysPerBar + k));
      sum += fireCountByDate.get(d.toISOString().slice(0, 10)) ?? 0;
    }
    values.push(sum);
  }
  return { values, daysPerBar };
}
