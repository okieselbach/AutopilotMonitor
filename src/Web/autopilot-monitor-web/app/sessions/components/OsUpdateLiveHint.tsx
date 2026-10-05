"use client";

import { useEffect, useState } from "react";
import { deviceNowMs, type OsUpdateLiveFacts, type OsUpdateLiveState } from "@/lib/osUpdateLive";
import { osUpdateLiveHintText } from "./osUpdateLiveLogic";

const TICK_MS = 30_000;

// The colours of the time attribution's Windows Update and wait slices.
const TONE: Record<OsUpdateLiveState, { box: string; dot: string; live: boolean }> = {
  updating: { box: "bg-cyan-50 border-cyan-200 text-cyan-800", dot: "bg-cyan-500", live: true },
  awaiting_sign_in: { box: "bg-sky-50 border-sky-200 text-sky-800", dot: "bg-sky-400", live: true },
  ended: { box: "bg-cyan-50 border-cyan-200 text-cyan-800", dot: "bg-cyan-500", live: false },
};

/**
 * The OOBE quality update of a running session, in the Enrollment Progress card: while it runs,
 * while the device waits for its user after the update's restart, or after it failed or was
 * skipped. Once the session ended, the time attribution below shows the update instead.
 */
export default function OsUpdateLiveHint({ facts }: { facts: OsUpdateLiveFacts }) {
  // Minutes are enough; the label's own thresholds are minutes too.
  const [browserNowMs, setBrowserNowMs] = useState(() => Date.now());
  useEffect(() => {
    const id = setInterval(() => setBrowserNowMs(Date.now()), TICK_MS);
    return () => clearInterval(id);
  }, []);

  const text = osUpdateLiveHintText(facts, deviceNowMs(facts, browserNowMs));
  if (!text) return null;
  const tone = TONE[text.state];

  return (
    <div className={`mt-2 flex items-start gap-2 rounded-md border px-3 py-2 text-sm ${tone.box}`}>
      {/* Beside the first line, also when the text wraps on a narrow screen. */}
      <span className={`mt-1.5 h-2 w-2 shrink-0 rounded-full ${tone.dot} ${tone.live ? "motion-safe:animate-pulse" : ""}`} aria-hidden="true" />
      <div className="flex min-w-0 flex-wrap items-baseline gap-x-2 gap-y-0.5">
        <span>
          {/* Only the phrases are live regions: the ticking time would be announced every minute. */}
          <span role="status" className="font-medium">{text.phrase}</span>
          {text.elapsed}
          <span role="status">{text.restarting ? " · restarting" : ""}</span>
        </span>
        {text.detail && <span className="text-xs text-gray-500">{text.detail}</span>}
        {text.silence && <span className="text-xs text-gray-500">{text.silence}</span>}
      </div>
    </div>
  );
}
