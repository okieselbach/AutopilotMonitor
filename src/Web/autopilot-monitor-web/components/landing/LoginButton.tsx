"use client";

import { useAuth } from "../../contexts/AuthContext";
import { getPortalLoginUrl, shouldCrossOriginToPortal } from "../../lib/hostRouting";

export function LoginButton({
  className,
  children,
}: {
  className?: string;
  children: React.ReactNode;
}) {
  const { login } = useAuth();

  const handleClick = () => {
    // On the public host (www / apex), hand off to portal. so MSAL fires
    // there and the resulting token lands in portal's sessionStorage.
    // Doing the login on www first would force a second silent login on
    // portal after the post-auth redirect — and with prompt:"select_account"
    // that is not actually silent.
    if (shouldCrossOriginToPortal()) {
      window.location.href = getPortalLoginUrl();
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
