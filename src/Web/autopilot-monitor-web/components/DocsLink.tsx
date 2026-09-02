import { DOCS_URL } from "@/utils/config";

interface DocsLinkProps {
  /** Path below the published docs root, with a leading slash (e.g. "/reference/settings#notifications"). */
  path: string;
  /** Full label, shown from the `sm` breakpoint upwards. */
  label?: string;
  /** Label for narrow screens; defaults to the full label when omitted. */
  shortLabel?: string;
  className?: string;
}

const DEFAULT_LABEL = "Read the docs";
const DEFAULT_SHORT_LABEL = "Docs";

/**
 * Small "Read the docs" link that opens the matching customer documentation page in a new tab.
 * Narrow screens get the one-word "Docs" so the label never squeezes the section title.
 * Neutral colouring so it sits on every section-header tone, including the red danger zone.
 */
export function DocsLink({
  path,
  label = DEFAULT_LABEL,
  shortLabel = label === DEFAULT_LABEL ? DEFAULT_SHORT_LABEL : label,
  className = "",
}: DocsLinkProps) {
  return (
    <a
      href={`${DOCS_URL}${path}`}
      target="_blank"
      rel="noopener noreferrer"
      aria-label={`${label} (opens in a new tab)`}
      className={`inline-flex flex-shrink-0 items-center gap-1 whitespace-nowrap text-xs font-medium text-gray-500 hover:text-gray-900 hover:underline underline-offset-2 dark:text-gray-400 dark:hover:text-gray-100 ${className}`}
    >
      {shortLabel === label ? (
        label
      ) : (
        <>
          <span className="sm:hidden">{shortLabel}</span>
          <span className="hidden sm:inline">{label}</span>
        </>
      )}
      <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14" />
      </svg>
    </a>
  );
}
