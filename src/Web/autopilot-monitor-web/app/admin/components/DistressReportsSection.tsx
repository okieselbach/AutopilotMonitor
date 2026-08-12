"use client";

import { useState, useEffect } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { TableSkeleton } from "@/components/skeletons/TableSkeleton";

interface DistressReport {
  tenantId: string;
  errorType: string;
  manufacturer: string | null;
  model: string | null;
  serialNumber: string | null;
  agentVersion: string | null;
  httpStatusCode: number | null;
  message: string | null;
  agentTimestamp: string;
  ingestedAt: string;
  sourceIp: string | null;
}

type SortKey = "ingestedAt" | "errorType" | "tenantId" | "manufacturer" | "model" | "serialNumber" | "httpStatusCode";
type SortDir = "asc" | "desc";

interface DistressReportsSectionProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
}

const ERROR_TYPE_LABELS: Record<string, { label: string; color: string }> = {
  AuthCertificateMissing:  { label: "Cert Missing",     color: "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300" },
  AuthCertificateInvalid:  { label: "Cert Invalid",     color: "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300" },
  AuthCertificateRejected: { label: "Cert Rejected",    color: "bg-orange-100 text-orange-800 dark:bg-orange-900/40 dark:text-orange-300" },
  HardwareNotAllowed:      { label: "HW Blocked",       color: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300" },
  DeviceNotRegistered:     { label: "Not Registered",   color: "bg-yellow-100 text-yellow-800 dark:bg-yellow-900/40 dark:text-yellow-300" },
  TenantRejected:          { label: "Tenant Rejected",  color: "bg-purple-100 text-purple-800 dark:bg-purple-900/40 dark:text-purple-300" },
  ConfigFetchDenied:       { label: "Config Denied",    color: "bg-blue-100 text-blue-800 dark:bg-blue-900/40 dark:text-blue-300" },
  SessionRegistrationDenied: { label: "Session Denied", color: "bg-blue-100 text-blue-800 dark:bg-blue-900/40 dark:text-blue-300" },
};

function ErrorTypeBadge({ errorType }: { errorType: string }) {
  const info = ERROR_TYPE_LABELS[errorType] ?? { label: errorType, color: "bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300" };
  return (
    <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${info.color}`}>
      {info.label}
    </span>
  );
}

function SortableTh({
  sortKey,
  currentKey,
  currentDir,
  onSort,
  children,
}: {
  sortKey: SortKey;
  currentKey: SortKey;
  currentDir: SortDir;
  onSort: (key: SortKey) => void;
  children: React.ReactNode;
}) {
  const active = currentKey === sortKey;
  return (
    <th
      onClick={() => onSort(sortKey)}
      className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider cursor-pointer select-none hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors"
    >
      <span className="inline-flex items-center gap-1">
        {children}
        <span className={`text-[10px] ${active ? "text-amber-600 dark:text-amber-400" : "text-gray-300 dark:text-gray-600"}`}>
          {active ? (currentDir === "asc" ? "▲" : "▼") : "↕"}
        </span>
      </span>
    </th>
  );
}

function CopyButton({ value }: { value: string }) {
  const [copied, setCopied] = useState(false);
  const handleCopy = (e: React.MouseEvent) => {
    e.stopPropagation();
    navigator.clipboard.writeText(value).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    });
  };
  return (
    <button
      onClick={handleCopy}
      title="Copy to clipboard"
      className="ml-1.5 p-0.5 rounded text-gray-400 hover:text-gray-600 dark:hover:text-gray-300 transition-colors"
    >
      {copied ? (
        <svg className="w-3.5 h-3.5 text-green-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
        </svg>
      ) : (
        <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
        </svg>
      )}
    </button>
  );
}

export function DistressReportsSection({
  getAccessToken,
  setError,
}: DistressReportsSectionProps) {
  const [reports, setReports] = useState<DistressReport[]>([]);
  // true from the start: the fetch fires on mount, and a false initial value
  // flashes the "No distress reports" empty state for one paint.
  const [loading, setLoading] = useState(true);
  const [selectedReport, setSelectedReport] = useState<DistressReport | null>(null);
  const [currentPage, setCurrentPage] = useState(0);
  const [sortKey, setSortKey] = useState<SortKey>("ingestedAt");
  const [sortDir, setSortDir] = useState<SortDir>("desc");
  const pageSize = 15;

  const handleSort = (key: SortKey) => {
    if (sortKey === key) {
      setSortDir(d => (d === "asc" ? "desc" : "asc"));
    } else {
      setSortKey(key);
      setSortDir(key === "ingestedAt" ? "desc" : "asc");
    }
    setCurrentPage(0);
  };

  const sortedReports = [...reports].sort((a, b) => {
    const av = a[sortKey];
    const bv = b[sortKey];
    // nulls last
    if (av == null && bv == null) return 0;
    if (av == null) return 1;
    if (bv == null) return -1;
    let cmp: number;
    if (sortKey === "ingestedAt") {
      cmp = new Date(av as string).getTime() - new Date(bv as string).getTime();
    } else if (sortKey === "httpStatusCode") {
      cmp = (av as number) - (bv as number);
    } else {
      cmp = String(av).localeCompare(String(bv));
    }
    return sortDir === "asc" ? cmp : -cmp;
  });

  const fetchReports = async () => {
    try {
      setLoading(true);
      const res = await authenticatedFetch(api.distressReports.list(), getAccessToken);
      if (res.status === 404) {
        setReports([]);
        return;
      }
      if (!res.ok) throw new Error(`Failed to load distress reports: ${res.statusText}`);
      const data = await res.json();
      setReports(data.reports ?? []);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while loading distress reports");
      }
      setError(err instanceof Error ? err.message : "Failed to load distress reports");
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    const run = async () => {
      await fetchReports();
    };
    void run();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Summary stats
  const errorTypeCounts = reports.reduce<Record<string, number>>((acc, r) => {
    acc[r.errorType] = (acc[r.errorType] ?? 0) + 1;
    return acc;
  }, {});

  const tenantCounts = reports.reduce<Record<string, number>>((acc, r) => {
    acc[r.tenantId] = (acc[r.tenantId] ?? 0) + 1;
    return acc;
  }, {});

  return (
    <div className="bg-gradient-to-br from-amber-50 to-orange-50 dark:from-gray-800 dark:to-gray-800 border-2 border-amber-300 dark:border-amber-700 rounded-lg shadow-lg">
      {/* Section Header */}
      <div className="p-6 border-b border-amber-200 dark:border-amber-700 bg-gradient-to-r from-amber-100 to-orange-100 dark:from-amber-900/40 dark:to-orange-900/40">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-amber-600 dark:text-amber-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L4.082 16.5c-.77.833.192 2.5 1.732 2.5z" />
          </svg>
          <div>
            <div className="flex items-center gap-2">
              <h2 className="text-xl font-semibold text-amber-900 dark:text-amber-100">Distress Reports</h2>
              <span className="text-xs text-amber-600 dark:text-amber-400 bg-amber-200 dark:bg-amber-800/50 px-2 py-0.5 rounded">
                Pre-Auth (unverified)
              </span>
            </div>
            <p className="text-sm text-amber-600 dark:text-amber-300 mt-1">
              Agent distress signals sent when authentication fails (cert missing, hardware blocked, device not registered).
              All data is unverified. Retention: 14 days.
            </p>
          </div>
        </div>
      </div>

      {/* Summary Cards — intentionally hidden while loading: distress is often
          empty on a healthy system, and a skeleton band that then vanishes
          shifts the layout more than one that appears. */}
      {!loading && reports.length > 0 && (
        <div className="p-4 border-b border-amber-200 dark:border-amber-700 bg-amber-50/50 dark:bg-gray-800/50">
          <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
            <div className="bg-white dark:bg-gray-700 rounded-lg p-3 border border-gray-200 dark:border-gray-600">
              <div className="text-2xl font-bold text-gray-900 dark:text-gray-100">{reports.length}</div>
              <div className="text-xs text-gray-500 dark:text-gray-400">Total Reports</div>
            </div>
            <div className="bg-white dark:bg-gray-700 rounded-lg p-3 border border-gray-200 dark:border-gray-600">
              <div className="text-2xl font-bold text-gray-900 dark:text-gray-100">{Object.keys(tenantCounts).length}</div>
              <div className="text-xs text-gray-500 dark:text-gray-400">Affected Tenants</div>
            </div>
            <div className="col-span-2 bg-white dark:bg-gray-700 rounded-lg p-3 border border-gray-200 dark:border-gray-600">
              <div className="text-xs text-gray-500 dark:text-gray-400 mb-1.5">By Error Type</div>
              <div className="flex flex-wrap gap-1.5">
                {Object.entries(errorTypeCounts)
                  .sort(([, a], [, b]) => b - a)
                  .map(([type, count]) => (
                    <span key={type} className="text-xs">
                      <ErrorTypeBadge errorType={type} /> <span className="text-gray-500 dark:text-gray-400 ml-0.5">{count}</span>
                    </span>
                  ))}
              </div>
            </div>
          </div>
        </div>
      )}

      {/* Reports Table */}
      <div className="p-6">
        {loading ? (
          <TableSkeleton columns={7} rows={15} />
        ) : reports.length === 0 ? (
          <div className="text-center py-8 text-gray-500 dark:text-gray-400">
            <svg className="w-12 h-12 mx-auto mb-3 text-gray-300 dark:text-gray-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <p className="text-sm">No distress reports in the last 14 days.</p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
              <thead className="bg-gray-50 dark:bg-gray-700/50">
                <tr>
                  <SortableTh sortKey="ingestedAt" currentKey={sortKey} currentDir={sortDir} onSort={handleSort}>Time</SortableTh>
                  <SortableTh sortKey="errorType" currentKey={sortKey} currentDir={sortDir} onSort={handleSort}>Error Type</SortableTh>
                  <SortableTh sortKey="tenantId" currentKey={sortKey} currentDir={sortDir} onSort={handleSort}>Tenant</SortableTh>
                  <SortableTh sortKey="manufacturer" currentKey={sortKey} currentDir={sortDir} onSort={handleSort}>Manufacturer</SortableTh>
                  <SortableTh sortKey="model" currentKey={sortKey} currentDir={sortDir} onSort={handleSort}>Model</SortableTh>
                  <SortableTh sortKey="serialNumber" currentKey={sortKey} currentDir={sortDir} onSort={handleSort}>S/N</SortableTh>
                  <SortableTh sortKey="httpStatusCode" currentKey={sortKey} currentDir={sortDir} onSort={handleSort}>HTTP</SortableTh>
                </tr>
              </thead>
              <tbody className="bg-white dark:bg-gray-800 divide-y divide-gray-200 dark:divide-gray-700">
                {sortedReports.slice(currentPage * pageSize, (currentPage + 1) * pageSize).map((r, idx) => (
                  <tr
                    key={`${r.tenantId}-${r.ingestedAt}-${idx}`}
                    onClick={() => setSelectedReport(r)}
                    className="hover:bg-amber-50 dark:hover:bg-amber-900/20 cursor-pointer transition-colors"
                  >
                    <td className="px-3 py-2.5 text-sm text-gray-900 dark:text-gray-100 whitespace-nowrap">
                      {new Date(r.ingestedAt).toLocaleString()}
                    </td>
                    <td className="px-3 py-2.5 text-sm">
                      <ErrorTypeBadge errorType={r.errorType} />
                    </td>
                    <td className="px-3 py-2.5 text-sm font-mono text-gray-700 dark:text-gray-300">
                      {r.tenantId.length > 8 ? `${r.tenantId.slice(0, 8)}...` : r.tenantId}
                    </td>
                    <td className="px-3 py-2.5 text-sm text-gray-700 dark:text-gray-300">
                      {r.manufacturer ?? <span className="text-gray-300 dark:text-gray-600">-</span>}
                    </td>
                    <td className="px-3 py-2.5 text-sm text-gray-700 dark:text-gray-300 truncate max-w-[160px]">
                      {r.model ?? <span className="text-gray-300 dark:text-gray-600">-</span>}
                    </td>
                    <td className="px-3 py-2.5 text-sm font-mono text-gray-700 dark:text-gray-300">
                      {r.serialNumber ? (r.serialNumber.length > 10 ? `${r.serialNumber.slice(0, 10)}...` : r.serialNumber) : <span className="text-gray-300 dark:text-gray-600">-</span>}
                    </td>
                    <td className="px-3 py-2.5 text-sm text-gray-700 dark:text-gray-300">
                      {r.httpStatusCode ?? <span className="text-gray-300 dark:text-gray-600">-</span>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>

            {/* Pagination */}
            {reports.length > pageSize && (
              <div className="flex items-center justify-between border-t border-gray-200 dark:border-gray-700 px-4 py-3 bg-gray-50 dark:bg-gray-700/50 rounded-b-md">
                <span className="text-xs text-gray-500 dark:text-gray-400">
                  {currentPage * pageSize + 1}&ndash;{Math.min((currentPage + 1) * pageSize, reports.length)} of {reports.length}
                </span>
                <div className="flex items-center gap-2">
                  <button
                    onClick={() => setCurrentPage(p => p - 1)}
                    disabled={currentPage === 0}
                    className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                  >
                    Previous
                  </button>
                  <span className="text-xs text-gray-500 dark:text-gray-400">
                    {currentPage + 1} / {Math.ceil(reports.length / pageSize)}
                  </span>
                  <button
                    onClick={() => setCurrentPage(p => p + 1)}
                    disabled={(currentPage + 1) * pageSize >= reports.length}
                    className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                  >
                    Next
                  </button>
                </div>
              </div>
            )}
          </div>
        )}
      </div>

      {/* Detail Modal */}
      {selectedReport && (
        <div
          className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4"
          onClick={() => setSelectedReport(null)}
        >
          <div
            className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-lg w-full max-h-[90vh] overflow-y-auto"
            onClick={e => e.stopPropagation()}
          >
            <div className="p-6">
              <div className="flex items-center justify-between mb-4">
                <h3 className="text-lg font-semibold text-gray-900 dark:text-gray-100">Distress Report Details</h3>
                <ErrorTypeBadge errorType={selectedReport.errorType} />
              </div>

              <dl className="space-y-3 text-sm">
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Tenant ID</dt>
                  <dd className="font-mono text-gray-900 dark:text-gray-100 mt-0.5 flex items-center">
                    {selectedReport.tenantId}
                    <CopyButton value={selectedReport.tenantId} />
                  </dd>
                </div>
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Error Type</dt>
                  <dd className="text-gray-900 dark:text-gray-100 mt-0.5">{selectedReport.errorType}</dd>
                </div>
                {selectedReport.httpStatusCode && (
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">HTTP Status</dt>
                    <dd className="text-gray-900 dark:text-gray-100 mt-0.5">{selectedReport.httpStatusCode}</dd>
                  </div>
                )}
                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Manufacturer</dt>
                    <dd className="text-gray-900 dark:text-gray-100 mt-0.5">{selectedReport.manufacturer ?? "-"}</dd>
                  </div>
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Model</dt>
                    <dd className="text-gray-900 dark:text-gray-100 mt-0.5">{selectedReport.model ?? "-"}</dd>
                  </div>
                </div>
                {selectedReport.serialNumber && (
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Serial Number</dt>
                    <dd className="font-mono text-gray-900 dark:text-gray-100 mt-0.5 flex items-center">
                      {selectedReport.serialNumber}
                      <CopyButton value={selectedReport.serialNumber} />
                    </dd>
                  </div>
                )}
                {selectedReport.agentVersion && (
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Agent Version</dt>
                    <dd className="text-gray-900 dark:text-gray-100 mt-0.5">{selectedReport.agentVersion}</dd>
                  </div>
                )}
                {selectedReport.message && (
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Message</dt>
                    <dd className="text-gray-900 dark:text-gray-100 mt-0.5 text-xs bg-gray-50 dark:bg-gray-700 rounded p-2 font-mono break-all">
                      {selectedReport.message}
                    </dd>
                  </div>
                )}
                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Agent Timestamp</dt>
                    <dd className="text-gray-900 dark:text-gray-100 mt-0.5 text-xs">
                      {new Date(selectedReport.agentTimestamp).toLocaleString()}
                    </dd>
                  </div>
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Ingested At</dt>
                    <dd className="text-gray-900 dark:text-gray-100 mt-0.5 text-xs">
                      {new Date(selectedReport.ingestedAt).toLocaleString()}
                    </dd>
                  </div>
                </div>
                {selectedReport.sourceIp && (
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Source IP</dt>
                    <dd className="font-mono text-gray-900 dark:text-gray-100 mt-0.5">{selectedReport.sourceIp}</dd>
                  </div>
                )}
              </dl>

              <div className="mt-6 flex justify-end">
                <button
                  onClick={() => setSelectedReport(null)}
                  className="px-4 py-2 text-sm font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 transition-colors"
                >
                  Close
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
