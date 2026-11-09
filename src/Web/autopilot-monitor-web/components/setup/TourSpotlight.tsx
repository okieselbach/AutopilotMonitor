"use client";

import { useEffect, useRef, useState } from "react";
import { ModalPortal } from "@/components/ModalPortal";
import { placeBubble, scrollTopFor, type Box, type BubblePlacement } from "@/lib/tourPlacement";

interface TourSpotlightProps {
  /** `data-tour` value of the element to highlight; the first one present wins. */
  anchors: readonly string[];
  /** Accessible name of the speech bubble. */
  label: string;
  /** A solid ring: the step is done. */
  done?: boolean;
  onClose: () => void;
  children: React.ReactNode;
}

const PADDING = 8;
/** The page may still be loading its data; give up quietly after this long. */
const FIND_TIMEOUT_MS = 15_000;

function findTarget(anchors: readonly string[]): HTMLElement | null {
  for (const anchor of anchors) {
    const element = document.querySelector<HTMLElement>(`[data-tour="${anchor}"]`);
    if (element) return element;
  }
  return null;
}

/** Bottom edge of the sticky top bars (view-as bar and navbar). */
function topInset(): number {
  const navbar = document.querySelector<HTMLElement>("nav.sticky");
  return navbar ? Math.max(0, navbar.getBoundingClientRect().bottom) : 56;
}

/**
 * Dims the page except one element, rings it and points a speech bubble at it. Clicks outside the
 * element and the bubble are blocked, Escape closes. Positions follow scroll, resize and layout
 * changes of the element.
 */
export function TourSpotlight({ anchors, label, done = false, onClose, children }: TourSpotlightProps) {
  const bubbleRef = useRef<HTMLDivElement>(null);
  const [spot, setSpot] = useState<Box | null>(null);
  const [placement, setPlacement] = useState<BubblePlacement | null>(null);
  const anchorKey = anchors.join("|");

  useEffect(() => {
    const list = anchorKey.split("|");
    const startedAt = Date.now();
    let target: HTMLElement | null = null;
    let frame = 0;
    let scrolled = false;
    const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    const resize = new ResizeObserver(() => schedule());

    function measure() {
      frame = 0;
      if (!target || !target.isConnected) {
        target = findTarget(list);
        if (!target) {
          if (Date.now() - startedAt < FIND_TIMEOUT_MS) frame = window.requestAnimationFrame(measure);
          return;
        }
        resize.observe(target);
        if (bubbleRef.current) resize.observe(bubbleRef.current);
      }
      const rect = target.getBoundingClientRect();
      const box: Box = {
        left: rect.left - PADDING,
        top: rect.top - PADDING,
        width: rect.width + 2 * PADDING,
        height: rect.height + 2 * PADDING,
      };
      const viewport = { width: window.innerWidth, height: window.innerHeight, topInset: topInset() };
      const bubble = { width: bubbleRef.current?.offsetWidth ?? 380, height: bubbleRef.current?.offsetHeight ?? 260 };
      if (!scrolled) {
        scrolled = true;
        window.scrollTo({ top: scrollTopFor(box, bubble, viewport, window.scrollY), behavior: reduceMotion ? "auto" : "smooth" });
      }
      setSpot(box);
      setPlacement(placeBubble(box, bubble, viewport));
    }

    function schedule() {
      if (!frame) frame = window.requestAnimationFrame(measure);
    }

    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };

    schedule();
    window.addEventListener("scroll", schedule, { capture: true, passive: true });
    window.addEventListener("resize", schedule);
    window.addEventListener("keydown", onKey);
    return () => {
      if (frame) window.cancelAnimationFrame(frame);
      resize.disconnect();
      window.removeEventListener("scroll", schedule, { capture: true });
      window.removeEventListener("resize", schedule);
      window.removeEventListener("keydown", onKey);
    };
  }, [anchorKey, onClose]);

  const blocker = "fixed pointer-events-auto";
  const arrowStyle: React.CSSProperties | null = placement
    ? placement.side === "bottom" ? { top: -6, left: placement.arrow }
      : placement.side === "top" ? { bottom: -6, left: placement.arrow }
        : placement.side === "right" ? { left: -6, top: placement.arrow }
          : placement.side === "left" ? { right: -6, top: placement.arrow }
            : null
    : null;

  return (
    <ModalPortal>
      <div className="fixed inset-0 z-40 pointer-events-none">
        {spot && (
          <>
            <div className={`${blocker} left-0 right-0 top-0`} style={{ height: Math.max(0, spot.top) }} />
            <div className={`${blocker} left-0 right-0 bottom-0`} style={{ top: spot.top + spot.height }} />
            <div className={`${blocker} left-0`} style={{ top: spot.top, height: spot.height, width: Math.max(0, spot.left) }} />
            <div className={`${blocker} right-0`} style={{ top: spot.top, height: spot.height, left: spot.left + spot.width }} />
            <div
              className="fixed rounded-xl setup-tour-spot"
              style={{ left: spot.left, top: spot.top, width: spot.width, height: spot.height }}
            >
              <svg className="absolute -left-1.5 -top-1.5 overflow-visible" width={spot.width + 12} height={spot.height + 12} aria-hidden="true">
                <rect
                  x={1}
                  y={1}
                  width={spot.width + 10}
                  height={spot.height + 10}
                  rx={13}
                  fill="none"
                  strokeWidth={2}
                  className={done ? "stroke-green-600" : "stroke-green-500 setup-tour-ring"}
                  strokeDasharray={done ? undefined : "8 6"}
                />
              </svg>
            </div>
          </>
        )}
        {/* One width and one height cap in every placement: the size decides the placement, so a size
            that followed the placement would flip it back and forth. On a phone min() is the full width. */}
        <div
          ref={bubbleRef}
          role="dialog"
          aria-label={label}
          className="fixed pointer-events-auto w-[min(400px,calc(100vw-24px))] rounded-xl bg-white shadow-2xl ring-1 ring-gray-200 dark:bg-gray-800 dark:ring-gray-700"
          style={placement ? { left: placement.left, top: placement.top } : { left: -9999, top: 0 }}
        >
          {arrowStyle && (
            <span
              aria-hidden="true"
              // A square with two borders, turned so the bordered corner points at the target.
              className="absolute h-3 w-3 border-l border-t border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-800"
              style={{
                ...arrowStyle,
                transform: `rotate(${placement?.side === "bottom" ? 45 : placement?.side === "top" ? 225 : placement?.side === "right" ? -45 : 135}deg)`,
              }}
            />
          )}
          <div className="max-h-[60vh] overflow-y-auto rounded-xl p-4">{children}</div>
        </div>
      </div>
    </ModalPortal>
  );
}
