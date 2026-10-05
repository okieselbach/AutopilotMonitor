"use client";

import { useRef } from "react";
import type { AttachmentPack } from "@/hooks/useAttachmentPack";
import { formatBytes } from "@/lib/formatting";
import { ATTACHMENT_BUDGET_BYTES } from "@/lib/reportAttachments";

interface AttachmentFieldProps {
  label: string;
  accept: string;
  pack: AttachmentPack;
  disabled: boolean;
  /** Short notes under the field (accepted types, where the files live). */
  hints?: readonly string[];
}

/** One file list of a report form: picker, chosen files, packing state and errors. */
export function AttachmentField({ label, accept, pack, disabled, hints = [] }: AttachmentFieldProps) {
  const inputRef = useRef<HTMLInputElement>(null);

  return (
    <div className="mb-4">
      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
        {label} <span className="text-gray-400">(optional)</span>
      </label>
      <input
        ref={inputRef}
        type="file"
        accept={accept}
        multiple
        onChange={e => {
          pack.add(e.target.files ? Array.from(e.target.files) : []);
          if (inputRef.current) inputRef.current.value = "";
        }}
        className="w-full text-sm text-gray-500 dark:text-gray-400 file:mr-4 file:py-2 file:px-4 file:rounded-md file:border-0 file:text-sm file:font-semibold file:bg-blue-50 file:text-blue-700 hover:file:bg-blue-100 dark:file:bg-blue-900/40 dark:file:text-blue-300"
        disabled={disabled}
      />
      {pack.error && <p className="text-xs text-red-600 dark:text-red-400 mt-1">{pack.error}</p>}
      {pack.files.length > 0 && (
        <div className="mt-2 space-y-1">
          {pack.files.map((file, i) => (
            <div key={`${file.name}-${file.size}-${file.lastModified}`} className="flex items-center justify-between bg-gray-50 dark:bg-gray-700/50 rounded px-2 py-1 text-xs text-gray-600 dark:text-gray-300">
              <span className="truncate mr-2">{file.name} ({formatBytes(file.size)})</span>
              <button
                type="button"
                onClick={() => pack.remove(i)}
                disabled={disabled}
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
            {pack.packing
              ? "Packing…"
              : pack.packed
                ? `${pack.files.length} file${pack.files.length !== 1 ? "s" : ""} — ${formatBytes(pack.packed.bytes.length)} to send`
                : null}
          </p>
        </div>
      )}
      {hints.map(hint => (
        <p key={hint} className="text-xs text-gray-500 dark:text-gray-400 mt-1">{hint}</p>
      ))}
    </div>
  );
}

/** Packed size of all attachments against the shared budget of the report routes. */
export function AttachmentBudgetLine({ usedBytes }: { usedBytes: number }) {
  const over = usedBytes > ATTACHMENT_BUDGET_BYTES;
  return (
    <p className={`text-xs mb-4 ${over ? "text-red-600 dark:text-red-400" : "text-gray-500 dark:text-gray-400"}`}>
      Attachments: {formatBytes(usedBytes)} of {formatBytes(ATTACHMENT_BUDGET_BYTES)}
      {over ? " — remove some files or add smaller ones." : " (logs are sent compressed)"}
    </p>
  );
}
