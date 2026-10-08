"use client";

import { useEffect, useRef } from "react";
import { ModalPortal } from "@/components/ModalPortal";
import { useLatest } from "@/hooks/useLatest";
import type { HistoryEntry } from "@/lib/push/pushCore";

interface EntryActionSheetProps {
  entry: HistoryEntry;
  /** Removes the entry from the local history; nothing leaves the device. */
  onDelete: () => void;
  onClose: () => void;
}

/**
 * The actions of one history entry, opened by a long press or a right-click on its card: a
 * bottom sheet on the phone, a centred panel on a wide screen. Escape and a tap beside the
 * panel close it; focus moves into the sheet and returns to the card afterwards.
 *
 * The sheet opens while the finger is still on the card, and the release may land as a click
 * on whatever is under it now — the overlay or even the Delete button. A click therefore only
 * counts after a pointer went down (or a key was pressed) inside the sheet itself.
 */
export function EntryActionSheet({ entry, onDelete, onClose }: EntryActionSheetProps) {
  const panelRef = useRef<HTMLDivElement>(null);
  const armedRef = useRef(false);
  const onCloseRef = useLatest(onClose);

  // Focus the dialog on open and give it back to the card on close.
  useEffect(() => {
    const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    panelRef.current?.focus();
    return () => {
      if (previous?.isConnected) previous.focus();
    };
  }, []);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") onCloseRef.current();
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [onCloseRef]);

  const arm = () => {
    armedRef.current = true;
  };
  const armed = () => armedRef.current;

  return (
    <ModalPortal>
      <div
        className="fixed inset-0 bg-black bg-opacity-50 z-50 flex items-end sm:items-center justify-center"
        onPointerDown={arm}
        onKeyDown={arm}
        onClick={() => {
          if (armed()) onClose();
        }}
      >
        <div
          ref={panelRef}
          role="dialog"
          aria-modal="true"
          aria-label="Alert actions"
          tabIndex={-1}
          className="bg-white rounded-t-2xl sm:rounded-lg shadow-xl w-full max-w-md p-4 space-y-3 focus:outline-none"
          onClick={(event) => event.stopPropagation()}
        >
          <p className="text-sm font-medium text-gray-900 truncate">{entry.title}</p>
          <div className="space-y-2">
            <button
              type="button"
              onClick={() => {
                if (armed()) onDelete();
              }}
              className="w-full px-4 py-3 bg-white border border-red-300 text-red-700 rounded-lg hover:bg-red-50 transition-colors text-sm font-medium"
            >
              Delete
            </button>
            {entry.portalUrl && (
              <a
                href={entry.portalUrl}
                target="_blank"
                rel="noopener noreferrer"
                onClick={(event) => {
                  if (!armed()) {
                    event.preventDefault();
                    return;
                  }
                  onClose();
                }}
                className="block w-full px-4 py-3 bg-white border border-gray-300 text-gray-700 rounded-lg hover:bg-gray-50 transition-colors text-sm font-medium text-center"
              >
                Open in portal
              </a>
            )}
            <button
              type="button"
              onClick={() => {
                if (armed()) onClose();
              }}
              className="w-full px-4 py-3 text-sm text-gray-600 hover:text-gray-900"
            >
              Cancel
            </button>
          </div>
        </div>
      </div>
    </ModalPortal>
  );
}
