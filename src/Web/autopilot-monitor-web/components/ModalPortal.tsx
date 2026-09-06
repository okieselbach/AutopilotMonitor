"use client";

import { useSyncExternalStore } from "react";
import { createPortal } from "react-dom";

const subscribeNoop = () => () => {};

/**
 * Renders a modal overlay as a direct child of <body>.
 *
 * Overlays are `position: fixed`, but they still take part in their parent's
 * box model: a `space-y-*` container hands every non-first child a
 * `margin-top`, which shifts a fixed overlay down from the viewport edge and
 * leaves the navbar un-dimmed. Portaling the overlay out of the content tree
 * removes that coupling (and any future stacking-context surprise) for every
 * dialog at once. React event bubbling still follows the component tree, so
 * handlers on ancestors behave exactly as before.
 *
 * `document` does not exist during prerender (output: 'export'), so the first
 * render on the server/hydration pass yields null and the portal mounts on the
 * client afterwards.
 */
export function ModalPortal({ children }: { children: React.ReactNode }) {
  const mounted = useSyncExternalStore(subscribeNoop, () => true, () => false);
  if (!mounted) return null;
  return createPortal(children, document.body);
}
