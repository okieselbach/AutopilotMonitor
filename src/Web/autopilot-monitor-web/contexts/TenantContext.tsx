"use client";

import React, { createContext, useContext, useState, useEffect } from 'react';
import { useAuth } from './AuthContext';

interface TenantContextType {
  tenantId: string;
  setTenantId: (id: string) => void;
}

const TenantContext = createContext<TenantContextType | undefined>(undefined);

// Legacy localStorage key — older builds wrote the active tenantId here so it
// survived reloads. The current model treats the JWT `/api/auth/me` response as
// the single source of truth, so we never read from this key anymore. We still
// proactively clear it on mount to remove a stale value that an XSS payload or
// a curious user could otherwise plant — this closes a small mount-window risk
// where a child component could fetch with a tampered tenantId before the auth
// effect overrides it. The defense is belt-and-braces: the backend MUST also
// derive tenantId from the JWT, never from query/body params.
const LEGACY_TENANT_ID_LS_KEY = 'tenantId';

export function TenantProvider({ children }: { children: React.ReactNode }) {
  const { user, isAuthenticated, isLoading } = useAuth();

  // Always start empty. Consumers across the app already gate fetches on
  // `if (!tenantId) return;` so the empty initial render is the expected path.
  const [tenantId, setTenantId] = useState<string>('');

  // One-shot cleanup of any stale localStorage value left over from older builds.
  useEffect(() => {
    if (typeof window === 'undefined') return;
    try {
      if (localStorage.getItem(LEGACY_TENANT_ID_LS_KEY) !== null) {
        localStorage.removeItem(LEGACY_TENANT_ID_LS_KEY);
      }
    } catch {
      // localStorage may be disabled (privacy mode) — ignore.
    }
  }, []);

  // Mirror the tenant ID from the authenticated user — adjust-during-render instead of
  // an effect. This is the APPLICATION tenant ID, not the Azure AD tenant ID. While auth
  // is still loading the previous value is deliberately RETAINED (no flicker to '' during
  // token refresh); it only clears on a settled signed-out state.
  const authTenantId =
    !isLoading && isAuthenticated && user?.tenantId
      ? user.tenantId
      : !isLoading && !isAuthenticated
        ? ''
        : null; // null = auth still resolving, keep the current value
  if (authTenantId !== null && authTenantId !== tenantId) {
    setTenantId(authTenantId);
  }

  return (
    <TenantContext.Provider value={{ tenantId, setTenantId }}>
      {children}
    </TenantContext.Provider>
  );
}

export function useTenant() {
  const context = useContext(TenantContext);
  if (context === undefined) {
    throw new Error('useTenant must be used within a TenantProvider');
  }
  return context;
}
