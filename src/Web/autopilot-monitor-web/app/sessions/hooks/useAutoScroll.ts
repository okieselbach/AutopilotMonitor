"use client";
import { useState, useRef, useEffect } from "react";
import { EnrollmentEvent } from "@/types";

export function useAutoScroll(events: EnrollmentEvent[]) {
  const [autoScroll, setAutoScroll] = useState(false);
  const isNearBottomRef = useRef(false);
  const isProgrammaticScrollRef = useRef(false);

  // Auto-scroll: track whether user is near the bottom (only while feature is enabled).
  // Uses a ref so scroll events don't cause re-renders.
  useEffect(() => {
    if (!autoScroll) return;
    const handleScroll = () => {
      const distanceFromBottom = document.body.scrollHeight - window.scrollY - window.innerHeight;
      if (isProgrammaticScrollRef.current) {
        // During a programmatic scroll animation, only allow flipping to true
        // (we reached the bottom) — never flip to false (content grew mid-animation).
        if (distanceFromBottom <= 150) isNearBottomRef.current = true;
      } else {
        isNearBottomRef.current = distanceFromBottom <= 150;
      }
    };
    window.addEventListener('scroll', handleScroll, { passive: true });
    return () => window.removeEventListener('scroll', handleScroll);
  }, [autoScroll]);

  // Auto-scroll: when new events arrive, scroll to bottom only if user is already near the bottom.
  useEffect(() => {
    if (!autoScroll || events.length === 0 || !isNearBottomRef.current) return;
    isProgrammaticScrollRef.current = true;
    const timer = setTimeout(() => {
      window.scrollTo({ top: document.body.scrollHeight, behavior: 'smooth' });
      // Keep the guard up long enough for the smooth scroll animation to finish.
      setTimeout(() => { isProgrammaticScrollRef.current = false; }, 600);
    }, 150);
    return () => { clearTimeout(timer); isProgrammaticScrollRef.current = false; };
  }, [events, autoScroll]);

  const handleAutoScrollToggle = () => {
    setAutoScroll(prev => {
      const next = !prev;
      if (next) {
        // Enabling: immediately scroll to bottom and mark as near-bottom
        isNearBottomRef.current = true;
        isProgrammaticScrollRef.current = true;
        setTimeout(() => {
          window.scrollTo({ top: document.body.scrollHeight, behavior: 'smooth' });
          setTimeout(() => { isProgrammaticScrollRef.current = false; }, 600);
        }, 50);
      }
      return next;
    });
  };

  return { autoScroll, handleAutoScrollToggle };
}
