"use client";

import { useEffect } from "react";
import Link from "next/link";
import { DOCS_URL } from "@/lib/config";
import { DOCS_PATHS } from "@/lib/docsPaths";
import { trackEvent } from "@/lib/appInsights";
import {
  ADD_ON_VALIDATIONS,
  addOnBannerCause,
  addOnBannerNextStep,
  addOnBannerTitle,
  type AddOnValidationKind,
} from "@/lib/addOnPermission";

interface AddOnPermissionBannerProps {
  /** Validations whose missing permission is confirmed, in chain order (never empty). */
  kinds: AddOnValidationKind[];
  /** They are all the tenant's validations, so no device can be validated (feature-flags `addOnPermissionMissingBlocksAll`). */
  blocksAll: boolean;
  /** Tenant admins and Global Admins only: a delegated admin's link would target their own tenant. */
  canFix: boolean;
}

/**
 * A switched-on validation kept devices out and still lacks its optional Graph permission 15
 * minutes later (feature-flags `cloudPcPermissionMissing` / `intuneDeviceBindingPermissionMissing`).
 * Not dismissable: it goes away with the grant or once the validation is switched off.
 */
export function AddOnPermissionBanner({ kinds, blocksAll, canFix }: AddOnPermissionBannerProps) {
  const features = kinds.map((k) => ADD_ON_VALIDATIONS[k].feature).join(",");
  useEffect(() => {
    trackEvent("addon_permission_banner_shown", { features, blocksAll });
  }, [features, blocksAll]);

  return (
    <div className="mb-6 bg-red-600 border-2 border-red-700 rounded-xl p-5 shadow-lg">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div className="flex items-start gap-3">
          <svg className="w-6 h-6 text-white mt-0.5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
          </svg>
          <div>
            <p className="text-base font-bold text-white">{addOnBannerTitle(blocksAll)}</p>
            <p className="text-sm text-red-100 mt-0.5">
              {addOnBannerCause(kinds, blocksAll)} {addOnBannerNextStep(kinds)}{" "}
              <a
                href={`${DOCS_URL}${DOCS_PATHS.deviceValidationPermissionMissing}`}
                target="_blank"
                rel="noopener noreferrer"
                className="underline font-medium text-white hover:text-red-100"
                onClick={() => trackEvent("addon_permission_banner_clicked", { link: "docs", features })}
              >
                What this means and how to fix it
              </a>
            </p>
          </div>
        </div>
        {canFix && (
          <Link
            href={`/settings/tenant/autopilot#${ADD_ON_VALIDATIONS[kinds[0]].anchor}`}
            className="shrink-0 inline-flex items-center justify-center gap-2 bg-white text-red-700 font-semibold text-sm px-4 py-2 rounded-lg hover:bg-red-50 transition-colors"
            onClick={() => trackEvent("addon_permission_banner_clicked", { link: "settings", features })}
          >
            Open settings
          </Link>
        )}
      </div>
    </div>
  );
}
