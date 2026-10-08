import { DOCS_URL } from "@/utils/config";

const TERMS_PATH = "/terms/";
const DPA_URL = `${DOCS_URL}/legal/data-privacy-agreement-dpa`;

/**
 * The one wording of the Terms + DPA tick: the get-started CTA and the portal's acceptance dialog
 * show the same sentence. Both links open in a new tab so the tick survives reading them.
 */
export function TermsDpaAgreementText({
  linkClassName,
  trackIds,
}: {
  linkClassName: string;
  /** data-track ids for the marketing click count; the portal dialog sends none. */
  trackIds?: { terms: string; dpa: string };
}) {
  return (
    <span>
      I agree to the{" "}
      <a href={TERMS_PATH} target="_blank" rel="noopener noreferrer" data-track={trackIds?.terms} className={linkClassName}>
        Terms of Use
      </a>{" "}
      and the{" "}
      <a href={DPA_URL} target="_blank" rel="noopener noreferrer" data-track={trackIds?.dpa} className={linkClassName}>
        Data Processing Agreement
      </a>
      .
    </span>
  );
}
