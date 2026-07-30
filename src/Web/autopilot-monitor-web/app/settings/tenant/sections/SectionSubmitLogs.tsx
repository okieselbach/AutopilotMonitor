"use client";

import { useRef, useState } from "react";
import { useAuth } from "../../../../contexts/AuthContext";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { api } from "@/lib/api";
import { trackEvent } from "@/lib/appInsights";

const MAX_LOG_SIZE = 5 * 1024 * 1024; // 5 MB
const LOG_ACCEPT = ".log,.txt,.zip,.json,.jsonl,.ndjson";

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

async function fileToBase64(file: File): Promise<string> {
  const buffer = await file.arrayBuffer();
  return btoa(
    new Uint8Array(buffer).reduce((data, byte) => data + String.fromCharCode(byte), ""),
  );
}

async function bundleFiles(files: File[], zipName: string): Promise<{ base64: string; fileName: string } | null> {
  if (files.length === 0) return null;
  if (files.length === 1) {
    return { base64: await fileToBase64(files[0]), fileName: files[0].name };
  }
  const entries: Record<string, Uint8Array> = {};
  for (const f of files) {
    entries[f.name] = new Uint8Array(await f.arrayBuffer());
  }
  // Load fflate on demand so it stays out of the settings route chunk.
  const { zipSync } = await import("fflate");
  const zipped = zipSync(entries);
  const base64 = btoa(zipped.reduce((data, byte) => data + String.fromCharCode(byte), ""));
  return { base64, fileName: zipName };
}

export function SectionSubmitLogs() {
  const { user, getAccessToken } = useAuth();

  const [comment, setComment] = useState("");
  const [email, setEmail] = useState("");
  const [logFiles, setLogFiles] = useState<File[]>([]);
  const [screenshotFiles, setScreenshotFiles] = useState<File[]>([]);
  const [logError, setLogError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [submitResult, setSubmitResult] = useState<"success" | "error" | null>(null);
  const [submitErrorMessage, setSubmitErrorMessage] = useState<string | null>(null);

  const logInputRef = useRef<HTMLInputElement>(null);
  const screenshotInputRef = useRef<HTMLInputElement>(null);

  const addLogs = (newFiles: File[]) => {
    setLogError(null);
    const merged = [...logFiles];
    for (const f of newFiles) {
      if (!merged.some(existing => existing.name === f.name && existing.size === f.size)) {
        merged.push(f);
      }
    }
    const totalSize = merged.reduce((sum, f) => sum + f.size, 0);
    if (totalSize > MAX_LOG_SIZE) {
      setLogError(`Total size (${formatFileSize(totalSize)}) exceeds 5 MB limit. Remove some files or add smaller ones.`);
      return;
    }
    setLogFiles(merged);
    if (logInputRef.current) logInputRef.current.value = "";
  };

  const removeLog = (index: number) => {
    setLogError(null);
    setLogFiles(prev => prev.filter((_, i) => i !== index));
  };

  const addScreenshots = (newFiles: File[]) => {
    const merged = [...screenshotFiles];
    for (const f of newFiles) {
      if (!merged.some(existing => existing.name === f.name && existing.size === f.size)) {
        merged.push(f);
      }
    }
    setScreenshotFiles(merged);
    if (screenshotInputRef.current) screenshotInputRef.current.value = "";
  };

  const removeScreenshot = (index: number) => {
    setScreenshotFiles(prev => prev.filter((_, i) => i !== index));
  };

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
      const logBundle = await bundleFiles(logFiles, "diag-files.zip");
      const screenshotBundle = await bundleFiles(screenshotFiles, "screenshots.zip");

      const response = await authenticatedFetch(
        api.diagFilesReports.submit(),
        getAccessToken,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            tenantId: user.tenantId,
            comment,
            email,
            screenshotBase64: screenshotBundle?.base64 ?? null,
            screenshotFileName: screenshotBundle?.fileName ?? null,
            agentLogBase64: logBundle?.base64 ?? null,
            agentLogFileName: logBundle?.fileName ?? null,
          }),
        },
      );

      if (!response.ok) {
        const data = await response.json().catch(() => null);
        throw new Error(data?.message || `Failed to submit (${response.status}).`);
      }

      trackEvent("diag_files_report_submitted");
      setSubmitResult("success");
      setComment("");
      setEmail("");
      setLogFiles([]);
      setScreenshotFiles([]);
      setLogError(null);
    } catch (err: unknown) {
      if (err instanceof TokenExpiredError) {
        setSubmitErrorMessage("Session expired. Please reload the page and try again.");
      } else {
        setSubmitErrorMessage(err instanceof Error ? err.message : "Failed to submit report.");
      }
      setSubmitResult("error");
    } finally {
      setSubmitting(false);
    }
  };

  const logTotalSize = logFiles.reduce((sum, f) => sum + f.size, 0);
  const hasAnyContent = comment.trim().length > 0 || logFiles.length > 0 || screenshotFiles.length > 0;

  return (
    <div className="bg-white rounded-lg shadow p-6 max-w-2xl mx-auto">
      <div className="flex items-center mb-4">
        <div className="flex-shrink-0 w-10 h-10 bg-blue-100 rounded-full flex items-center justify-center">
          <svg className="w-5 h-5 text-blue-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
          </svg>
        </div>
        <div className="ml-3">
          <h2 className="text-lg font-semibold text-gray-900">Submit Logs to Support</h2>
          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-purple-100 text-purple-800 mt-1">
            Private Preview
          </span>
        </div>
      </div>

      <p className="text-sm text-gray-700 mb-4">
        Send log files, JSON state snapshots, or screenshots to the Autopilot Monitor team for
        analysis — no need to share via OneDrive. Submissions are stored in the same backend
        pipeline as Session Reports and visible only to Global Admins.
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
          placeholder="What's going on? Which device / scenario do these files relate to?"
          className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm bg-white text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500"
          rows={3}
          disabled={submitting}
        />
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
          placeholder="your.email@company.com"
          className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm bg-white text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500"
          disabled={submitting}
        />
        <p className="text-xs text-gray-500 mt-1">
          No guarantee of response. Issues may be silently fixed &mdash; check the changelog.
        </p>
      </div>

      {/* Log / state files */}
      <div className="mb-4">
        <label className="block text-sm font-medium text-gray-700 mb-1">
          Log &amp; state files <span className="text-gray-400">(optional, max 5 MB total)</span>
        </label>
        <input
          ref={logInputRef}
          type="file"
          accept={LOG_ACCEPT}
          multiple
          onChange={e => addLogs(e.target.files ? Array.from(e.target.files) : [])}
          className="w-full text-sm text-gray-500 file:mr-4 file:py-2 file:px-4 file:rounded-md file:border-0 file:text-sm file:font-semibold file:bg-blue-50 file:text-blue-700 hover:file:bg-blue-100"
          disabled={submitting}
        />
        {logError && <p className="text-xs text-red-600 mt-1">{logError}</p>}
        {logFiles.length > 0 && (
          <div className="mt-2 space-y-1">
            {logFiles.map((file, i) => (
              <div key={`${file.name}-${file.size}`} className="flex items-center justify-between bg-gray-50 rounded px-2 py-1 text-xs text-gray-600">
                <span className="truncate mr-2">{file.name} ({formatFileSize(file.size)})</span>
                <button
                  type="button"
                  onClick={() => removeLog(i)}
                  disabled={submitting}
                  className="flex-shrink-0 text-gray-400 hover:text-red-500 disabled:opacity-50"
                  title="Remove"
                >
                  <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                  </svg>
                </button>
              </div>
            ))}
            <p className="text-xs text-gray-400">
              {logFiles.length} file{logFiles.length !== 1 ? "s" : ""} — {formatFileSize(logTotalSize)} total
            </p>
          </div>
        )}
        <p className="text-xs text-gray-500 mt-1">
          Accepted: .log, .txt, .zip, .json, .jsonl, .ndjson. Multiple files are zipped automatically.
        </p>
      </div>

      {/* Screenshots */}
      <div className="mb-6">
        <label className="block text-sm font-medium text-gray-700 mb-1">
          Screenshots <span className="text-gray-400">(optional)</span>
        </label>
        <input
          ref={screenshotInputRef}
          type="file"
          accept="image/*"
          multiple
          onChange={e => addScreenshots(e.target.files ? Array.from(e.target.files) : [])}
          className="w-full text-sm text-gray-500 file:mr-4 file:py-2 file:px-4 file:rounded-md file:border-0 file:text-sm file:font-semibold file:bg-blue-50 file:text-blue-700 hover:file:bg-blue-100"
          disabled={submitting}
        />
        {screenshotFiles.length > 0 && (
          <div className="mt-2 space-y-1">
            {screenshotFiles.map((file, i) => (
              <div key={`${file.name}-${file.size}`} className="flex items-center justify-between bg-gray-50 rounded px-2 py-1 text-xs text-gray-600">
                <span className="truncate mr-2">{file.name} ({formatFileSize(file.size)})</span>
                <button
                  type="button"
                  onClick={() => removeScreenshot(i)}
                  disabled={submitting}
                  className="flex-shrink-0 text-gray-400 hover:text-red-500 disabled:opacity-50"
                  title="Remove"
                >
                  <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                  </svg>
                </button>
              </div>
            ))}
          </div>
        )}
      </div>

      <div className="flex justify-end">
        <button
          onClick={handleSubmit}
          disabled={submitting || !!logError || !hasAnyContent}
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
