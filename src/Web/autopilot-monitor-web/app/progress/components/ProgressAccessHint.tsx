import { SUPPORT_EMAIL } from "@/lib/supportContact";
import type { ProgressAccessHint as Hint } from "../hooks/progressAccessHint";

/** One muted line below the search telling a member without a role whom to ask for the full portal. */
export function ProgressAccessHint({ hint }: { hint: Hint }) {
  if (hint.kind === "member") {
    return (
      <p className="mt-10 text-center text-sm text-gray-500">
        {"Looking for the full portal? Your organization already uses Autopilot Monitor. Ask your Autopilot Monitor admin for a role."}
      </p>
    );
  }

  const signedUp = hint.signedUpOn ? ` on ${hint.signedUpOn}` : "";
  return (
    <p className="mt-10 text-center text-sm text-gray-500">
      {`Looking for the full portal? Your organization signed up for Autopilot Monitor${signedUp}, but no device has been monitored recently. Ask your Autopilot Monitor admin for a role, or write to`}
      {" "}
      <a href={`mailto:${SUPPORT_EMAIL}`} className="text-green-700 underline hover:text-green-800">
        {SUPPORT_EMAIL}
      </a>
      {" "}
      {"if no one looks after it anymore."}
    </p>
  );
}
