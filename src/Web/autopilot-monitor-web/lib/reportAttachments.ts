import { SHARED_MANIFEST } from "@/lib/generated/shared-manifests.generated";

/**
 * Attachments of the two report forms (Submit Logs, Report Session): packed the way they are
 * sent, measured against one budget derived from the backend's body cap.
 *
 * Both routes take base64 attachments inside a JSON body of at most
 * `submissionLimits.reportRequestMaxBytes`. The budget counts the bytes actually sent: logs
 * travel DEFLATE-zipped (text shrinks several-fold), screenshots as they are.
 */

/** The report routes' body cap, shared with the backend through the manifest. */
export const REPORT_REQUEST_MAX_BYTES: number = SHARED_MANIFEST.submissionLimits.reportRequestMaxBytes;

/** Room kept in the body for JSON keys, the comment and the contact address. */
const BODY_OVERHEAD_BYTES = 256 * 1024;

/**
 * Bytes of packed attachments one report may carry: what fits in the body cap once base64
 * (+33 %) and the overhead are accounted for. The same number in both forms.
 */
export const ATTACHMENT_BUDGET_BYTES = Math.floor(((REPORT_REQUEST_MAX_BYTES - BODY_OVERHEAD_BYTES) * 3) / 4);

/** Raw log input the browser packs at most — a guard for the tab, not a wire limit. */
export const MAX_RAW_LOG_BYTES = 100 * 1024 * 1024;

/** The part of `File` the packers use, so tests can pass plain objects. */
export interface NamedBlob {
  readonly name: string;
  readonly size: number;
  arrayBuffer(): Promise<ArrayBuffer>;
}

export interface PackedAttachment {
  bytes: Uint8Array;
  fileName: string;
}

/**
 * Entry names that are unique inside one zip (case-insensitive): a second "IME.log" becomes
 * "IME (2).log" instead of silently replacing the first.
 */
export function uniqueEntryNames(names: readonly string[]): string[] {
  const taken = new Set<string>();
  return names.map(name => {
    let candidate = name;
    for (let n = 2; taken.has(candidate.toLowerCase()); n++) {
      const dot = name.lastIndexOf(".");
      candidate = dot > 0 ? `${name.slice(0, dot)} (${n})${name.slice(dot)}` : `${name} (${n})`;
    }
    taken.add(candidate.toLowerCase());
    return candidate;
  });
}

async function zipFiles(files: readonly NamedBlob[], level: 0 | 6): Promise<Uint8Array> {
  // fflate is only needed once files are attached — loaded on demand to keep it out of the route chunk.
  const { zipSync } = await import("fflate");
  const names = uniqueEntryNames(files.map(f => f.name));
  const entries: Record<string, Uint8Array> = {};
  for (let i = 0; i < files.length; i++) {
    entries[names[i]] = new Uint8Array(await files[i].arrayBuffer());
  }
  return zipSync(entries, { level });
}

/**
 * Logs and state files: always DEFLATE-zipped, except a single file that already is a .zip,
 * which goes as it is. One file keeps its name (`IME.log.zip`), several become `zipName`.
 */
export async function packLogs(files: readonly NamedBlob[], zipName: string): Promise<PackedAttachment | null> {
  if (files.length === 0) return null;
  if (files.length === 1 && /\.zip$/i.test(files[0].name)) {
    return { bytes: new Uint8Array(await files[0].arrayBuffer()), fileName: files[0].name };
  }
  return {
    bytes: await zipFiles(files, 6),
    fileName: files.length === 1 ? `${files[0].name}.zip` : zipName,
  };
}

/** Screenshots: one image as it is, several in an uncompressed zip (images do not shrink). */
export async function packScreenshots(files: readonly NamedBlob[], zipName: string): Promise<PackedAttachment | null> {
  if (files.length === 0) return null;
  if (files.length === 1) {
    return { bytes: new Uint8Array(await files[0].arrayBuffer()), fileName: files[0].name };
  }
  return { bytes: await zipFiles(files, 0), fileName: zipName };
}

/** Packed bytes of the given attachments together. */
export function packedSize(...attachments: readonly (PackedAttachment | null)[]): number {
  return attachments.reduce((sum, a) => sum + (a?.bytes.length ?? 0), 0);
}

/** UTF-8 size of a request body — the number the backend compares against its cap. */
export function utf8ByteLength(text: string): number {
  return new TextEncoder().encode(text).length;
}

/** True when a serialized report body fits the backend's cap. */
export function fitsReportRequest(body: string): boolean {
  return utf8ByteLength(body) <= REPORT_REQUEST_MAX_BYTES;
}
