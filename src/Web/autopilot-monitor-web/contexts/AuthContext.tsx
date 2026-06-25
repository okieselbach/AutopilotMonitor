"use client";

import React, { createContext, useContext, useEffect, useState, useCallback, useRef } from 'react';
import { PublicClientApplication, AccountInfo, InteractionStatus, InteractionRequiredAuthError, BrowserAuthError } from '@azure/msal-browser';
import { MsalProvider, useMsal, useIsAuthenticated } from '@azure/msal-react';
import { msalConfig, loginRequest, apiRequest } from '@/lib/msalConfig';
import { api } from '@/lib/api';

// Initialize MSAL instance
const msalInstance = new PublicClientApplication(msalConfig);

// Track MSAL initialization state so components can wait for it.
let msalReady = false;

// Prefetch: store the in-flight auth/me Promise (not the resolved value) so
// fetchUserInfo can await it instead of racing a duplicate fetch when the
// prefetch hasn't settled yet. Resolves to the JSON body, or null if the
// fetch failed / token acquisition failed.
// Runs as a fire-and-forget side-effect — MUST NOT block msalInitPromise,
// otherwise a cold backend would keep the UI on a white screen.
let prefetchedAuthMePromise: Promise<Record<string, unknown> | null> | null = null;

const msalInitPromise = msalInstance
  .initialize()
  .then(() => msalInstance.handleRedirectPromise())
  .then(() => {
    msalReady = true;

    // Kick off prefetch while React is still mounting. fetchUserInfo will
    // await the same Promise instead of firing its own fetch.
    const accounts = msalInstance.getAllAccounts();
    if (accounts.length > 0) {
      prefetchedAuthMePromise = msalInstance.acquireTokenSilent({
        scopes: apiRequest.scopes,
        account: accounts[0],
      }).then(async (tokenResponse) => {
        const res = await fetch(api.auth.me(), {
          headers: { 'Authorization': `Bearer ${tokenResponse.accessToken}` },
          signal: AbortSignal.timeout(8000),
        });
        return res.ok ? (await res.json()) as Record<string, unknown> : null;
      }).catch(() => null);
    }
  })
  .catch((error) => {
    console.error('[Auth] MSAL initialization/redirect error:', error);
    // Mark as ready even on error so the app doesn't hang forever.
    // Auth operations will fail individually and trigger appropriate recovery.
    msalReady = true;
  });

/**
 * Module-level guard: only one acquireTokenRedirect may be in-flight at a time.
 * Multiple call-sites (ProtectedRoute, getAccessToken, fetchUserInfo) can all
 * independently decide that a redirect is needed.  Without this gate the second
 * call throws BrowserAuthError: interaction_in_progress which is unrecoverable
 * and causes the "Application error" crash on mobile.
 */
let redirectInFlight = false;

async function safeAcquireTokenRedirect(
  instance: PublicClientApplication,
  account: AccountInfo | undefined,
): Promise<void> {
  if (redirectInFlight) {
    console.log('[Auth] Redirect already in-flight, skipping duplicate');
    return;
  }
  redirectInFlight = true;
  try {
    await instance.acquireTokenRedirect({
      scopes: apiRequest.scopes,
      account,
    });
  } catch (err) {
    // interaction_in_progress means another redirect beat us — not an error.
    if (err instanceof BrowserAuthError && err.errorCode === 'interaction_in_progress') {
      console.log('[Auth] Redirect already in progress (BrowserAuthError), ignoring');
    } else {
      console.error('[Auth] acquireTokenRedirect failed:', err);
    }
  } finally {
    redirectInFlight = false;
  }
}

interface UserInfo {
  displayName: string;
  upn: string;
  tenantId: string;
  objectId: string;
  isGlobalAdmin: boolean;
  /**
   * Read-only platform tier: cross-tenant VISIBILITY like a Global Admin but no platform mutations.
   * ADDITIVE — a user may be both a GlobalReader and their own tenant's Admin (then isTenantAdmin is
   * also true and they keep edit rights on their own tenant). Use {@link AuthContextType.hasGlobalScope}
   * for visibility gating and isGlobalAdmin for platform-mutation gating.
   */
  isGlobalReader: boolean;
  isTenantAdmin: boolean;
  /**
   * Delegated ("MSP") admin: the caller manages a SUBSET of OTHER tenants (read-only this phase) — the
   * "scoped global" tier between a single-tenant member and a platform Global Admin. True iff
   * {@link delegatedTenantIds} is non-empty. Use {@link AuthContextType.hasFleetScope} for fleet/switcher gating.
   */
  isDelegated: boolean;
  /** The tenant IDs this caller manages as a delegated admin (lowercase). Empty for non-delegated users. */
  delegatedTenantIds: string[];
  role: 'Admin' | 'Operator' | 'Viewer' | null;
  canManageBootstrapTokens: boolean;
  hasMcpAccess: boolean;
  bootstrapTokenEnabled: boolean;
  unrestrictedModeEnabled: boolean;
}

interface AuthContextType {
  isAuthenticated: boolean;
  user: UserInfo | null;
  /** Platform-wide read scope: Global Admin OR read-only Global Reader. Use for cross-tenant VISIBILITY. */
  hasGlobalScope: boolean;
  /**
   * Fleet scope: the caller can see MORE than one tenant — full platform scope (GA/Reader) OR a delegated
   * ("MSP") subset. Use to gate fleet/switcher UI. Does NOT itself authorize a specific tenant (the backend
   * gates that); a delegated user is bounded to {@link UserInfo.delegatedTenantIds}.
   */
  hasFleetScope: boolean;
  isLoading: boolean;
  isPreviewBlocked: boolean;
  previewMessage: string;
  login: () => Promise<void>;
  logout: () => Promise<void>;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  refreshUserInfo: () => Promise<void>;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

/**
 * Internal Auth Provider that uses MSAL hooks
 * This component must be inside MsalProvider
 */
function AuthProviderInternal({ children }: { children: React.ReactNode }) {
  const { instance, accounts, inProgress } = useMsal();
  const isAuthenticated = useIsAuthenticated();
  const [user, setUser] = useState<UserInfo | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const isLoadingRef = useRef(true);
  const [isPreviewBlocked, setPreviewBlocked] = useState(false);
  const [previewMessage, setPreviewMessage] = useState('');

  // Handle SSR - if we're on the server, MSAL won't initialize
  // Set loading to false immediately on mount in browser
  useEffect(() => {
    if (typeof window === 'undefined') {
      setIsLoading(false);
    }
  }, []);

  /**
   * Fetches user info from backend API
   */
  const fetchUserInfo = useCallback(async (account: AccountInfo): Promise<UserInfo | null> => {
    try {
      // Await the prefetch Promise if one is in flight (or already resolved).
      // Consume exactly once so subsequent fetchUserInfo calls go through the
      // normal path. If the prefetch returned null (e.g. token failed), fall
      // through to a fresh fetch below rather than returning null.
      if (prefetchedAuthMePromise) {
        const pending = prefetchedAuthMePromise;
        prefetchedAuthMePromise = null;
        const data = await pending;
        if (data) return {
          displayName: (data.displayName as string) || account.name || '',
          upn: (data.upn as string) || account.username || '',
          tenantId: (data.tenantId as string) || account.tenantId || '',
          objectId: (data.objectId as string) || account.homeAccountId || '',
          isGlobalAdmin: (data.isGlobalAdmin as boolean) || false,
          isGlobalReader: (data.isGlobalReader as boolean) || false,
          isTenantAdmin: (data.isTenantAdmin as boolean) || false,
          isDelegated: (data.isDelegated as boolean) || false,
          delegatedTenantIds: (data.delegatedTenantIds as string[]) || [],
          role: (data.role as 'Admin' | 'Operator' | 'Viewer' | null) || null,
          canManageBootstrapTokens: (data.canManageBootstrapTokens as boolean) || false,
          hasMcpAccess: (data.hasMcpAccess as boolean) || false,
          bootstrapTokenEnabled: (data.bootstrapTokenEnabled as boolean) || false,
          unrestrictedModeEnabled: (data.unrestrictedModeEnabled as boolean) || false,
        };
      }

      // Get access token for API
      const tokenResponse = await instance.acquireTokenSilent({
        scopes: apiRequest.scopes,
        account: account,
      });

      // Call backend API to get user info including global admin status.
      // 8-second timeout so a cold Azure Function start does not block the
      // landing page spinner indefinitely — the catch block falls back to
      // token claims so the user can still log in.
      const authMeController = new AbortController();
      const authMeTimeout = setTimeout(() => authMeController.abort(), 8000);
      let response: Response;
      try {
        response = await fetch(api.auth.me(), {
          headers: {
            'Authorization': `Bearer ${tokenResponse.accessToken}`,
          },
          signal: authMeController.signal,
        });
      } finally {
        clearTimeout(authMeTimeout);
      }

      if (!response.ok) {
        if (response.status === 403) {
          const errorData = await response.json();
          if (errorData.error === 'TenantSuspended') {
            console.error('[Auth] Tenant suspended:', errorData.message);
            alert(`Access Denied\n\n${errorData.message}`);
            await instance.logoutRedirect({ account });
            return null;
          }
          if (errorData.error === 'PrivatePreview') {
            console.log('[Auth] Tenant not yet approved for preview');
            setPreviewBlocked(true);
            setPreviewMessage(errorData.message || 'Your organization is on the waitlist.');
            // Return basic user info so the user stays logged in but sees the preview page
            return {
              displayName: account.name || '',
              upn: account.username || '',
              tenantId: account.tenantId || '',
              objectId: account.homeAccountId || '',
              isGlobalAdmin: false,
              isGlobalReader: false,
              isTenantAdmin: false,
              isDelegated: false,
              delegatedTenantIds: [],
              role: null,
              canManageBootstrapTokens: false,
              hasMcpAccess: false,
              bootstrapTokenEnabled: false,
              unrestrictedModeEnabled: false,
            };
          }
        }
        throw new Error(`Failed to fetch user info: ${response.statusText}`);
      }

      const data = await response.json();

      return {
        displayName: data.displayName || account.name || '',
        upn: data.upn || account.username || '',
        tenantId: data.tenantId || account.tenantId || '',
        objectId: data.objectId || account.homeAccountId || '',
        isGlobalAdmin: data.isGlobalAdmin || false,
        isGlobalReader: data.isGlobalReader || false,
        isTenantAdmin: data.isTenantAdmin || false,
        isDelegated: data.isDelegated || false,
        delegatedTenantIds: data.delegatedTenantIds || [],
        role: data.role || null,
        canManageBootstrapTokens: data.canManageBootstrapTokens || false,
        hasMcpAccess: data.hasMcpAccess || false,
        bootstrapTokenEnabled: data.bootstrapTokenEnabled || false,
        unrestrictedModeEnabled: data.unrestrictedModeEnabled || false,
      };
    } catch (error) {
      // If the refresh token is expired or consent is required, redirect to
      // interactive login immediately instead of falling back to stale claims.
      if (error instanceof InteractionRequiredAuthError) {
        console.warn('[Auth] Interactive login required — redirecting:', error.errorCode);
        await safeAcquireTokenRedirect(instance as PublicClientApplication, account);
        return null;
      }

      // interaction_in_progress — another redirect is already handling this.
      if (error instanceof BrowserAuthError && error.errorCode === 'interaction_in_progress') {
        console.log('[Auth] Interaction already in progress during fetchUserInfo, waiting');
        return null;
      }

      console.error('[Auth] Failed to fetch user info:', error);

      // Fallback to token claims only for non-auth errors (network issues,
      // backend cold starts, etc.) so the user can still see the app.
      return {
        displayName: account.name || '',
        upn: account.username || '',
        tenantId: account.tenantId || '',
        objectId: account.homeAccountId || '',
        isGlobalAdmin: false,
        isGlobalReader: false,
        isTenantAdmin: false,
        isDelegated: false,
        delegatedTenantIds: [],
        role: null,
        canManageBootstrapTokens: false,
        hasMcpAccess: false,
        bootstrapTokenEnabled: false,
        unrestrictedModeEnabled: false,
      };
    }
  }, [instance]);

  /**
   * Refreshes user information from backend
   */
  const refreshUserInfo = useCallback(async () => {
    if (accounts.length > 0) {
      const userInfo = await fetchUserInfo(accounts[0]);
      setUser(userInfo);
    }
  }, [accounts, fetchUserInfo]);

  /**
   * Load user info when authentication state changes.
   * Waits for MSAL to be fully initialized before proceeding.
   */
  useEffect(() => {
    const loadUserInfo = async () => {
      // Wait for MSAL initialization to complete before evaluating auth state.
      // This prevents the 3-second timeout from firing prematurely while MSAL
      // is still processing the redirect promise.
      if (!msalReady) {
        await msalInitPromise;
      }

      if (inProgress === InteractionStatus.None) {
        if (accounts.length > 0) {
          const userInfo = await fetchUserInfo(accounts[0]);
          setUser(userInfo);
        } else {
          setUser(null);
        }
        isLoadingRef.current = false;
        setIsLoading(false);
      }
    };

    loadUserInfo();

    // Fallback: if MSAL doesn't settle within 5 seconds, set loading to false
    // anyway so the user isn't stuck on a spinner forever.
    const timeout = setTimeout(() => {
      if (isLoadingRef.current) {
        console.warn('[Auth] MSAL initialization timeout - setting isLoading to false');
        isLoadingRef.current = false;
        setIsLoading(false);
      }
    }, 5000);

    return () => clearTimeout(timeout);
  }, [accounts, inProgress, fetchUserInfo]);

  /**
   * Initiates login flow
   */
  const login = useCallback(async () => {
    // Check if an interaction is already in progress
    if (inProgress !== InteractionStatus.None) {
      console.log('[Auth] Interaction already in progress, skipping login');
      return;
    }

    try {
      await instance.loginRedirect(loginRequest);
    } catch (error: unknown) {
      // Ignore interaction_in_progress errors - this can happen if user clicks button multiple times
      // or if another part of the app already triggered a redirect.
      if (error instanceof Error && 'errorCode' in error && error.errorCode === 'interaction_in_progress') {
        console.log('[Auth] Interaction already in progress, ignoring duplicate login attempt');
        return;
      }
      console.error('[Auth] Login error:', error);
      throw error;
    }
  }, [instance, inProgress]);

  /**
   * Initiates logout flow
   */
  const logout = useCallback(async () => {
    try {
      await instance.logoutRedirect({
        account: accounts[0],
      });
    } catch (error) {
      console.error('[Auth] Logout error:', error);
      throw error;
    }
  }, [instance, accounts]);

  /**
   * Gets access token for API calls
   * Automatically handles token refresh
   */
  const getAccessToken = useCallback(async (forceRefresh?: boolean): Promise<string | null> => {
    if (accounts.length === 0) {
      return null;
    }

    try {
      const response = await instance.acquireTokenSilent({
        scopes: apiRequest.scopes,
        account: accounts[0],
        forceRefresh: forceRefresh ?? false,
      });

      return response.accessToken;
    } catch (error) {
      // interaction_in_progress — another redirect is already in flight.
      // Return null and let the redirect complete; the page will reload.
      if (error instanceof BrowserAuthError && error.errorCode === 'interaction_in_progress') {
        console.log('[Auth] Interaction already in progress during getAccessToken, returning null');
        return null;
      }

      console.error('[Auth] Token acquisition error:', error);

      // If silent token acquisition fails, trigger interactive redirect
      // via the guarded helper to avoid duplicate redirects.
      await safeAcquireTokenRedirect(instance as PublicClientApplication, accounts[0]);
      // Browser will redirect; this line is only reached if the redirect was skipped.
      return null;
    }
  }, [instance, accounts]);

  const value: AuthContextType = {
    isAuthenticated,
    user,
    hasGlobalScope: (user?.isGlobalAdmin || user?.isGlobalReader) ?? false,
    hasFleetScope: (user?.isGlobalAdmin || user?.isGlobalReader || user?.isDelegated) ?? false,
    isLoading,
    isPreviewBlocked,
    previewMessage,
    login,
    logout,
    getAccessToken,
    refreshUserInfo,
  };

  return (
    <AuthContext.Provider value={value}>
      {children}
    </AuthContext.Provider>
  );
}

/**
 * Main Auth Provider that wraps MsalProvider
 */
export function AuthProvider({ children }: { children: React.ReactNode }) {
  return (
    <MsalProvider instance={msalInstance}>
      <AuthProviderInternal>
        {children}
      </AuthProviderInternal>
    </MsalProvider>
  );
}

/**
 * Hook to use auth context
 */
export function useAuth() {
  const context = useContext(AuthContext);
  if (context === undefined) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return context;
}
