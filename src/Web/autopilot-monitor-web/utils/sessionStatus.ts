// Session status helpers. Mirror of the backend SessionStatus enum
// (src/Shared/AutopilotMonitor.Shared/Models/SessionApiModels.cs).
// Non-terminal: InProgress, Pending, Stalled, AwaitingUser, Unknown.
// Terminal: Succeeded, Failed, Incomplete.
//
// AwaitingUser (Device Setup done, user/Account-Setup phase pending) and Incomplete
// (terminal, non-failure — no completion or explicit failure was observed) come from the
// timeout reclassification, see docs/design/enrollment-status-reclassification.md.

export function isTerminalStatus(status: string | null | undefined): boolean {
  return status === "Succeeded" || status === "Failed" || status === "Incomplete";
}
