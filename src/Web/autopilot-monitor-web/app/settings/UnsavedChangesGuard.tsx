"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { trackEvent } from "@/lib/appInsights";
import { EXPANDABLE_NAV_GROUPS } from "@/lib/globalNavConfig";
import { isMsalRedirectInFlight } from "@/lib/redirectRecovery";
import { trustedRoute } from "@/lib/routes";
import { publishUnsavedChanges, type LeaveRequest } from "@/lib/unsavedChanges";
import { useTenantConfig } from "./TenantConfigContext";
import { SECTION_FIELD_MAP } from "./sectionFieldMap";
import { leavesSettings, sameTabLinkTarget } from "./lib/leaveGuard";
import UnsavedChangesDialog from "./components/UnsavedChangesDialog";

/** Sidebar label per page, so the dialog names a page the way the sidebar does. */
const SIDEBAR_LABELS = new Map<string, string>(
  EXPANDABLE_NAV_GROUPS.flatMap((group) => group.items.flatMap((item) => item.items.map((sub) => [sub.href, sub.label] as const))),
);

/**
 * Keeps unsaved settings edits from being lost on the way out of the settings area. It publishes their
 * pages for the sidebar, asks before a link or code leaves the area, and lets the browser ask before
 * the tab closes or reloads. Moving between settings pages never asks: the edits stay in the layout.
 */
export function UnsavedChangesGuard() {
  const { unsavedSections } = useTenantConfig();
  const router = useRouter();
  const [pending, setPending] = useState<LeaveRequest | null>(null);
  // Set once the user chose to leave: from then on nothing holds the way out.
  const leavingRef = useRef(false);

  // One entry per page, in map order: the Agent Settings page holds two sections.
  const pages = useMemo(
    () => [...new Set([...unsavedSections].map((section) => SECTION_FIELD_MAP[section].page))]
      .map((href) => ({ href, label: SIDEBAR_LABELS.get(href) ?? href })),
    [unsavedSections],
  );
  const hasUnsaved = pages.length > 0;

  const requestLeave = useCallback((request: LeaveRequest) => {
    if (leavingRef.current || (request.href !== undefined && !leavesSettings(request.href))) {
      request.proceed();
      return;
    }
    setPending(request);
  }, []);

  useEffect(() => {
    publishUnsavedChanges(pages.map((page) => page.href), requestLeave);
  }, [pages, requestLeave]);
  useEffect(() => () => publishUnsavedChanges([], null), []);

  // Links of every kind (sidebar, navbar, page content): caught before the link's own handler
  // navigates. A new tab, a download or another site are left alone.
  useEffect(() => {
    if (!hasUnsaved) return;
    const onClick = (event: MouseEvent) => {
      if (leavingRef.current) return;
      const link = event.target instanceof Element ? event.target.closest("a[href]") : null;
      if (!(link instanceof HTMLAnchorElement)) return;
      const target = sameTabLinkTarget(
        event,
        { href: link.href, target: link.target, download: link.hasAttribute("download") },
        window.location.origin,
      );
      if (target === null || !leavesSettings(target)) return;
      event.preventDefault();
      event.stopPropagation();
      setPending({ href: target, via: "link", proceed: () => router.push(trustedRoute(target)) });
    };
    document.addEventListener("click", onClick, true);
    return () => document.removeEventListener("click", onClick, true);
  }, [hasUnsaved, router]);

  // Closing the tab, reloading, typing another address: the browser's own prompt.
  useEffect(() => {
    if (!hasUnsaved) return;
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      // Never hold a sign-in or sign-out redirect: a cancelled one leaves MSAL's interaction status
      // behind, and the next sign-in fails.
      if (leavingRef.current || isMsalRedirectInFlight()) return;
      event.preventDefault();
      event.returnValue = "";
    };
    window.addEventListener("beforeunload", onBeforeUnload);
    return () => window.removeEventListener("beforeunload", onBeforeUnload);
  }, [hasUnsaved]);

  const stay = useCallback(() => {
    if (!pending) return;
    trackEvent("settings_unsaved_prompt", { outcome: "stay", via: pending.via, pages: pages.length });
    setPending(null);
    pending.onStay?.();
  }, [pending, pages.length]);

  const leave = useCallback(() => {
    if (!pending) return;
    trackEvent("settings_unsaved_prompt", { outcome: "leave", via: pending.via, pages: pages.length });
    leavingRef.current = true;
    setPending(null);
    pending.proceed();
  }, [pending, pages.length]);

  if (!pending) return null;
  return <UnsavedChangesDialog pages={pages} onStay={stay} onLeave={leave} />;
}
