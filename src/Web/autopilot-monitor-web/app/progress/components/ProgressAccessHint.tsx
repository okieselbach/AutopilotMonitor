import { DOCS_PATHS } from "@/lib/docsPaths";
import { SUPPORT_EMAIL } from "@/lib/supportContact";
import { DOCS_URL } from "@/lib/config";
import type { ProgressAccessHint as Hint } from "../hooks/progressAccessHint";

export const ACCESS_HINT_DOCS_URL = `${DOCS_URL}${DOCS_PATHS.progressPortalOnly}`;

const LINK_CLASS = "text-green-700 underline hover:text-green-800";

/** One muted line below the search telling a member without a role whom to ask for the full portal. */
export function ProgressAccessHint({ hint }: { hint: Hint }) {
  const learnMore = (
    <a href={ACCESS_HINT_DOCS_URL} target="_blank" rel="noopener noreferrer" className={LINK_CLASS}>
      Learn more
    </a>
  );

  if (hint.kind === "member") {
    return (
      <p className="mt-10 text-center text-sm text-gray-500">
        {"Looking for the full portal? Your organization already uses Autopilot Monitor. Ask your Autopilot Monitor admin for a role."}
        {" "}
        {learnMore}
      </p>
    );
  }

  const signedUp = hint.signedUpOn ? ` on ${hint.signedUpOn}` : "";
  return (
    <p className="mt-10 text-center text-sm text-gray-500">
      {`Looking for the full portal? Your organization signed up for Autopilot Monitor${signedUp}, but no device has been monitored recently. Ask your Autopilot Monitor admin for a role, or write to`}
      {" "}
      <a href={`mailto:${SUPPORT_EMAIL}`} className={LINK_CLASS}>
        {SUPPORT_EMAIL}
      </a>
      {" "}
      {"if no one looks after it anymore."}
      {" "}
      {learnMore}
    </p>
  );
}
