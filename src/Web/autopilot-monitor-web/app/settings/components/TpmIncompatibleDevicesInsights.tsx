"use client";

import { useState, useEffect, useCallback } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";

interface AggregatedTpmPssUnsupported {
  serialNumber: string;
  manufacturer: string;
  model: string;
  attemptCount: number;
  firstSeen: string;
  lastSeen: string;
}

interface TpmPssUnsupportedResponse {
  success: boolean;
  aggregated: AggregatedTpmPssUnsupported[];
  totalRawReports: number;
  dataQualityNotice: string;
}

interface TpmIncompatibleDevicesInsightsProps {
  getAccessToken: () => Promise<string | null>;
}

function formatDate(iso: string): string {
  try {
    return new Date(iso).toLocaleString(undefined, {
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  } catch {
    return iso;
  }
}

/**
 * Devices whose TPM cannot perform RSA-PSS signing — Schannel silently drops their client
 * certificate from TLS client-auth (Windows 11 25H2+ prefers PSS), so the agent can never
 * authenticate. Remediation is a TPM firmware update or device replacement.
 *
 * Unlike the sibling insights panels this one renders nothing while loading, on error, and
 * when no device is affected: it is a rare condition and a third permanent card in the
 * section would be noise for the common all-clear case.
 */
export default function TpmIncompatibleDevicesInsights({
  getAccessToken,
}: TpmIncompatibleDevicesInsightsProps) {
  const [data, setData] = useState<TpmPssUnsupportedResponse | null>(null);

  const fetchData = useCallback(async () => {
    try {
      const res = await authenticatedFetch(api.distress.tpmPssUnsupported(), getAccessToken);
      if (!res.ok) return;
      const json: TpmPssUnsupportedResponse = await res.json();
      setData(json);
    } catch {
      // Silent degrade — the panel is a secondary insight; the section stays usable without it.
    }
  }, [getAccessToken]);

  useEffect(() => {
    fetchData();
  }, [fetchData]);

  if (!data || data.aggregated.length === 0) {
    return null;
  }

  return (
    <div className="bg-white rounded-lg shadow mt-6">
      {/* Header */}
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-amber-50 to-orange-50">
        <div className="flex items-center justify-between">
          <div className="flex items-center space-x-2">
            <svg className="w-6 h-6 text-amber-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L4.082 16.5c-.77.833.192 2.5 1.732 2.5z" />
            </svg>
            <div>
              <h3 className="text-lg font-semibold text-gray-900">
                Devices with Incompatible TPM
                <span className="ml-2 text-xs font-normal text-gray-500">Last 14 days</span>
              </h3>
              <p className="text-sm text-gray-500 mt-0.5">
                These devices have a TPM that cannot perform RSA-PSS signing, so the agent cannot
                authenticate and no enrollment data is reported
              </p>
            </div>
          </div>
          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-amber-100 text-amber-800">
            Unverified
          </span>
        </div>
      </div>

      {/* Explanation + remediation */}
      <div className="px-6 pt-4 space-y-3">
        <div className="bg-amber-50 border border-amber-200 rounded-lg p-3">
          <p className="text-xs text-amber-800">
            Windows 11 25H2 and later prefer RSA-PSS for TLS client authentication. On these devices
            the TPM-backed Intune certificate cannot sign with RSA-PSS (common on older TPM firmware),
            so Windows silently withholds the certificate and every connection attempt is rejected.
            Remediation: update the TPM firmware via the vendor&apos;s tooling, or replace the device.
            This data comes from pre-authentication distress signals; serial number, manufacturer,
            and model values are self-reported and unverified.
          </p>
        </div>
      </div>

      <div className="p-6">
        <div className="overflow-x-auto">
          <table className="min-w-full text-sm">
            <thead>
              <tr className="text-left text-xs text-gray-500 uppercase tracking-wider border-b border-gray-200">
                <th className="pb-2 pr-4">Serial Number</th>
                <th className="pb-2 pr-4">Manufacturer</th>
                <th className="pb-2 pr-4">Model</th>
                <th className="pb-2 pr-4 text-right">Attempts</th>
                <th className="pb-2 pr-4">First Seen</th>
                <th className="pb-2">Last Seen</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {data.aggregated.map((row, idx) => (
                <tr key={`${row.serialNumber}|${idx}`}>
                  <td className="py-3 pr-4 font-mono text-gray-900">{row.serialNumber || "–"}</td>
                  <td className="py-3 pr-4 text-gray-700">{row.manufacturer || "–"}</td>
                  <td className="py-3 pr-4 text-gray-700">{row.model || "–"}</td>
                  <td className="py-3 pr-4 text-right font-mono text-gray-900">{row.attemptCount}</td>
                  <td className="py-3 pr-4 text-gray-500 whitespace-nowrap">{formatDate(row.firstSeen)}</td>
                  <td className="py-3 text-gray-500 whitespace-nowrap">{formatDate(row.lastSeen)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="text-xs text-gray-400 mt-3">
          {data.totalRawReports} total report{data.totalRawReports !== 1 ? "s" : ""} across {data.aggregated.length} device{data.aggregated.length !== 1 ? "s" : ""}
        </p>
      </div>
    </div>
  );
}
