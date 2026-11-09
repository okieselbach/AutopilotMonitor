"use client";

import { useSyncExternalStore } from "react";

/**
 * Unsaved settings edits, shared with the app shell outside the settings area. A module store, like
 * feedbackDialogStore, because the settings layout knows the edits while the sidebar (which marks
 * their pages) and the navbar and search (which can leave the page by code) are mounted above it.
 */

/** Where a leave started; reported with the prompt's outcome. */
export type LeaveVia = "link" | "search" | "notification" | "sign_out" | "consent";

export interface LeaveRequest {
  /** The in-app target; absent when the leave is no in-app navigation (sign-out, consent redirect). */
  href?: string;
  via: LeaveVia;
  /** Performs the leave. */
  proceed: () => void;
  /** Called when the user stays instead. */
  onStay?: () => void;
}

type LeaveHandler = (request: LeaveRequest) => void;

const NO_PAGES: readonly string[] = [];

let unsavedPages: readonly string[] = NO_PAGES;
let leaveHandler: LeaveHandler | null = null;
const listeners = new Set<() => void>();

function subscribe(onStoreChange: () => void): () => void {
  listeners.add(onStoreChange);
  return () => listeners.delete(onStoreChange);
}

/**
 * Published by the settings area: the sidebar pages holding unsaved edits, and the handler that asks
 * before code leaves them behind. An empty list withdraws both.
 */
export function publishUnsavedChanges(pages: readonly string[], handler: LeaveHandler | null): void {
  leaveHandler = pages.length > 0 ? handler : null;
  const next = pages.length > 0 ? pages : NO_PAGES;
  if (next.length === unsavedPages.length && next.every((page, i) => page === unsavedPages[i])) return;
  unsavedPages = next;
  for (const listener of [...listeners]) listener();
}

/** The sidebar pages holding unsaved edits. */
export function useUnsavedPages(): readonly string[] {
  return useSyncExternalStore(subscribe, () => unsavedPages, () => NO_PAGES);
}

/**
 * For navigation started by code (a search result, a notification, sign-out): leaves at once, or once
 * the user confirmed leaving unsaved edits behind. Links need no call; the settings area catches them.
 */
export function leaveOrAsk(request: LeaveRequest): void {
  if (leaveHandler) leaveHandler(request);
  else request.proceed();
}
