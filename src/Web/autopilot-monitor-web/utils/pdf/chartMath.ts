// Pure chart math for the PDF chart renderers — no jsPDF dependency, unit-testable.

/**
 * Round a data maximum up to a "nice" axis maximum (1/2/2.5/5 progression) and
 * return evenly spaced tick values from 0 to that maximum.
 */
export function niceTicks(maxValue: number, tickCount = 4): { max: number; ticks: number[] } {
  if (!Number.isFinite(maxValue) || maxValue <= 0) {
    return { max: 1, ticks: [0, 1] };
  }
  const rawStep = maxValue / tickCount;
  const magnitude = Math.pow(10, Math.floor(Math.log10(rawStep)));
  const normalized = rawStep / magnitude;
  const niceFactor =
    normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 2.5 ? 2.5 : normalized <= 5 ? 5 : 10;
  const step = niceFactor * magnitude;
  const max = Math.ceil(maxValue / step) * step;
  const ticks: number[] = [];
  // Float-safe loop: derive each tick from the integer index, not by accumulation.
  const count = Math.round(max / step);
  for (let i = 0; i <= count; i++) {
    ticks.push(Number((i * step).toPrecision(12)));
  }
  return { max, ticks };
}

/** Map a data value in [0, domainMax] to a length in [0, range]. */
export function linearScale(domainMax: number, range: number): (v: number) => number {
  if (!Number.isFinite(domainMax) || domainMax <= 0) return () => 0;
  return (v: number) => (v / domainMax) * range;
}

/**
 * Pick up to maxLabels evenly spaced indices out of n buckets, always including
 * the first and last — so 7 daily buckets label all days while 90 label ~10.
 */
export function tickIndices(n: number, maxLabels: number): number[] {
  if (n <= 0 || maxLabels <= 0) return [];
  if (n <= maxLabels) return Array.from({ length: n }, (_, i) => i);
  const step = (n - 1) / (maxLabels - 1);
  const indices = new Set<number>();
  for (let i = 0; i < maxLabels; i++) {
    indices.add(Math.round(i * step));
  }
  return [...indices].sort((a, b) => a - b);
}

/** UTC M/D bucket label — mirrors formatBucketTick on the app detail page. */
export function formatBucketLabel(iso: string): string {
  const d = new Date(iso);
  if (isNaN(d.getTime())) return iso;
  return `${d.getUTCMonth() + 1}/${d.getUTCDate()}`;
}
