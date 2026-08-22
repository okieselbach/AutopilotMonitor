// Pure predicates extracted from `components/DownloadProgress.tsx` so the filter
// behaviour can be unit-tested without a DOM/JSX harness.

export interface DownloadFilterInput {
  bytesDownloaded: number;
  bytesTotal: number;
  status: string;
  isDownloadStartEvent: boolean;
  isSkippedEvent: boolean;
  progressPercent: number;
}

// Returns true if the event should be dropped because its declared total size is
// below 1 KB and the event isn't a terminal/skip/start signal.
//
// V2 emits bytesTotal=100 for WinGet/Store apps (e.g. Company Portal) which have
// no DO byte progress. Without exempting `app_download_started`, those apps would
// never appear in the UI — V1 had emitted bytesTotal=0 and slipped through the
// next filter instead.
export function shouldSkipLowBytesTotal(input: DownloadFilterInput): boolean {
  const { bytesTotal, status, isSkippedEvent, isDownloadStartEvent } = input;
  return (
    bytesTotal > 0 &&
    bytesTotal < 1024 &&
    status !== "completed" &&
    status !== "failed" &&
    !isSkippedEvent &&
    !isDownloadStartEvent
  );
}

// Returns true if the event carries evidence that package content actually flowed (or is
// about to): a download-start signal, byte counters, or download progress. The terminal
// download_progress event IME emits at enforcement end passes the two skip-filters below via
// status === "completed" even when nothing was ever downloaded — Store/WinGet uninstalls
// (session 502274b4: Xbox, Xbox Game Bar) arrive as 0/0 bytes, 0 %, "completed" and would
// otherwise render as phantom completed downloads. Win32 uninstalls DO re-download the full
// package and keep their row through real byte counters.
export function hasByteActivity(input: DownloadFilterInput): boolean {
  const { bytesDownloaded, bytesTotal, isDownloadStartEvent, progressPercent } = input;
  return isDownloadStartEvent || bytesDownloaded > 0 || bytesTotal > 0 || progressPercent > 0;
}

// Returns true if the event has no activity (zero bytes, not started, not progressed)
// and should not yet appear as a download row.
export function shouldSkipNoActivity(input: DownloadFilterInput): boolean {
  const { bytesDownloaded, bytesTotal, status, isDownloadStartEvent, isSkippedEvent, progressPercent } = input;
  return (
    bytesDownloaded === 0 &&
    bytesTotal === 0 &&
    status !== "completed" &&
    status !== "failed" &&
    !isDownloadStartEvent &&
    !isSkippedEvent &&
    progressPercent < 100
  );
}
