/**
 * Search matching for the Distress Reports list: a trimmed, case-insensitive substring over the
 * identifying fields of a report (device, tenant, failure, certificate, source address).
 */

import type { DistressReportEntry } from "@/lib/generated/wire-types.generated";

type DistressSearchFields = Pick<
  DistressReportEntry,
  | "serialNumber"
  | "tenantId"
  | "manufacturer"
  | "model"
  | "errorType"
  | "agentVersion"
  | "osBuild"
  | "message"
  | "sourceIp"
  | "certThumbprint"
  | "certSubject"
>;

/**
 * @param errorTypeLabel the badge text the list shows for the error type ("HW Blocked"), so the
 *   admin finds a row by what is on screen as well as by the raw type.
 */
export function matchesDistressSearch(
  report: DistressSearchFields,
  query: string,
  errorTypeLabel?: string,
): boolean {
  const needle = query.trim().toLowerCase();
  if (!needle) return true;

  return [
    report.serialNumber,
    report.tenantId,
    report.manufacturer,
    report.model,
    report.errorType,
    errorTypeLabel,
    report.agentVersion,
    report.osBuild,
    report.message,
    report.sourceIp,
    report.certThumbprint,
    report.certSubject,
  ].some(v => typeof v === "string" && v.toLowerCase().includes(needle));
}
