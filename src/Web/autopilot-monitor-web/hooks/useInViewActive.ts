import { useEffect, useState, type RefObject } from "react";

/**
 * True while the element is on screen in a visible tab, once it has been at least
 * `startThreshold` visible. Looping animations key their timers on it, so they start when the
 * reader reaches them and stop while scrolled away or in a background tab, then resume.
 * `disabled` (reduced motion) keeps it false.
 */
export function useInViewActive(
  ref: RefObject<Element | null>,
  { startThreshold = 0.3, disabled = false }: { startThreshold?: number; disabled?: boolean } = {}
): boolean {
  const [active, setActive] = useState(false);

  useEffect(() => {
    if (disabled) return;
    const el = ref.current;
    if (!el) return;
    let started = false;
    let inView = false;
    const update = () => setActive(started && inView && !document.hidden);
    const observer = new IntersectionObserver(
      entries => {
        const latest = entries[entries.length - 1];
        inView = latest.isIntersecting;
        if (latest.intersectionRatio >= startThreshold) started = true;
        update();
      },
      { threshold: [0, startThreshold] }
    );
    observer.observe(el);
    document.addEventListener("visibilitychange", update);
    return () => {
      observer.disconnect();
      document.removeEventListener("visibilitychange", update);
    };
  }, [ref, startThreshold, disabled]);

  return active;
}
