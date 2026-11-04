"use client";

import Link from "next/link";
import { DOCS_PATHS } from "@/lib/docsPaths";
import { DOCS_URL } from "@/lib/config";
import { trackEvent } from "@/lib/appInsights";
import {
  ADD_ON_NOTICE_TITLE,
  ADD_ON_VALIDATIONS,
  addOnNoticeText,
  grantPermissionHref,
  type AddOnValidationKind,
} from "@/lib/addOnPermission";

interface AddOnPermissionNoticeProps {
  kind: AddOnValidationKind;
  /** When the first device was kept out, set only once the backend confirmed the missing permission. */
  affectedSince?: string | null;
  busy: boolean;
  /** Opens the usual confirmation before the validation is switched off. */
  onTurnOff: () => void;
}

/**
 * A switched-on validation lacks its optional Graph permission, so it accepts no device. Shown at
 * its switch from the moment it is on; the two ways out are granting the add-on (needs an Entra
 * Global Administrator or Privileged Role Administrator) or turning the validation off.
 */
export function AddOnPermissionNotice({ kind, affectedSince, busy, onTurnOff }: AddOnPermissionNoticeProps) {
  const { feature } = ADD_ON_VALIDATIONS[kind];
  return (
    <div
      className="bg-amber-50 border border-amber-300 rounded-lg p-3 text-sm text-amber-900 dark:bg-amber-950/30 dark:border-amber-700/50 dark:text-amber-100"
      data-testid={`addon-permission-notice-${kind}`}
    >
      <p className="font-semibold">{ADD_ON_NOTICE_TITLE}</p>
      <p className="mt-1">{addOnNoticeText(kind)}</p>
      {affectedSince && (
        <p className="mt-1 text-xs text-amber-800 dark:text-amber-200">
          Devices affected since {new Date(affectedSince).toLocaleString()}.
        </p>
      )}
      <div className="mt-2.5 flex flex-wrap items-center gap-3">
        <Link
          href={grantPermissionHref([kind])}
          onClick={() => trackEvent("addon_permission_notice_action", { action: "grant", feature })}
          className="px-3 py-1.5 text-sm font-medium text-white bg-amber-600 rounded-lg hover:bg-amber-700 transition-colors"
        >
          Grant permission
        </Link>
        <button
          type="button"
          onClick={() => {
            trackEvent("addon_permission_notice_action", { action: "turn_off", feature });
            onTurnOff();
          }}
          disabled={busy}
          className="text-sm font-medium text-amber-800 hover:text-amber-900 underline underline-offset-2 disabled:opacity-60 disabled:cursor-not-allowed dark:text-amber-200 dark:hover:text-amber-100"
        >
          Turn off
        </button>
        <a
          href={`${DOCS_URL}${DOCS_PATHS.deviceValidationPermissionMissing}`}
          target="_blank"
          rel="noopener noreferrer"
          className="text-sm font-medium text-amber-800 hover:text-amber-900 underline underline-offset-2 dark:text-amber-200 dark:hover:text-amber-100"
        >
          What this means and how to fix it
        </a>
      </div>
    </div>
  );
}
