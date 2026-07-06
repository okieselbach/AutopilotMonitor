import { Configuration, LogLevel, RedirectRequest } from "@azure/msal-browser";

/**
 * MSAL Configuration for Multi-Tenant Azure AD Authentication
 *
 * Environment Variables:
 * - NEXT_PUBLIC_ENTRA_CLIENT_ID: Application (client) ID from App Registration
 * - NEXT_PUBLIC_ENTRA_REDIRECT_URI: Fallback redirect URI for SSR/dev (browser uses window.location.origin)
 * - NEXT_PUBLIC_ENTRA_POST_LOGOUT_REDIRECT_URI: Fallback post-logout URI for SSR/dev
 */

// Resolve the redirect URI from the current browser origin so multiple custom
// domains (e.g. www.* and portal.*) keep the user on whichever host they started
// on. Each origin must still be registered as a redirect URI in the App Registration.
const resolveRedirectUri = (fallback: string | undefined): string => {
  if (typeof window !== "undefined" && window.location?.origin) {
    return window.location.origin;
  }
  return fallback || "http://localhost:3000";
};

// MSAL Configuration
export const msalConfig: Configuration = {
  auth: {
    clientId: process.env.NEXT_PUBLIC_ENTRA_CLIENT_ID || "YOUR_CLIENT_ID_HERE",
    authority: "https://login.microsoftonline.com/organizations", // Multi-tenant
    redirectUri: resolveRedirectUri(process.env.NEXT_PUBLIC_ENTRA_REDIRECT_URI),
    postLogoutRedirectUri: resolveRedirectUri(process.env.NEXT_PUBLIC_ENTRA_POST_LOGOUT_REDIRECT_URI),
    navigateToLoginRequestUrl: false,
  },
  cache: {
    cacheLocation: "sessionStorage", // Using sessionStorage for better XSS protection
    storeAuthStateInCookie: true, // Required for Safari ITP compatibility during auth redirects
  },
  system: {
    loggerOptions: {
      loggerCallback: (level, message, containsPii) => {
        if (containsPii) {
          return;
        }
        switch (level) {
          case LogLevel.Error:
            console.error(`[MSAL] ${message}`);
            return;
          case LogLevel.Info:
            console.info(`[MSAL] ${message}`);
            return;
          case LogLevel.Verbose:
            console.debug(`[MSAL] ${message}`);
            return;
          case LogLevel.Warning:
            console.warn(`[MSAL] ${message}`);
            return;
          default:
            return;
        }
      },
      logLevel: LogLevel.Info,
      piiLoggingEnabled: false,
    },
    allowNativeBroker: false, // Disables WAM Broker
  },
};

/**
 * Scopes you add here will be prompted for user consent during sign-in.
 * By default, MSAL.js will add OIDC scopes (openid, profile, email) to any login request.
 * For more information about OIDC scopes, visit:
 * https://docs.microsoft.com/en-us/azure/active-directory/develop/v2-permissions-and-consent#openid-connect-scopes
 */
export const loginRequest: RedirectRequest = {
  scopes: [
    "User.Read", // Microsoft Graph - read user profile
  ],
  prompt: "select_account", // Force account selection on login
};

/**
 * Scopes for accessing the backend API
 * IMPORTANT: Backend API must be exposed in Azure AD App Registration with this scope
 * Format: api://<backend-client-id>/access_as_user
 *
 * NEXT_PUBLIC_ENTRA_API_CLIENT_ID lets local dev sign in with the dev app
 * registration while requesting a token whose audience is the prod API app
 * (the deployed backend only accepts its own audience). Unset in production,
 * where one app registration serves as both SPA client and API.
 */
const apiAppId =
  process.env.NEXT_PUBLIC_ENTRA_API_CLIENT_ID || process.env.NEXT_PUBLIC_ENTRA_CLIENT_ID;

export const apiRequest = {
  scopes: [`api://${apiAppId}/access_as_user`],
};

/**
 * Protected resource map for token acquisition
 * Maps API endpoints to their required scopes
 */
export const protectedResources = {
  api: {
    endpoint: process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:7071",
    scopes: apiRequest.scopes,
  },
};
