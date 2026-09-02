"use client";

import React, { createContext, useContext, useEffect, useMemo, useState, useCallback, useRef } from 'react';
import { PublicClientApplication, AccountInfo, InteractionStatus, InteractionRequiredAuthError, BrowserAuthError } from '@azure/msal-browser';
import { MsalProvider, useMsal, useIsAuthenticated } from '@azure/msal-react';
import { msalConfig, loginRequest, apiRequest, activeAuthApp, buildMsalConfig, clientIdForApp } from '@/lib/msalConfig';
import {
  classifyEntraAuthError,
  clearLoginAttemptApp,
  clearLoginFallback,
  getSelectedAuthApp,
  legacyConfigured,
  loginFallbackActive,
  markLoginDeclined,
  otherApp,
  requestRehome,
  setLoginAttemptApp,
  setSelectedAuthApp,
  shouldRehome,
  tryBeginLoginFallback,
} from '@/lib/authApp';
import { api } from '@/lib/api';
import { trackEvent } from '@/lib/appInsights';
import type { AuthMeResponse } from '@/types/auth';

// Initialize MSAL instance for the ACTIVE app registration (dual app-reg window: the
// selection was decided at module boot in lib/msalConfig.ts; app switches always reload).
const msalInstance = new PublicClientApplication(msalConfig);

// Track MSAL initialization state so components can wait for it.
let msalReady = false;

// Prefetch: store the in-flight auth/me Promise (not the resolved value) so
// fetchUserInfo can await it instead of racing a duplicate fetch when the
// prefetch hasn't settled yet. Resolves to the JSON body, or null if the
// fetch failed / token acquisition failed.
// Runs as a fire-and-forget side-effect — MUST NOT block msalInitPromise,
// otherwise a cold backend would keep the UI on a white screen.
let prefetchedAuthMePromise: Promise<AuthMeResponse | null> | null = null;

// Post-login re-homing inputs (see lib/authApp.ts shouldRehome): set by the redirect handler
// below for the page load that completed a redirect sign-in, consumed by the first auth/me.
let redirectLoginCompleted = false;
let redirectLoginViaFallback = false;

/**
 * Safety net of the dual app-reg model, for the rare browser that lands on an app its tenant
 * never consented to (no stored selection, wrong default): Entra reports "needs admin
 * approval" (AADSTS90094/65001) on the redirect return, and we retry ONCE with the other app
 * — silently, because the user just authenticated so an Entra session exists. An explicit
 * consent DECLINE (AADSTS65004) never falls back: for a brand-new tenant that would
 * mis-consent the legacy app; ProtectedRoute shows the failed screen instead.
 * Returns true when a fallback redirect was started (page is navigating away).
 */
async function tryHandleRedirectAuthError(error: unknown): Promise<boolean> {
  const err = error as { errorMessage?: string; message?: string } | null;
  const classification = classifyEntraAuthError(String(err?.errorMessage ?? err?.message ?? ''));
  clearLoginAttemptApp();
  if (classification === 'declined') {
    // Trace point: the user actively declined consent — for a funneled signup this means the
    // new app was rejected, which support needs to see (events are buffered until AI init).
    trackEvent('auth_app_login_declined', { app: activeAuthApp });
    markLoginDeclined();
    return false;
  }
  if (classification !== 'admin-approval-required' || !legacyConfigured() || !tryBeginLoginFallback()) {
    return false;
  }
  const target = otherApp(activeAuthApp);
  console.warn(`[Auth] App registration not consented in this tenant — retrying via the ${target} app`);
  // Best-effort (the fallback redirect navigates away): the durable signal is
  // auth_app_fallback_succeeded on the return leg, keyed off the surviving session marker.
  trackEvent('auth_app_fallback_started', { from: activeAuthApp, to: target });
  try {
    setLoginAttemptApp(target);
    const fallbackInstance = new PublicClientApplication(buildMsalConfig(clientIdForApp(target)));
    await fallbackInstance.initialize();
    // No prompt: the user just entered credentials, so the Entra session completes silently.
    await fallbackInstance.loginRedirect({ ...loginRequest, prompt: undefined });
    return true;
  } catch (fallbackError) {
    console.error('[Auth] Cross-app fallback login failed:', fallbackError);
    clearLoginAttemptApp();
    return false;
  }
}

const msalInitPromise = msalInstance
  .initialize()
  .then(() => msalInstance.handleRedirectPromise())
  .then((redirectResult) => {
    if (redirectResult?.account) {
      // Successful redirect sign-in on the active app: remember it as this browser's app
      // (the "learn" half of the model — future boots go straight to the right one).
      // A still-set fallback marker means THIS login only worked via the one-shot cross-app
      // retry — the strategic signal that one of the two apps is not consented in the tenant.
      redirectLoginCompleted = true;
      redirectLoginViaFallback = loginFallbackActive();
      if (redirectLoginViaFallback) {
        trackEvent('auth_app_fallback_succeeded', { app: activeAuthApp });
      }
      setSelectedAuthApp(activeAuthApp);
      clearLoginFallback();
    }
    // Redirect settled (or none was in flight) — the attempt marker has served its purpose.
    clearLoginAttemptApp();
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
        return res.ok ? (await res.json()) as AuthMeResponse : null;
      }).catch(() => null);
    }
  })
  .catch(async (error) => {
    console.error('[Auth] MSAL initialization/redirect error:', error);
    await tryHandleRedirectAuthError(error);
    // Mark as ready even on error so the app doesn't hang forever (if the fallback
    // started a redirect the page is navigating away anyway).
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
    // Pin the redirect return to the app that starts it (see lib/authApp.ts).
    setLoginAttemptApp(activeAuthApp);
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

/**
 * Client-side auth state, derived from the generated auth/me wire type — a backend key
 * rename goes tsc-red here instead of silently degrading to fallbacks.
 * Deviations from the wire: `role` is an explicit null (the wire omits the key for a
 * roleless caller), and `homedApp` is not carried (it is learned into localStorage via
 * {@link learnHomedAppFromAuthMe}, never React state).
 *
 * Semantics reminders:
 * - isGlobalReader: read-only platform tier — cross-tenant VISIBILITY like a Global Admin
 *   but no platform mutations. ADDITIVE (a GlobalReader can also be their own tenant's
 *   Admin). Use {@link AuthContextType.hasGlobalScope} for visibility gating and
 *   isGlobalAdmin for platform-mutation gating.
 * - isDelegated / delegatedTenantIds: delegated ("MSP") admin managing a SUBSET of other
 *   tenants — use {@link AuthContextType.hasFleetScope} for fleet/switcher gating.
 */
interface UserInfo extends Omit<AuthMeResponse, 'role' | 'homedApp'> {
  role: 'Admin' | 'Operator' | 'Viewer' | null;
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
  isActivationPending: boolean;
  activationMessage: string;
  login: (options?: { auto?: boolean }) => Promise<void>;
  logout: () => Promise<void>;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  refreshUserInfo: () => Promise<void>;
}

/**
 * Background learning of the dual app-reg model: auth/me returns which app registration the
 * tenant is homed on; storing it makes the NEXT login on this browser use that app directly.
 * No reload here — the current session keeps its valid token (both audiences are accepted).
 * This is also how an operator flip propagates: flip → next login of every user runs via the
 * new app, at which point the tenant's admin consent already exists.
 *
 * Exception: the page load that just completed a redirect sign-in on the OTHER app (fresh
 * browser, wrong default). That browser is re-homed immediately — AuthGate, the post-login
 * hop, switches it to the homed app and the sign-in completes silently — so the first session
 * never runs on an app the tenant did not set up (Entra app roles, Conditional Access scope).
 */
function learnHomedAppFromAuthMe(data: AuthMeResponse): void {
  const homedApp = data.homedApp;
  if (homedApp === 'legacy' || homedApp === 'primary') {
    // Change-only trace: this browser just learned its tenant was flipped — the propagation
    // half of the homing migration ("next login uses the new app") made observable per user.
    if (legacyConfigured() && getSelectedAuthApp() !== homedApp) {
      trackEvent('auth_app_homing_learned', { homedApp });
    }
    setSelectedAuthApp(homedApp);
    if (shouldRehome({
      homedApp,
      activeApp: activeAuthApp,
      redirectLoginCompleted,
      viaFallback: redirectLoginViaFallback,
    })) {
      requestRehome(homedApp);
    }
    // The redirect-login page load is spent either way: a later refreshUserInfo must not
    // re-request a re-home.
    redirectLoginCompleted = false;
  }
}

/**
 * Maps a successful auth/me body onto client auth state. Token-claim fallbacks cover the
 * cold-start edge where a field arrives empty; the shape itself is compiler-checked
 * against the generated wire type.
 */
function toUserInfo(data: AuthMeResponse, account: AccountInfo): UserInfo {
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
    role: data.role ?? null,
    canManageBootstrapTokens: data.canManageBootstrapTokens || false,
    hasMcpAccess: data.hasMcpAccess || false,
    bootstrapTokenEnabled: data.bootstrapTokenEnabled || false,
    unrestrictedModeEnabled: data.unrestrictedModeEnabled || false,
  };
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
  const [isActivationPending, setActivationPending] = useState(false);
  const [activationMessage, setActivationMessage] = useState('');

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
        if (data) learnHomedAppFromAuthMe(data);
        if (data) return toUserInfo(data, account);
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
          // 'PendingActivation' is the current backend code; 'PrivatePreview' is the legacy
          // code kept accepted so web and backend can deploy in any order.
          if (errorData.error === 'PendingActivation' || errorData.error === 'PrivatePreview') {
            console.log('[Auth] Tenant not yet activated');
            setActivationPending(true);
            setActivationMessage(errorData.message || 'Your organization is being activated.');
            // Return basic user info so the user stays logged in but sees the activation page
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

      const data = (await response.json()) as AuthMeResponse;

      // A successful auth/me means the tenant is (now) activated — clear any pending
      // state so the activation page's poll can redirect into the portal.
      setActivationPending(false);
      setActivationMessage('');

      learnHomedAppFromAuthMe(data);

      return toUserInfo(data, account);
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
   * Initiates login flow.
   *
   * `auto: true` marks a login the APP triggered (ProtectedRoute re-auth,
   * cross-origin www → portal handover) rather than a user click: the
   * `prompt: "select_account"` picker is dropped so Entra completes silently
   * via its session cookie when one exists — otherwise the www → portal
   * handover shows a second interactive sign-in for an already signed-in user.
   */
  const login = useCallback(async (options?: { auto?: boolean }) => {
    // Check if an interaction is already in progress
    if (inProgress !== InteractionStatus.None) {
      console.log('[Auth] Interaction already in progress, skipping login');
      return;
    }

    try {
      const request = options?.auto
        ? { ...loginRequest, prompt: undefined }
        : loginRequest;
      // Pin the redirect return to the app that starts it (see lib/authApp.ts).
      setLoginAttemptApp(activeAuthApp);
      await instance.loginRedirect(request);
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

  // Memoized (P6.3): AuthProvider sits above every page — an unmemoized value object
  // re-rendered every context consumer on each provider render (MSAL account refreshes
  // included), which cascaded into the settings/dashboard trees.
  const value: AuthContextType = useMemo(() => ({
    isAuthenticated,
    user,
    hasGlobalScope: (user?.isGlobalAdmin || user?.isGlobalReader) ?? false,
    hasFleetScope: (user?.isGlobalAdmin || user?.isGlobalReader || user?.isDelegated) ?? false,
    isLoading,
    isActivationPending,
    activationMessage,
    login,
    logout,
    getAccessToken,
    refreshUserInfo,
  }), [isAuthenticated, user, isLoading, isActivationPending, activationMessage, login, logout, getAccessToken, refreshUserInfo]);

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
