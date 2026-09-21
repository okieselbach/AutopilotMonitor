import type { SlaMetricsResponse } from "@/utils/wire-types.generated";

export interface SlaTargetCheck {
  label: string;
  met: boolean;
  /** False when the target's period holds nothing to judge — neither met nor breached. */
  hasData: boolean;
}

export type SlaOverallState = "met" | "breached" | "noData";

/**
 * One check per configured target, on the window its breach notification evaluates:
 * success rate + duration on the rolling evaluation window, app installs on the current week.
 */
export function buildSlaChecks(metrics: SlaMetricsResponse): SlaTargetCheck[] {
  const checks: SlaTargetCheck[] = [];
  const period = metrics.evaluationPeriod;
  if (metrics.targetSuccessRate != null)
    checks.push({ label: "Success Rate", met: period.successRateMet, hasData: period.hasData });
  if (metrics.targetMaxDurationMinutes != null)
    checks.push({ label: "Duration", met: period.durationTargetMet, hasData: period.hasData });
  // appInstallSla only exists once the week has enough installs to evaluate.
  if (metrics.targetAppInstallSuccessRate != null && metrics.appInstallSla)
    checks.push({ label: "App Installs", met: metrics.appInstallSla.targetMet, hasData: true });
  return checks;
}

export function summarizeSlaChecks(checks: SlaTargetCheck[]): {
  state: SlaOverallState;
  metCount: number;
  judgedCount: number;
} {
  const judged = checks.filter((c) => c.hasData);
  const metCount = judged.filter((c) => c.met).length;
  const state: SlaOverallState =
    judged.length === 0 ? "noData" : metCount === judged.length ? "met" : "breached";
  return { state, metCount, judgedCount: judged.length };
}

/** The window length comes from the backend, so the label can never name another window than the one judged. */
export function formatSlaWindow(days: number): string {
  return `last ${days} days`;
}
