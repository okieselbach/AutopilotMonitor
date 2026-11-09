"use client";

import { useEffect, useRef } from "react";
import type { Route } from "next";
import { ModalPortal } from "@/components/ModalPortal";
import { NavLink } from "@/components/NavLink";

interface UnsavedChangesDialogProps {
  /** The settings pages holding unsaved edits, named as in the sidebar. */
  pages: readonly { href: Route; label: string }[];
  /** Stay on the page; also Escape, a click beside the dialog, and following a page link. */
  onStay: () => void;
  /** Leave and lose the edits. */
  onLeave: () => void;
}

/** Asks before leaving the settings area with edits that are not saved yet. */
export default function UnsavedChangesDialog({ pages, onStay, onLeave }: UnsavedChangesDialogProps) {
  const stayButton = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    stayButton.current?.focus();
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") onStay();
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [onStay]);

  return (
    <ModalPortal>
      <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4" onClick={onStay}>
        <div
          role="dialog"
          aria-modal="true"
          aria-labelledby="unsaved-changes-title"
          className="bg-white rounded-lg shadow-xl max-w-md w-full p-6"
          onClick={(event) => event.stopPropagation()}
        >
          <h3 id="unsaved-changes-title" className="text-lg font-semibold text-gray-900">
            Leave without saving?
          </h3>
          <p className="mt-2 text-sm text-gray-600">These pages have changes that are not saved yet:</p>
          <ul className="mt-2 space-y-1">
            {pages.map((page) => (
              <li key={page.href}>
                <NavLink
                  href={page.href}
                  onClick={onStay}
                  className="inline-flex items-center gap-2 text-sm font-medium text-gray-900 hover:underline"
                >
                  <span className="h-2 w-2 shrink-0 rounded-full bg-amber-500" aria-hidden="true" />
                  {page.label}
                </NavLink>
              </li>
            ))}
          </ul>
          <p className="mt-3 text-sm text-gray-600">Save them on their page, or leave and lose them.</p>
          <div className="mt-6 flex flex-wrap justify-end gap-3">
            <button
              type="button"
              onClick={onLeave}
              className="px-4 py-2 rounded-md text-sm font-medium border border-gray-300 text-red-600 hover:bg-red-50"
            >
              Leave without saving
            </button>
            <button
              ref={stayButton}
              type="button"
              onClick={onStay}
              className="px-4 py-2 rounded-md text-sm font-medium text-white bg-green-600 hover:bg-green-700"
            >
              Stay
            </button>
          </div>
        </div>
      </div>
    </ModalPortal>
  );
}
