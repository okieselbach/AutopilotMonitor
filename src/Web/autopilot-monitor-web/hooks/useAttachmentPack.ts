"use client";

import { useCallback, useRef, useState } from "react";
import { formatBytes } from "@/lib/formatting";
import { MAX_RAW_LOG_BYTES, packLogs, packScreenshots, type PackedAttachment } from "@/lib/reportAttachments";

export type AttachmentKind = "logs" | "screenshots";

export interface AttachmentPack {
  files: File[];
  /** The list packed the way it is sent; null while empty or before the first pack finished. */
  packed: PackedAttachment | null;
  packing: boolean;
  error: string | null;
  add: (newFiles: File[]) => void;
  remove: (index: number) => void;
  reset: () => void;
}

/**
 * One attachment list of a report form. It is re-packed every time the list changes, so the
 * form can show the real size against the shared budget before anything is sent.
 */
export function useAttachmentPack(kind: AttachmentKind, zipName: string): AttachmentPack {
  const [files, setFiles] = useState<File[]>([]);
  const [packed, setPacked] = useState<PackedAttachment | null>(null);
  const [packing, setPacking] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // A later change wins: results of an earlier, slower pack are dropped.
  const generation = useRef(0);

  const repack = useCallback(async (next: File[]) => {
    const current = ++generation.current;
    setFiles(next);
    setError(null);
    if (next.length === 0) {
      setPacked(null);
      setPacking(false);
      return;
    }
    setPacking(true);
    try {
      const result = kind === "logs" ? await packLogs(next, zipName) : await packScreenshots(next, zipName);
      if (current === generation.current) setPacked(result);
    } catch {
      if (current === generation.current) {
        setPacked(null);
        setError("The files could not be read. Remove them and add them again.");
      }
    } finally {
      if (current === generation.current) setPacking(false);
    }
  }, [kind, zipName]);

  const add = useCallback((newFiles: File[]) => {
    const merged = [...files];
    for (const f of newFiles) {
      if (!merged.some(e => e.name === f.name && e.size === f.size && e.lastModified === f.lastModified)) {
        merged.push(f);
      }
    }
    if (kind === "logs") {
      const raw = merged.reduce((sum, f) => sum + f.size, 0);
      if (raw > MAX_RAW_LOG_BYTES) {
        setError(`These logs add up to ${formatBytes(raw)}; the browser packs at most ${formatBytes(MAX_RAW_LOG_BYTES)}.`);
        return;
      }
    }
    void repack(merged);
  }, [files, kind, repack]);

  const remove = useCallback((index: number) => {
    void repack(files.filter((_, i) => i !== index));
  }, [files, repack]);

  const reset = useCallback(() => {
    void repack([]);
  }, [repack]);

  return { files, packed, packing, error, add, remove, reset };
}
