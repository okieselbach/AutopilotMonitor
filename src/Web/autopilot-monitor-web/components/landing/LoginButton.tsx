"use client";

import { useAuth } from "../../contexts/AuthContext";
import { getPortalLoginUrl, shouldCrossOriginToPortal } from "../../lib/hostRouting";
import { getSelectedAuthApp, legacyConfigured, switchAuthApp } from "../../lib/authApp";

export function LoginButton({
  className,
  children,
  signup = false,
}: {
  className?: string;
  children: React.ReactNode;
  /**
   * Signup-funnel CTA (get-started "Sign in to get started"): routes the login through the
   * PRIMARY app registration so brand-new tenants consent the NEW app as part of the expected
   * signup flow — while the plain "Sign in" default keeps existing customers on their app
   * (dual app-reg window; localStorage is per-origin, hence the ?authapp handover to portal).
   */
  signup?: boolean;
}) {
  const { login } = useAuth();

  const handleClick = () => {
    // On the public host (www / apex), hand off to portal. so MSAL fires
    // there and the resulting token lands in portal's sessionStorage.
    // Doing the login on www first would force a second silent login on
    // portal after the post-auth redirect — and with prompt:"select_account"
    // that is not actually silent.
    if (shouldCrossOriginToPortal()) {
      window.location.href = signup && legacyConfigured()
        ? `${getPortalLoginUrl()}?authapp=primary`
        : getPortalLoginUrl();
      return;
    }
    if (signup && legacyConfigured() && getSelectedAuthApp() !== "primary") {
      // Same-origin signup click while the bundle booted with the legacy app: persist the
      // choice and reboot so the module-level MSAL instance is reconstructed for primary.
      switchAuthApp("primary");
      return;
    }
    void login();
  };

  return (
    <button onClick={handleClick} className={className}>
      {children}
    </button>
  );
}
