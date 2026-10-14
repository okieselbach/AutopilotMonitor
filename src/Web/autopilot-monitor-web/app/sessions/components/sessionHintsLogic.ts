/**
 * Gates for the two "no dead end" hints on a failed session. Kept React-free so the full matrix is
 * testable; the page hands in what it already knows.
 */

export interface DiagnosticsSetupHintInput {
  status: string | undefined;
  /** session.diagnosticsBlobName is set (truthiness — the backend omits null fields). */
  hasDiagnosticsPackage: boolean;
  /** The feature-flags read succeeded; before it (and on an error) the flag below is only a default. */
  featureFlagsLoaded: boolean;
  diagnosticsUploadConfigured: boolean;
  /** Global Admin / MSP drill-in: the viewer cannot act on another tenant's settings from here. */
  isCrossTenantView: boolean;
  isGatherRulesSession: boolean;
}

/**
 * "No diagnostics package — diagnostics upload isn't set up": only on a failed enrollment, only when the
 * tenant really has no usable upload (Off, or on without a working destination), never on a guess.
 */
export function showDiagnosticsSetupHint(input: DiagnosticsSetupHintInput): boolean {
  return (
    input.status === "Failed" &&
    !input.hasDiagnosticsPackage &&
    input.featureFlagsLoaded &&
    !input.diagnosticsUploadConfigured &&
    !input.isCrossTenantView &&
    !input.isGatherRulesSession
  );
}

export interface InvestigateHintInput {
  status: string | undefined;
  /** At least one analysis read succeeded — an empty list before that (or after an error) proves nothing. */
  analysisLoaded: boolean;
  loadingAnalysis: boolean;
  /** Open plus resolved findings. */
  findingCount: number;
}

/**
 * "No analyze rule flagged this failure": replaces the generic empty state of a failed session. Worded so
 * it stays literally true while the end-of-enrollment analysis is still queued — findings that arrive
 * later refresh the section over SignalR and the hint disappears.
 */
export function showInvestigateHint(input: InvestigateHintInput): boolean {
  return input.status === "Failed" && input.analysisLoaded && !input.loadingAnalysis && input.findingCount === 0;
}

/**
 * The Diagnosis button: every session that ended without success or is stuck, and a success only
 * while an analyze finding is open on it. Read-only viewers get it too — the page only reads.
 */
export function showDiagnosisButton(status: string | undefined, openFindingCount: number): boolean {
  switch (status) {
    case "Failed":
    case "Stalled":
    case "AwaitingUser":
    case "Incomplete":
      return true;
    case "Succeeded":
      return openFindingCount > 0;
    default:
      return false;
  }
}
