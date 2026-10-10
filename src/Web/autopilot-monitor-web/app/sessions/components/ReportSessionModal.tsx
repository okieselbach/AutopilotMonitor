"use client";

import { useState } from "react";
import { Session, EnrollmentEvent, RuleResult } from "@/types";
import { ModalPortal } from "@/components/ModalPortal";
import { AttachmentBudgetLine, AttachmentField } from "@/components/ReportAttachments";
import { useAttachmentPack } from "@/hooks/useAttachmentPack";
import { formatBytes } from "@/lib/formatting";
import { ATTACHMENT_BUDGET_BYTES, packedSize, type PackedAttachment } from "@/lib/reportAttachments";
import { SHARED_MANIFEST } from "@/lib/generated/shared-manifests.generated";

const MAX_COMMENT_CHARS = SHARED_MANIFEST.submissionLimits.reportCommentMaxChars;
const MAX_EMAIL_CHARS = SHARED_MANIFEST.submissionLimits.contactEmailMaxChars;

/** What the dialog hands the page; the page adds the session exports and sends the report. */
export interface ReportSubmission {
  comment: string;
  email: string;
  screenshot: PackedAttachment | null;
  agentLog: PackedAttachment | null;
  includeDiagnostics: boolean;
}

interface ReportSessionModalProps {
  show: boolean;
  session: Session | null;
  events: EnrollmentEvent[];
  analysisResults: RuleResult[];
  onSubmit: (submission: ReportSubmission) => Promise<void>;
  onCancel: () => void;
  submitting: boolean;
  /** True while the timeline is still streaming event pages — the export would be partial. */
  eventsStreaming?: boolean;
}

export default function ReportSessionModal({
  show, session, events, analysisResults, onSubmit, onCancel, submitting, eventsStreaming
}: ReportSessionModalProps) {
  const [comment, setComment] = useState("");
  const [email, setEmail] = useState("");
  const agentLogs = useAttachmentPack("logs", "agent-logs.zip");
  const screenshots = useAttachmentPack("screenshots", "screenshots.zip");
  const [includeDiagnostics, setIncludeDiagnostics] = useState(true);
  const [submitResult, setSubmitResult] = useState<'success' | 'error' | null>(null);
  const [submitErrorMessage, setSubmitErrorMessage] = useState<string | null>(null);

  // Clear stale result feedback from a previous submission the moment the modal
  // (re)opens. Adjust-during-render (compare-prev) instead of an effect — same
  // trigger (show flipping to true), but a stale banner can never paint first.
  const [prevShow, setPrevShow] = useState(show);
  if (prevShow !== show) {
    setPrevShow(show);
    if (show) {
      setSubmitResult(null);
      setSubmitErrorMessage(null);
    }
  }

  if (!show || !session) return null;

  // Backend omits null fields (WhenWritingNull) — truthiness check, never `=== null`.
  const hasDiagnostics = !!session.diagnosticsBlobName;

  const usedBytes = packedSize(agentLogs.packed, screenshots.packed);
  const overBudget = usedBytes > ATTACHMENT_BUDGET_BYTES;
  const packing = agentLogs.packing || screenshots.packing;

  const handleSubmit = async () => {
    try {
      await onSubmit({
        comment,
        email,
        screenshot: screenshots.packed,
        agentLog: agentLogs.packed,
        includeDiagnostics: hasDiagnostics && includeDiagnostics,
      });
      setSubmitResult('success');
      setComment("");
      setEmail("");
      agentLogs.reset();
      screenshots.reset();
    } catch (err: unknown) {
      setSubmitResult('error');
      setSubmitErrorMessage(err instanceof Error ? err.message : 'Failed to submit report.');
    }
  };

  const handleClose = () => {
    setSubmitResult(null);
    setSubmitErrorMessage(null);
    onCancel();
  };

  return (
    <ModalPortal>
      <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4" onClick={handleClose}>
        <div className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-lg w-full max-h-[90vh] overflow-y-auto" onClick={e => e.stopPropagation()}>
          <div className="p-6">
            {/* Header */}
            <div className="flex items-center mb-4">
              <div className="flex-shrink-0 w-12 h-12 bg-blue-100 dark:bg-blue-900/40 rounded-full flex items-center justify-center">
                <svg className="w-6 h-6 text-blue-600 dark:text-blue-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
                </svg>
              </div>
              <div className="ml-4">
                <h3 className="text-lg font-semibold text-gray-900 dark:text-gray-100">Report Session</h3>
              </div>
            </div>

            {/* Submit result feedback */}
            {submitResult === 'success' && (
              <div className="mb-4 p-4 bg-green-50 dark:bg-green-900/20 border border-green-200 dark:border-green-800 rounded-lg flex items-start gap-3">
                <svg className="w-5 h-5 text-green-600 dark:text-green-400 flex-shrink-0 mt-0.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <div>
                  <p className="text-sm font-semibold text-green-800 dark:text-green-300">Report submitted successfully</p>
                  <p className="text-sm text-green-700 dark:text-green-400 mt-0.5">
                    The session has been submitted for analysis by the Autopilot Monitor team.
                  </p>
                </div>
              </div>
            )}

            {submitResult === 'error' && (
              <div className="mb-4 p-4 bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800 rounded-lg flex items-start gap-3">
                <svg className="w-5 h-5 text-red-600 dark:text-red-400 flex-shrink-0 mt-0.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 14l2-2m0 0l2-2m-2 2l-2-2m2 2l2 2m7-2a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <div>
                  <p className="text-sm font-semibold text-red-800 dark:text-red-300">Failed to submit report</p>
                  <p className="text-sm text-red-700 dark:text-red-400 mt-0.5">{submitErrorMessage}</p>
                </div>
              </div>
            )}

            {/* Form — hidden after successful submit */}
            {submitResult !== 'success' && (
              <>
                {/* Explanation */}
                <p className="text-sm text-gray-700 dark:text-gray-300 mb-4">
                  Submit this session for analysis by the Autopilot Monitor team. The event timeline,
                  session data, analysis results, and UI exports will be included so discrepancies
                  can be analyzed and improvements made.
                </p>

                {/* Comment */}
                <div className="mb-4">
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                    Comment <span className="text-gray-400">(optional)</span>
                  </label>
                  <textarea
                    value={comment}
                    onChange={e => setComment(e.target.value)}
                    maxLength={MAX_COMMENT_CHARS}
                    placeholder="Describe what seems incorrect or unexpected..."
                    className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-md text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500"
                    rows={3}
                    disabled={submitting}
                  />
                  {comment.length > 0 && (
                    <p className="text-xs text-gray-400 text-right mt-0.5">{comment.length} / {MAX_COMMENT_CHARS}</p>
                  )}
                </div>

                {/* Email */}
                <div className="mb-4">
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                    Communication Email <span className="text-gray-400">(optional but recommended)</span>
                  </label>
                  <input
                    type="email"
                    value={email}
                    onChange={e => setEmail(e.target.value)}
                    maxLength={MAX_EMAIL_CHARS}
                    placeholder="your.email@company.com"
                    className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-md text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500"
                    disabled={submitting}
                  />
                  <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                    No guarantee of response. Issues may be silently fixed &mdash; check the changelog.
                  </p>
                </div>

                <AttachmentField
                  label="Agent Logs"
                  accept=".log,.txt,.zip"
                  pack={agentLogs}
                  disabled={submitting}
                  hints={[
                    "Located at %ProgramData%\\AutopilotMonitor\\Logs\\ on the device.",
                    "Located at %ProgramData%\\Microsoft\\IntuneManagementExtension\\Logs\\ on the device.",
                  ]}
                />

                <AttachmentField
                  label="Screenshots"
                  accept="image/*"
                  pack={screenshots}
                  disabled={submitting}
                />

                <AttachmentBudgetLine usedBytes={usedBytes} />

                {/* Diagnostics archive opt-in — active only when the session has an uploaded
                    diag ZIP; rendered disabled otherwise so the option is discoverable. */}
                <div className="mb-6">
                  <label className={`flex items-start gap-2 ${hasDiagnostics ? "cursor-pointer" : "cursor-not-allowed opacity-60"}`}>
                    <input
                      type="checkbox"
                      checked={hasDiagnostics && includeDiagnostics}
                      onChange={e => setIncludeDiagnostics(e.target.checked)}
                      disabled={submitting || !hasDiagnostics}
                      className="mt-0.5 h-4 w-4 rounded border-gray-300 dark:border-gray-600 text-green-600 focus:ring-green-500"
                    />
                    <span className="text-sm text-gray-700 dark:text-gray-300">
                      Include uploaded diagnostics archive
                      <span className="block text-xs text-gray-500 dark:text-gray-400 mt-0.5">
                        {hasDiagnostics
                          ? "A server-side copy of this session's diagnostics ZIP is stored with the report, so it stays available for analysis even after the session is deleted."
                          : "No diagnostics archive has been uploaded for this session — nothing to include."}
                      </span>
                    </span>
                  </label>
                </div>

                {/* Data summary */}
                <div className="bg-gray-50 dark:bg-gray-700/50 rounded-md p-3 mb-6 text-xs text-gray-600 dark:text-gray-300">
                  <p className="font-medium mb-1">Data included in report:</p>
                  <ul className="list-disc list-inside space-y-0.5">
                    <li>Session metadata (device, status, duration)</li>
                    <li>{events.length} event{events.length !== 1 ? "s" : ""} from timeline</li>
                    <li>{analysisResults.length} analysis result{analysisResults.length !== 1 ? "s" : ""}</li>
                    <li>Timeline export (TXT)</li>
                    <li>Table export (CSV)</li>
                    {agentLogs.files.length > 0 && (
                      <li>
                        {agentLogs.files.length === 1 ? `Agent log: ${agentLogs.files[0].name}` : `${agentLogs.files.length} agent logs`}
                        {agentLogs.packed ? ` (${formatBytes(agentLogs.packed.bytes.length)} zipped)` : ""}
                      </li>
                    )}
                    {screenshots.files.length === 1 && <li>Screenshot: {screenshots.files[0].name}</li>}
                    {screenshots.files.length > 1 && <li>{screenshots.files.length} screenshots (zipped)</li>}
                    {hasDiagnostics && includeDiagnostics && <li>Uploaded diagnostics archive (copied server-side)</li>}
                  </ul>
                  {eventsStreaming && (
                    <p className="mt-2 text-amber-700 dark:text-amber-400">
                      ⚠ The event timeline is still loading — a report submitted now will contain a
                      partial export. Wait a moment for the complete timeline if possible.
                    </p>
                  )}
                </div>
              </>
            )}

            {/* Actions */}
            <div className="flex justify-end gap-3">
              {submitResult === 'success' ? (
                <button
                  onClick={handleClose}
                  className="px-4 py-2 bg-green-600 text-white rounded-md hover:bg-green-700 transition-colors"
                >
                  Close
                </button>
              ) : (
                <>
                  <button
                    onClick={handleClose}
                    disabled={submitting}
                    className="px-4 py-2 bg-gray-200 dark:bg-gray-600 text-gray-700 dark:text-gray-200 rounded-md hover:bg-gray-300 dark:hover:bg-gray-500 transition-colors disabled:opacity-50"
                  >
                    Cancel
                  </button>
                  <button
                    onClick={handleSubmit}
                    disabled={submitting || packing || overBudget}
                    className="px-4 py-2 bg-green-600 text-white rounded-md hover:bg-green-700 transition-colors disabled:opacity-50 flex items-center gap-2"
                  >
                    {submitting ? (
                      <>
                        <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                        Submitting...
                      </>
                    ) : (
                      "Submit Report"
                    )}
                  </button>
                </>
              )}
            </div>
          </div>
        </div>
      </div>
    </ModalPortal>
  );
}
