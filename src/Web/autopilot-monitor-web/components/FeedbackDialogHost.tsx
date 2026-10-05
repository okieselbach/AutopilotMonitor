"use client";

import { useFeedbackDialogState } from "@/lib/feedbackDialogStore";
import { FeedbackDialog } from "./FeedbackDialog";

/**
 * Mounts the "Send feedback" dialog once, in the root layout; every trigger opens it through
 * the module store. Unmounted while closed, so each opening starts with an empty form.
 */
export function FeedbackDialogHost() {
  const { open, source } = useFeedbackDialogState();
  if (!open) return null;
  return <FeedbackDialog source={source} />;
}
