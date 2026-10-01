import { useCallback, useState } from "react";

/**
 * Clipboard copy with a short-lived "copied" marker. `copied` holds the key of the last value
 * copied for two seconds, so one hook serves several copy buttons on the same screen.
 */
export function useCopy() {
  const [copied, setCopied] = useState<string | null>(null);
  const copy = useCallback(async (value: string, key: string) => {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(key);
      setTimeout(() => setCopied(null), 2000);
    } catch {
      setCopied(null);
    }
  }, []);
  return { copied, copy };
}
