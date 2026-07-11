'use client';

/**
 * Loading states for long-running server aggregations. Both variants show a progress bar
 * sized from the last observed fetch duration (see useFetchProgress) plus an elapsed
 * counter, so the user sees the request is alive instead of staring at a bare spinner.
 * The bar is capped at 95% — it never claims "done" before the response actually lands.
 *
 * CalculatingCard: full-page card (page-level loading states).
 * CalculatingInline: compact block for in-place placeholders (tab/table bodies).
 */

function ProgressBarWithCaption({ elapsedMs, estimateMs }: { elapsedMs: number; estimateMs: number }) {
  const progressPercent = Math.min(95, (elapsedMs / estimateMs) * 100);
  const elapsedSeconds = Math.floor(elapsedMs / 1000);
  const overEstimate = elapsedMs > estimateMs;

  return (
    <>
      <div
        className="mt-6 w-full bg-gray-200 rounded-full h-2.5"
        role="progressbar"
        aria-valuenow={Math.round(progressPercent)}
        aria-valuemin={0}
        aria-valuemax={100}
      >
        <div
          className="bg-blue-600 h-2.5 rounded-full transition-all duration-500"
          style={{ width: `${progressPercent}%` }}
        ></div>
      </div>
      <p className="mt-2 text-xs text-gray-500">
        {elapsedSeconds}s elapsed
        {overEstimate
          ? ' — taking a bit longer than usual…'
          : ` · usually ~${Math.max(1, Math.round(estimateMs / 1000))}s`}
      </p>
    </>
  );
}

export function CalculatingInline({
  label,
  elapsedMs,
  estimateMs,
}: {
  label: string;
  elapsedMs: number;
  estimateMs: number;
}) {
  return (
    <div className="p-8 text-center">
      <div className="max-w-xs mx-auto">
        <p className="text-gray-500">{label}</p>
        <ProgressBarWithCaption elapsedMs={elapsedMs} estimateMs={estimateMs} />
      </div>
    </div>
  );
}

export function CalculatingCard({
  title,
  subtitle,
  elapsedMs,
  estimateMs,
}: {
  title: string;
  subtitle: string;
  elapsedMs: number;
  estimateMs: number;
}) {
  return (
    <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-100 flex items-center justify-center p-4">
      <div className="bg-white rounded-lg shadow-xl p-8 max-w-md w-full">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto"></div>
          <h2 className="mt-4 text-xl font-semibold text-gray-900">{title}</h2>
          <p className="mt-2 text-sm text-gray-600">{subtitle}</p>
          <ProgressBarWithCaption elapsedMs={elapsedMs} estimateMs={estimateMs} />
        </div>
      </div>
    </div>
  );
}
