/** Wording of the "enrollment monitoring is interrupted" notice, shared by the dashboard and Settings. */
export const GRAPH_ACCESS_TITLE = "Action required: Enrollment monitoring is interrupted";

const NOT_MONITORED = "Devices can't be validated, so new enrollments are not being monitored.";

/** What is wrong: feature-flags `graphAccessAppMissing` selects the concrete cause. */
export function graphAccessCause(appMissing: boolean): string {
  return appMissing
    ? `The Autopilot Monitor enterprise application is missing from your Microsoft Entra tenant. ${NOT_MONITORED}`
    : `Microsoft Entra is rejecting Autopilot Monitor's access to your tenant. ${NOT_MONITORED}`;
}

/** Who can repair it, for a page that is not Settings itself. */
export function graphAccessNextStep(appMissing: boolean): string {
  return appMissing
    ? "A Global Administrator or Privileged Role Administrator can restore it in Settings."
    : "A Global Administrator or Privileged Role Administrator can restore access in Settings.";
}
