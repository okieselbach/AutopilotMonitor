"use client";

import { useState } from "react";
import { BOOTSTRAP_GO_URL } from "@/utils/config";
import { SectionCardHeader } from "@/components/SectionCardHeader";
import { DOCS_PATHS } from "@/lib/docsPaths";

// Display-only: the authoritative URL comes from the backend's bootstrapUrl
// (Constants.BootstrapGoBaseUrl); this host renders the sessions list rows.
const GO_HOST = new URL(BOOTSTRAP_GO_URL).host;

export interface BootstrapSessionItem {
  shortCode: string;
  label: string;
  createdAt: string;
  expiresAt: string;
  createdByUpn: string;
  isRevoked: boolean;
  isExpired: boolean;
  usageCount: number;
}

interface BootstrapSessionsSectionProps {
  sessions: BootstrapSessionItem[];
  loading: boolean;
  onRefresh: () => void;
  onRevoke: (code: string) => Promise<void>;
  onCreate: (validityHours: number, label: string) => Promise<string | null>;
}

const VALIDITY_OPTIONS = [
  { label: "1 hour", value: 1 },
  { label: "4 hours", value: 4 },
  { label: "8 hours", value: 8 },
  { label: "24 hours", value: 24 },
  { label: "48 hours", value: 48 },
  { label: "7 days", value: 168 },
];

export default function BootstrapSessionsSection({
  sessions,
  loading,
  onRefresh,
  onRevoke,
  onCreate,
}: BootstrapSessionsSectionProps) {
  const [showCreateForm, setShowCreateForm] = useState(false);
  const [validityHours, setValidityHours] = useState(8);
  const [label, setLabel] = useState("");
  const [creating, setCreating] = useState(false);
  const [createdUrl, setCreatedUrl] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [copiedCommand, setCopiedCommand] = useState(false);
  const [revoking, setRevoking] = useState<string | null>(null);
  const [confirmRevoke, setConfirmRevoke] = useState<string | null>(null);

  const handleCreate = async () => {
    setCreating(true);
    try {
      const url = await onCreate(validityHours, label);
      if (url) {
        setCreatedUrl(url);
        setLabel("");
        setShowCreateForm(false);
      }
    } finally {
      setCreating(false);
    }
  };

  const handleRevoke = async (code: string) => {
    setRevoking(code);
    try {
      await onRevoke(code);
      setConfirmRevoke(null);
    } finally {
      setRevoking(null);
    }
  };

  const copyToClipboard = async (text: string, type: "url" | "command") => {
    await navigator.clipboard.writeText(text);
    if (type === "url") {
      setCopied(true);
      setTimeout(() => setCopied(false), 1400);
    } else {
      setCopiedCommand(true);
      setTimeout(() => setCopiedCommand(false), 1400);
    }
  };

  const getStatusBadge = (session: BootstrapSessionItem) => {
    if (session.isRevoked)
      return (
        <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-red-100 text-red-700">
          Revoked
        </span>
      );
    if (session.isExpired)
      return (
        <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-600">
          Expired
        </span>
      );
    return (
      <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-green-100 text-green-700">
        Active
      </span>
    );
  };

  const formatRelativeTime = (dateStr: string) => {
    const date = new Date(dateStr);
    const now = new Date();
    const diffMs = date.getTime() - now.getTime();
    const diffMins = Math.round(diffMs / 60000);

    if (diffMs < 0) {
      const absMins = Math.abs(diffMins);
      if (absMins < 60) return `${absMins}m ago`;
      if (absMins < 1440) return `${Math.round(absMins / 60)}h ago`;
      return `${Math.round(absMins / 1440)}d ago`;
    }
    if (diffMins < 60) return `in ${diffMins}m`;
    if (diffMins < 1440) return `in ${Math.round(diffMins / 60)}h`;
    return `in ${Math.round(diffMins / 1440)}d`;
  };

  const activeSessions = sessions.filter(
    (s) => !s.isRevoked && !s.isExpired,
  );

  return (
    <div className="bg-white rounded-lg shadow">
      {/* Header */}
      <SectionCardHeader
        tone="cyan"
        iconPath="M13 10V3L4 14h7v7l9-11h-7z"
        title="OOBE Bootstrap"
        subtitle="Generate time-limited URLs to install the agent before Intune enrollment"
        docsPath={DOCS_PATHS.bootstrapSessions}
        trailing={
          <button
            onClick={onRefresh}
            disabled={loading}
            className="p-2 text-gray-500 hover:text-gray-700 hover:bg-gray-100 rounded-lg transition-colors"
            title="Refresh"
          >
            <svg
              className={`w-5 h-5 ${loading ? "animate-spin" : ""}`}
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15"
              />
            </svg>
          </button>
        }
      />

      <div className="p-6 space-y-4">
        {/* Info banner */}
        <div className="bg-cyan-50 border border-cyan-200 rounded-lg p-3">
          <p className="text-sm text-cyan-800">
            Create a bootstrap session to deploy the agent during OOBE (Shift+F10).
            The generated URL is time-limited and can be used on multiple devices
            within the validity window.
          </p>
        </div>

        {/* Created URL display */}
        {createdUrl && (
          <div className="bg-green-50 border border-green-200 rounded-lg p-4">
            <div className="flex items-start justify-between">
              <div className="flex-1">
                <p className="text-sm font-medium text-green-800 mb-2">
                  Bootstrap session created!
                </p>
                <div className="space-y-2">
                  <div>
                    <p className="text-xs text-green-700 mb-1">PowerShell command (run on device during OOBE):</p>
                    <div className="flex items-center space-x-2">
                      <code className="flex-1 text-sm bg-white border border-green-300 rounded px-3 py-2 font-mono text-green-900 select-all">
                        irm {createdUrl} | iex
                      </code>
                      <button
                        onClick={() =>
                          copyToClipboard(`irm ${createdUrl} | iex`, "command")
                        }
                        className="p-2 text-green-600 hover:text-green-800 hover:bg-green-100 rounded transition-colors"
                        title={
                          copiedCommand ? "Copied!" : "Copy command"
                        }
                      >
                        {copiedCommand ? (
                          <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                          </svg>
                        ) : (
                          <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
                          </svg>
                        )}
                      </button>
                    </div>
                  </div>
                  <div>
                    <p className="text-xs text-green-700 mb-1">URL:</p>
                    <div className="flex items-center space-x-2">
                      <code className="flex-1 text-xs bg-white border border-green-300 rounded px-3 py-1.5 font-mono text-green-900 select-all break-all">
                        {createdUrl}
                      </code>
                      <button
                        onClick={() => copyToClipboard(createdUrl, "url")}
                        className="p-1.5 text-green-600 hover:text-green-800 hover:bg-green-100 rounded transition-colors"
                        title={copied ? "Copied!" : "Copy URL"}
                      >
                        {copied ? (
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                          </svg>
                        ) : (
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
                          </svg>
                        )}
                      </button>
                    </div>
                  </div>
                </div>
              </div>
              <button
                onClick={() => setCreatedUrl(null)}
                className="ml-2 p-1 text-green-400 hover:text-green-600"
              >
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                </svg>
              </button>
            </div>
          </div>
        )}

        {/* Create form */}
        {showCreateForm ? (
          <div className="border border-cyan-200 rounded-lg p-4 space-y-3">
            <div className="flex items-center space-x-4">
              <div className="flex-1">
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  Valid for
                </label>
                <select
                  value={validityHours}
                  onChange={(e) => setValidityHours(Number(e.target.value))}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-gray-900 focus:outline-none focus:ring-2 focus:ring-cyan-500"
                >
                  {VALIDITY_OPTIONS.map((opt) => (
                    <option key={opt.value} value={opt.value}>
                      {opt.label}
                    </option>
                  ))}
                </select>
              </div>
              <div className="flex-1">
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  Label (optional)
                </label>
                <input
                  type="text"
                  value={label}
                  onChange={(e) => setLabel(e.target.value)}
                  placeholder="e.g. Lab A, Floor 3"
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-gray-900 focus:outline-none focus:ring-2 focus:ring-cyan-500"
                />
              </div>
            </div>
            <div className="flex items-center space-x-2">
              <button
                onClick={handleCreate}
                disabled={creating}
                className="px-4 py-2 bg-cyan-600 text-white rounded-lg hover:bg-cyan-700 disabled:opacity-50 transition-colors flex items-center space-x-2"
              >
                {creating && (
                  <svg className="animate-spin h-4 w-4 border-b-2 border-white rounded-full" viewBox="0 0 24 24" />
                )}
                <span>{creating ? "Creating..." : "Create Session"}</span>
              </button>
              <button
                onClick={() => setShowCreateForm(false)}
                className="px-4 py-2 border border-gray-300 text-gray-700 bg-white rounded-lg hover:bg-gray-50 transition-colors"
              >
                Cancel
              </button>
            </div>
          </div>
        ) : (
          <button
            onClick={() => setShowCreateForm(true)}
            className="w-full px-4 py-3 border-2 border-dashed border-cyan-300 text-cyan-700 rounded-lg hover:border-cyan-400 hover:bg-cyan-50 transition-colors flex items-center justify-center space-x-2"
          >
            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
            </svg>
            <span>Create Bootstrap Session</span>
          </button>
        )}

        {/* Sessions list */}
        {sessions.length > 0 && (
          <div>
            <h4 className="text-sm font-medium text-gray-700 mb-2">
              Sessions ({activeSessions.length} active)
            </h4>
            <div className="space-y-2">
              {sessions.map((session) => (
                <div
                  key={session.shortCode}
                  className={`flex items-center justify-between p-3 rounded-lg border ${
                    session.isRevoked || session.isExpired
                      ? "border-gray-200 bg-gray-50 opacity-60"
                      : "border-cyan-200 hover:border-cyan-300"
                  } transition-colors`}
                >
                  <div className="flex-1 min-w-0">
                    <div className="flex items-center space-x-2">
                      <code className="text-sm font-mono text-gray-800">
                        {GO_HOST}/{session.shortCode}
                      </code>
                      {getStatusBadge(session)}
                      {session.label && (
                        <span className="text-sm text-gray-500 truncate">
                          {session.label}
                        </span>
                      )}
                    </div>
                    <div className="flex items-center space-x-3 mt-1 text-xs text-gray-500">
                      <span>
                        Created{" "}
                        {new Date(session.createdAt).toLocaleDateString()}{" "}
                        {new Date(session.createdAt).toLocaleTimeString([], {
                          hour: "2-digit",
                          minute: "2-digit",
                        })}
                      </span>
                      <span>
                        {session.isExpired
                          ? `Expired ${formatRelativeTime(session.expiresAt)}`
                          : `Expires ${formatRelativeTime(session.expiresAt)}`}
                      </span>
                      <span>{session.usageCount} uses</span>
                      <span className="truncate">
                        by {session.createdByUpn}
                      </span>
                    </div>
                  </div>
                  <div className="flex items-center space-x-1 ml-2">
                    {!session.isRevoked && !session.isExpired && (
                      <>
                        {confirmRevoke === session.shortCode ? (
                          <div className="flex items-center space-x-1">
                            <button
                              onClick={() => handleRevoke(session.shortCode)}
                              disabled={revoking === session.shortCode}
                              className="px-2 py-1 text-xs bg-red-600 text-white rounded hover:bg-red-700 disabled:opacity-50 transition-colors"
                            >
                              {revoking === session.shortCode
                                ? "..."
                                : "Confirm"}
                            </button>
                            <button
                              onClick={() => setConfirmRevoke(null)}
                              className="px-2 py-1 text-xs border border-gray-300 text-gray-600 rounded hover:bg-gray-50 transition-colors"
                            >
                              Cancel
                            </button>
                          </div>
                        ) : (
                          <button
                            onClick={() =>
                              setConfirmRevoke(session.shortCode)
                            }
                            className="p-1.5 text-gray-400 hover:text-red-600 hover:bg-red-50 rounded transition-colors"
                            title="Revoke session"
                          >
                            <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636" />
                            </svg>
                          </button>
                        )}
                      </>
                    )}
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}

        {sessions.length === 0 && !loading && (
          <p className="text-sm text-gray-500 text-center py-4">
            No bootstrap sessions yet. Create one to get started.
          </p>
        )}
      </div>
    </div>
  );
}
