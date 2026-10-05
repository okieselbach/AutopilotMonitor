"use client";

import { useState } from "react";
import { useAuth } from "../../../../contexts/AuthContext";
import { apiErrorText, fetchOk, jsonBody } from "@/lib/apiClient";
import { api } from "@/lib/api";
import { trackEvent } from "@/lib/appInsights";
import { bytesToBase64 } from "@/lib/base64";
import { openFeedbackDialog } from "@/lib/feedbackDialogStore";
import { ATTACHMENT_BUDGET_BYTES, fitsReportRequest, packedSize } from "@/lib/reportAttachments";
import { useAttachmentPack } from "@/hooks/useAttachmentPack";
import { AttachmentBudgetLine, AttachmentField } from "@/components/ReportAttachments";
import { SHARED_MANIFEST } from "@/utils/shared-manifests.generated";
import type { SubmitDiagFilesReportRequest } from "@/utils/wire-types.generated";

const LOG_ACCEPT = ".log,.txt,.zip,.json,.jsonl,.ndjson";
const MAX_COMMENT_CHARS = SHARED_MANIFEST.submissionLimits.reportCommentMaxChars;
const MAX_EMAIL_CHARS = SHARED_MANIFEST.submissionLimits.contactEmailMaxChars;

const FIELD_CLASS =
  "w-full px-3 py-2 border border-gray-300 rounded-md text-sm bg-white text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500";

function SectionHeader() {
  return (
    <div className="flex items-center mb-4">
      <div className="flex-shrink-0 w-10 h-10 bg-blue-100 rounded-full flex items-center justify-center">
        <svg className="w-5 h-5 text-blue-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
        </svg>
      </div>
      <div className="ml-3">
        <h2 className="text-lg font-semibold text-gray-900">Submit Logs to Support</h2>
      </div>
    </div>
  );
}

export function SectionSubmitLogs() {
  const { user, getAccessToken } = useAuth();
  const logs = useAttachmentPack("logs", "diag-files.zip");
  const screenshots = useAttachmentPack("screenshots", "screenshots.zip");

  const [comment, setComment] = useState("");
  const [email, setEmail] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [submitResult, setSubmitResult] = useState<"success" | "error" | null>(null);
  const [submitErrorMessage, setSubmitErrorMessage] = useState<string | null>(null);

  // The route admits Admin, Operator and Global Admin. Viewers and Global Readers get a pointer
  // to the feedback dialog instead of a form that would end in a 403.
  const canSubmit = !!user && (user.isTenantAdmin || user.role === "Operator" || user.isGlobalAdmin);

  if (!canSubmit) {
    return (
      <div className="bg-white rounded-lg shadow p-6 max-w-2xl mx-auto">
        <SectionHeader />
        <p className="text-sm text-gray-700 mb-4">
          Submitting files is available to tenant administrators and operators.
        </p>
        <button
          type="button"
          onClick={() => openFeedbackDialog("submit_logs_page")}
          className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors"
        >
          Send feedback instead
        </button>
      </div>
    );
  }

  const usedBytes = packedSize(logs.packed, screenshots.packed);
  const overBudget = usedBytes > ATTACHMENT_BUDGET_BYTES;
  const packing = logs.packing || screenshots.packing;
  const hasAnyContent = comment.trim().length > 0 || logs.files.length > 0 || screenshots.files.length > 0;

  const handleSubmit = async () => {
    if (!user?.tenantId) {
      setSubmitResult("error");
      setSubmitErrorMessage("Tenant context unavailable. Please reload the page.");
      return;
    }
    setSubmitting(true);
    setSubmitResult(null);
    setSubmitErrorMessage(null);
    try {
      const body = jsonBody<SubmitDiagFilesReportRequest>({
        tenantId: user.tenantId,
        comment,
        email,
        screenshotBase64: screenshots.packed ? bytesToBase64(screenshots.packed.bytes) : null,
        screenshotFileName: screenshots.packed?.fileName ?? null,
        agentLogBase64: logs.packed ? bytesToBase64(logs.packed.bytes) : null,
        agentLogFileName: logs.packed?.fileName ?? null,
      });
      if (!fitsReportRequest(body)) {
        setSubmitErrorMessage("The attachments are too large to send. Remove some files and try again.");
        setSubmitResult("error");
        return;
      }

      await fetchOk(api.diagFilesReports.submit(), getAccessToken, { method: "POST", body });

      trackEvent("diag_files_report_submitted");
      setSubmitResult("success");
      setComment("");
      setEmail("");
      logs.reset();
      screenshots.reset();
    } catch (err: unknown) {
      setSubmitErrorMessage(apiErrorText(err, "Failed to submit report."));
      setSubmitResult("error");
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="bg-white rounded-lg shadow p-6 max-w-2xl mx-auto">
      <SectionHeader />

      <p className="text-sm text-gray-700 mb-4">
        Send log files, JSON state snapshots, or screenshots to the Autopilot Monitor team for
        analysis — no need to share via OneDrive. Submissions are visible only to the Autopilot
        Monitor team.
      </p>

      {submitResult === "success" && (
        <div className="mb-4 p-4 bg-green-50 border border-green-200 rounded-lg flex items-start gap-3">
          <svg className="w-5 h-5 text-green-600 flex-shrink-0 mt-0.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
          </svg>
          <div>
            <p className="text-sm font-semibold text-green-800">Report submitted successfully</p>
            <p className="text-sm text-green-700 mt-0.5">
              The diagnostic files have been sent for analysis by the Autopilot Monitor team.
            </p>
          </div>
        </div>
      )}

      {submitResult === "error" && submitErrorMessage && (
        <div className="mb-4 p-4 bg-red-50 border border-red-200 rounded-lg flex items-start gap-3">
          <svg className="w-5 h-5 text-red-600 flex-shrink-0 mt-0.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 14l2-2m0 0l2-2m-2 2l-2-2m2 2l2 2m7-2a9 9 0 11-18 0 9 9 0 0118 0z" />
          </svg>
          <div>
            <p className="text-sm font-semibold text-red-800">Failed to submit report</p>
            <p className="text-sm text-red-700 mt-0.5">{submitErrorMessage}</p>
          </div>
        </div>
      )}

      {/* Comment */}
      <div className="mb-4">
        <label className="block text-sm font-medium text-gray-700 mb-1">
          Comment <span className="text-gray-400">(optional)</span>
        </label>
        <textarea
          value={comment}
          onChange={e => setComment(e.target.value)}
          maxLength={MAX_COMMENT_CHARS}
          placeholder="What's going on? Which device / scenario do these files relate to?"
          className={FIELD_CLASS}
          rows={3}
          disabled={submitting}
        />
        {comment.length > 0 && (
          <p className="text-xs text-gray-400 text-right mt-0.5">{comment.length} / {MAX_COMMENT_CHARS}</p>
        )}
      </div>

      {/* Email */}
      <div className="mb-4">
        <label className="block text-sm font-medium text-gray-700 mb-1">
          Communication Email <span className="text-gray-400">(optional but recommended)</span>
        </label>
        <input
          type="email"
          value={email}
          onChange={e => setEmail(e.target.value)}
          maxLength={MAX_EMAIL_CHARS}
          placeholder="your.email@company.com"
          className={FIELD_CLASS}
          disabled={submitting}
        />
        <p className="text-xs text-gray-500 mt-1">
          No guarantee of response. Issues may be silently fixed &mdash; check the changelog.
        </p>
      </div>

      <AttachmentField
        label="Log & state files"
        accept={LOG_ACCEPT}
        pack={logs}
        disabled={submitting}
        hints={["Accepted: .log, .txt, .zip, .json, .jsonl, .ndjson. Logs are zipped before sending."]}
      />

      <AttachmentField
        label="Screenshots"
        accept="image/*"
        pack={screenshots}
        disabled={submitting}
      />

      <AttachmentBudgetLine usedBytes={usedBytes} />

      <div className="flex justify-end">
        <button
          onClick={handleSubmit}
          disabled={submitting || packing || overBudget || !hasAnyContent}
          className="px-4 py-2 bg-green-600 text-white rounded-md hover:bg-green-700 transition-colors disabled:opacity-50 flex items-center gap-2"
          title={!hasAnyContent ? "Add a comment, log file, or screenshot first" : undefined}
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
      </div>
    </div>
  );
}
