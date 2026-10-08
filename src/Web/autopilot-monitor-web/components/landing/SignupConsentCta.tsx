"use client";

import { useState } from "react";
import { LoginButton } from "./LoginButton";
import { TermsDpaAgreementText } from "../legal/TermsDpaAgreementText";

const LINK_CLASS = "text-[var(--lp-accent-ink)] hover:opacity-80 underline";

/**
 * Get-started CTA: the signup sign-in stays inert until the visitor ticks that they accept the
 * Terms of Use and the Data Processing Agreement. The signup sign-in carries the tick to portal,
 * where the onboarding records it (lib/signupConsent.ts); a tenant onboarded through any other
 * sign-in confirms the same sentence in the portal's acceptance dialog.
 */
export function SignupConsentCta({ children }: { children?: React.ReactNode }) {
  const [accepted, setAccepted] = useState(false);

  return (
    <div data-track-section="signup" className="mt-14">
      <label className="flex items-start gap-3 max-w-xl text-sm text-[var(--lp-ink-soft)] leading-relaxed cursor-pointer select-none">
        <input
          type="checkbox"
          checked={accepted}
          onChange={(e) => setAccepted(e.target.checked)}
          className="mt-0.5 h-4 w-4 shrink-0 cursor-pointer accent-[var(--lp-accent-ink)]"
        />
        <TermsDpaAgreementText linkClassName={LINK_CLASS} trackIds={{ terms: "signup_terms", dpa: "signup_dpa" }} />
      </label>

      <div className="mt-5 flex flex-wrap items-center gap-3">
        <LoginButton
          signup
          track="signup_sign_in"
          disabled={!accepted}
          className="px-7 py-3 rounded-lg bg-[var(--lp-accent-ink)] text-white font-semibold shadow-md transition-all enabled:hover:brightness-110 enabled:hover:shadow-lg disabled:opacity-40 disabled:shadow-none disabled:cursor-not-allowed"
        >
          Sign in to get started
        </LoginButton>
        {children}
      </div>
    </div>
  );
}
