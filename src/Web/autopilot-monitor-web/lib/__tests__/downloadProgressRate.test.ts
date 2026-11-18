import { describe, it, expect } from "vitest";
import { pushRateSample, windowedRateBps, type RateSample } from "@/lib/downloadProgressRate";

const MiB = 1_048_576;

// Delivery Optimization ticks every 3 s with byte counts in whole MiB, at a real rate of 460 KB/s.
function quantizedTicks(seconds: number): RateSample[] {
  const ticks: RateSample[] = [];
  for (let t = 0; t <= seconds; t += 3) {
    ticks.push({ tMs: t * 1000, bytes: Math.floor((t * 460 * 1024) / MiB) * MiB });
  }
  return ticks;
}

describe("windowedRateBps", () => {
  it("needs a previous tick", () => {
    expect(windowedRateBps([])).toBeNull();
    expect(windowedRateBps([{ tMs: 0, bytes: MiB }])).toBeNull();
  });

  it("smooths the 1 MiB steps that make a pair rate swing", () => {
    let samples: RateSample[] = [];
    const pairErrors: number[] = [];
    const windowErrors: number[] = [];
    const real = 460 * 1024;
    for (const tick of quantizedTicks(180)) {
      const prev = samples[samples.length - 1];
      samples = pushRateSample(samples, tick);
      if (tick.tMs < 30_000 || !prev) continue;
      const pair = (tick.bytes - prev.bytes) / ((tick.tMs - prev.tMs) / 1000);
      pairErrors.push(Math.abs(pair - real) / real);
      windowErrors.push(Math.abs((windowedRateBps(samples) ?? 0) - real) / real);
    }
    const max = (xs: number[]) => Math.max(...xs);
    expect(max(pairErrors)).toBeGreaterThan(0.4);
    expect(max(windowErrors)).toBeLessThan(0.2);
  });

  it("uses the previous tick when ticks are slower than the window", () => {
    const samples = [
      { tMs: 0, bytes: 0 },
      { tMs: 30_000, bytes: 30 * MiB },
    ];
    expect(windowedRateBps(samples)).toBe(MiB);
  });

  it("gives no rate when the bytes did not grow", () => {
    expect(windowedRateBps([{ tMs: 0, bytes: MiB }, { tMs: 3_000, bytes: MiB }])).toBeNull();
  });
});

describe("pushRateSample", () => {
  it("keeps only the ticks of the window, and at least the previous one", () => {
    let samples: RateSample[] = [];
    for (let t = 0; t <= 60; t += 3) samples = pushRateSample(samples, { tMs: t * 1000, bytes: t * MiB });
    expect(samples[0].tMs).toBe(42_000);
    expect(samples[samples.length - 1].tMs).toBe(60_000);

    const slow = pushRateSample([{ tMs: 0, bytes: 0 }], { tMs: 60_000, bytes: MiB });
    expect(slow).toHaveLength(2);
  });

  it("starts over when the byte count falls (download restarted)", () => {
    const samples = pushRateSample([{ tMs: 0, bytes: 5 * MiB }, { tMs: 3_000, bytes: 6 * MiB }], { tMs: 6_000, bytes: MiB });
    expect(samples).toEqual([{ tMs: 6_000, bytes: MiB }]);
  });
});
