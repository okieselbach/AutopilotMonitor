"use client";

import { useAuth } from "../../contexts/AuthContext";
import { getPortalLoginUrl, shouldCrossOriginToPortal } from "../../lib/hostRouting";
import { getSelectedAuthApp, legacyConfigured, switchAuthApp } from "../../lib/authApp";
import { markSignupConsent, SIGNUP_CONSENT_PARAM } from "../../lib/signupConsent";

export function LoginButton({
  className,
  children,
  signup = false,
  disabled = false,
  track,
}: {
  className?: string;
  children: React.ReactNode;
  /** Inert until the caller's precondition holds (get-started: Terms + DPA accepted). */
  disabled?: boolean;
  /**
   * Signup-funnel CTA (get-started "Sign in to get started"): routes the login through the
   * PRIMARY app registration so brand-new tenants consent the NEW app as part of the expected
   * signup flow — while the plain "Sign in" default keeps existing customers on their app
   * (dual app-reg window; localStorage is per-origin, hence the ?authapp handover to portal).
   * It also carries the get-started Terms + DPA tick to portal (lib/signupConsent.ts), so a
   * tenant onboarded this way is not asked again in the portal.
   */
  signup?: boolean;
  /** data-track id for the anonymous marketing click count (components/MarketingTracker.tsx). */
  track?: string;
}) {
  const { login } = useAuth();

  const handleClick = () => {
    // On the public host (www / apex), hand off to portal. so MSAL fires
    // there and the resulting token lands in portal's sessionStorage.
    // Doing the login on www first would force a second silent login on
    // portal after the post-auth redirect — and with prompt:"select_account"
    // that is not actually silent.
    if (shouldCrossOriginToPortal()) {
      const url = new URL(getPortalLoginUrl());
      if (signup) {
        url.searchParams.set(SIGNUP_CONSENT_PARAM, "1");
        if (legacyConfigured()) url.searchParams.set("authapp", "primary");
      }
      window.location.href = url.toString();
      return;
    }
    if (signup) markSignupConsent();
    if (signup && legacyConfigured() && getSelectedAuthApp() !== "primary") {
      // Same-origin signup click while the bundle booted with the legacy app: persist the
      // choice and reboot so the module-level MSAL instance is reconstructed for primary.
      switchAuthApp("primary");
      return;
    }
    void login();
  };

  return (
    <button type="button" onClick={handleClick} disabled={disabled} data-track={track} className={className}>
      {children}
    </button>
  );
}
