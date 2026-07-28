"use client";

import { useState, useEffect, useRef } from "react";
import { Session, EnrollmentEvent, RuleResult } from "@/types";

const MAX_AGENT_LOG_SIZE = 5 * 1024 * 1024; // 5 MB

interface ReportSessionModalProps {
  show: boolean;
  session: Session | null;
  events: EnrollmentEvent[];
  analysisResults: RuleResult[];
  onSubmit: (
    comment: string, email: string,
    screenshotBase64: string | null, screenshotFileName: string | null,
    agentLogBase64: string | null, agentLogFileName: string | null
  ) => Promise<void>;
  onCancel: () => void;
  submitting: boolean;
}

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export default function ReportSessionModal({
  show, session, events, analysisResults, onSubmit, onCancel, submitting
}: ReportSessionModalProps) {
  const [comment, setComment] = useState("");
  const [email, setEmail] = useState("");
  const [screenshotFiles, setScreenshotFiles] = useState<File[]>([]);
  const [agentLogFiles, setAgentLogFiles] = useState<File[]>([]);
  const [agentLogError, setAgentLogError] = useState<string | null>(null);
  const [submitResult, setSubmitResult] = useState<'success' | 'error' | null>(null);
  const [submitErrorMessage, setSubmitErrorMessage] = useState<string | null>(null);

  const agentLogInputRef = useRef<HTMLInputElement>(null);
  const screenshotInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (show) {
      setSubmitResult(null);
      setSubmitErrorMessage(null);
    }
  }, [show]);

  if (!show || !session) return null;

  const addAgentLogs = (newFiles: File[]) => {
    setAgentLogError(null);
    const merged = [...agentLogFiles];
    for (const f of newFiles) {
      if (!merged.some(existing => existing.name === f.name && existing.size === f.size)) {
        merged.push(f);
      }
    }
    const totalSize = merged.reduce((sum, f) => sum + f.size, 0);
    if (totalSize > MAX_AGENT_LOG_SIZE) {
      setAgentLogError(`Total size (${formatFileSize(totalSize)}) exceeds 5 MB limit. Remove some files or add smaller ones.`);
      // Keep existing files, don't add the new ones
      return;
    }
    setAgentLogFiles(merged);
    if (agentLogInputRef.current) agentLogInputRef.current.value = "";
  };

  const removeAgentLog = (index: number) => {
    setAgentLogError(null);
    setAgentLogFiles(prev => prev.filter((_, i) => i !== index));
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

  const fileToBase64 = async (file: File): Promise<string> => {
    const buffer = await file.arrayBuffer();
    return btoa(
      new Uint8Array(buffer).reduce((data, byte) => data + String.fromCharCode(byte), "")
    );
  };

  const handleSubmit = async () => {
    let screenshotBase64: string | null = null;
    let screenshotFileName: string | null = null;
    let agentLogBase64: string | null = null;
    let agentLogFileName: string | null = null;

    if (screenshotFiles.length === 1) {
      screenshotBase64 = await fileToBase64(screenshotFiles[0]);
      screenshotFileName = screenshotFiles[0].name;
    } else if (screenshotFiles.length > 1) {
      const entries: Record<string, Uint8Array> = {};
      for (const file of screenshotFiles) {
        const buffer = await file.arrayBuffer();
        entries[file.name] = new Uint8Array(buffer);
      }
      // fflate is only needed when zipping multi-file uploads — load on demand
      // (module is cached after first import) to keep it out of the route chunk.
      const { zipSync } = await import("fflate");
      const zipped = zipSync(entries);
      screenshotBase64 = btoa(
        zipped.reduce((data, byte) => data + String.fromCharCode(byte), "")
      );
      screenshotFileName = "screenshots.zip";
    }

    if (agentLogFiles.length === 1) {
      agentLogBase64 = await fileToBase64(agentLogFiles[0]);
      agentLogFileName = agentLogFiles[0].name;
    } else if (agentLogFiles.length > 1) {
      const entries: Record<string, Uint8Array> = {};
      for (const file of agentLogFiles) {
        entries[file.name] = new Uint8Array(await file.arrayBuffer());
      }
      const { zipSync } = await import("fflate");
      const zipped = zipSync(entries);
      agentLogBase64 = btoa(
        zipped.reduce((data, byte) => data + String.fromCharCode(byte), "")
      );
      agentLogFileName = "agent-logs.zip";
    }

    try {
      await onSubmit(comment, email, screenshotBase64, screenshotFileName, agentLogBase64, agentLogFileName);
      setSubmitResult('success');
      setComment("");
      setEmail("");
      setScreenshotFiles([]);
      setAgentLogFiles([]);
      setAgentLogError(null);
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

  const agentLogTotalSize = agentLogFiles.reduce((sum, f) => sum + f.size, 0);

  return (
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
              <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-purple-100 text-purple-800 dark:bg-purple-900/40 dark:text-purple-300 mt-1">
                Private Preview
              </span>
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
                  placeholder="Describe what seems incorrect or unexpected..."
                  className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-md text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                  rows={3}
                  disabled={submitting}
                />
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
                  placeholder="your.email@company.com"
                  className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-md text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                  disabled={submitting}
                />
                <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                  No guarantee of response. Issues may be silently fixed &mdash; check the changelog.
                </p>
              </div>

              {/* Agent Log Files */}
              <div className="mb-4">
                <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                  Agent Logs <span className="text-gray-400">(optional, max 5 MB total)</span>
                </label>
                <input
                  ref={agentLogInputRef}
                  type="file"
                  accept=".log,.txt,.zip"
                  multiple
                  onChange={e => addAgentLogs(e.target.files ? Array.from(e.target.files) : [])}
                  className="w-full text-sm text-gray-500 dark:text-gray-400 file:mr-4 file:py-2 file:px-4 file:rounded-md file:border-0 file:text-sm file:font-semibold file:bg-blue-50 file:text-blue-700 hover:file:bg-blue-100 dark:file:bg-blue-900/40 dark:file:text-blue-300"
                  disabled={submitting}
                />
                {agentLogError && (
                  <p className="text-xs text-red-600 dark:text-red-400 mt-1">{agentLogError}</p>
                )}
                {agentLogFiles.length > 0 && (
                  <div className="mt-2 space-y-1">
                    {agentLogFiles.map((file, i) => (
                      <div key={`${file.name}-${file.size}`} className="flex items-center justify-between bg-gray-50 dark:bg-gray-700/50 rounded px-2 py-1 text-xs text-gray-600 dark:text-gray-300">
                        <span className="truncate mr-2">{file.name} ({formatFileSize(file.size)})</span>
                        <button
                          type="button"
                          onClick={() => removeAgentLog(i)}
                          disabled={submitting}
                          className="flex-shrink-0 text-gray-400 hover:text-red-500 dark:hover:text-red-400 disabled:opacity-50"
                          title="Remove"
                        >
                          <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                            <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                          </svg>
                        </button>
                      </div>
                    ))}
                    <p className="text-xs text-gray-400 dark:text-gray-500">
                      {agentLogFiles.length} file{agentLogFiles.length !== 1 ? "s" : ""} — {formatFileSize(agentLogTotalSize)} total
                    </p>
                  </div>
                )}
                <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                  Located at %ProgramData%\AutopilotMonitor\Logs\ on the device.
                </p>
                <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
                  Located at %ProgramData%\Microsoft\IntuneManagementExtension\Logs\ on the device.
                </p>
              </div>

              {/* Screenshots */}
              <div className="mb-6">
                <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                  Screenshots <span className="text-gray-400">(optional)</span>
                </label>
                <input
                  ref={screenshotInputRef}
                  type="file"
                  accept="image/*"
                  multiple
                  onChange={e => addScreenshots(e.target.files ? Array.from(e.target.files) : [])}
                  className="w-full text-sm text-gray-500 dark:text-gray-400 file:mr-4 file:py-2 file:px-4 file:rounded-md file:border-0 file:text-sm file:font-semibold file:bg-blue-50 file:text-blue-700 hover:file:bg-blue-100 dark:file:bg-blue-900/40 dark:file:text-blue-300"
                  disabled={submitting}
                />
                {screenshotFiles.length > 0 && (
                  <div className="mt-2 space-y-1">
                    {screenshotFiles.map((file, i) => (
                      <div key={`${file.name}-${file.size}`} className="flex items-center justify-between bg-gray-50 dark:bg-gray-700/50 rounded px-2 py-1 text-xs text-gray-600 dark:text-gray-300">
                        <span className="truncate mr-2">{file.name} ({formatFileSize(file.size)})</span>
                        <button
                          type="button"
                          onClick={() => removeScreenshot(i)}
                          disabled={submitting}
                          className="flex-shrink-0 text-gray-400 hover:text-red-500 dark:hover:text-red-400 disabled:opacity-50"
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

              {/* Data summary */}
              <div className="bg-gray-50 dark:bg-gray-700/50 rounded-md p-3 mb-6 text-xs text-gray-600 dark:text-gray-300">
                <p className="font-medium mb-1">Data included in report:</p>
                <ul className="list-disc list-inside space-y-0.5">
                  <li>Session metadata (device, status, duration)</li>
                  <li>{events.length} event{events.length !== 1 ? "s" : ""} from timeline</li>
                  <li>{analysisResults.length} analysis result{analysisResults.length !== 1 ? "s" : ""}</li>
                  <li>Timeline export (TXT)</li>
                  <li>Table export (CSV)</li>
                  {agentLogFiles.length === 1 && <li>Agent log: {agentLogFiles[0].name} ({formatFileSize(agentLogFiles[0].size)})</li>}
                  {agentLogFiles.length > 1 && <li>{agentLogFiles.length} agent logs ({formatFileSize(agentLogTotalSize)}, will be zipped)</li>}
                  {screenshotFiles.length === 1 && <li>Screenshot: {screenshotFiles[0].name}</li>}
                  {screenshotFiles.length > 1 && <li>{screenshotFiles.length} screenshots (will be zipped)</li>}
                </ul>
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
                  disabled={submitting || !!agentLogError}
                  className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors disabled:opacity-50 flex items-center gap-2"
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
  );
}
