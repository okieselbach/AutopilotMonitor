/**
 * Centralized application configuration
 * All configuration values are defined here for easy maintenance
 */

/**
 * API Base URL
 *
 * Sources (in priority order):
 * 1. NEXT_PUBLIC_API_BASE_URL environment variable
 * 2. Default: http://localhost:7071 (local development)
 *
 * Production: Set NEXT_PUBLIC_API_BASE_URL in your environment
 * Example: https://autopilotmonitor-api-eu.azurewebsites.net
 */
export const API_BASE_URL = process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:7071";

/**
 * Well-known base URLs — single registry (counterpart of Constants.cs on the
 * C# side). Every own or Microsoft host used in code MUST reference these
 * constants instead of repeating the literal; hardcodedUrls.guard.test.ts
 * enforces this. The EU cutover missed hardcoded copies of the blob host
 * precisely because they did not go through a registry.
 */

/** Published customer documentation. */
export const DOCS_URL = "https://docs.autopilotmonitor.com";

/** Public marketing/product website (also feeds metadataBase/sitemap/robots). */
export const SITE_URL = "https://www.autopilotmonitor.com";

/** Customer portal (deep links rendered into generated bootstrap scripts). */
export const PORTAL_URL = "https://portal.autopilotmonitor.com";

/** Entra ID login/token authority host (no trailing slash). */
export const ENTRA_LOGIN_URL = "https://login.microsoftonline.com";

/** Production backend origin (CSP connect-src + server-side env fallback). */
export const API_URL_PROD = "https://autopilotmonitor-api-eu.azurewebsites.net";

/** Production blob origin (CSP connect-src: diagnostics SAS uploads). */
export const BLOB_URL_PROD = "https://autopilotmonitoreu.blob.core.windows.net";

/**
 * Hostnames the portal accepts in a bootstrap response's agentDownloadUrl.
 * Keep in sync with ValidateBootstrapCodeFunction.cs, which builds that URL
 * from Constants.AgentDownloadBaseUrl. The legacy blob host stays allowlisted
 * until the customer migration to the download alias is complete.
 */
export const AGENT_DOWNLOAD_HOSTNAMES = [
  "download.autopilotmonitor.com",
  "autopilotmonitor.blob.core.windows.net",
] as const;

/**
 * Cache durations (in milliseconds)
 */
export const CACHE_DURATION = {
  /** Usage metrics cache: 5 minutes */
  USAGE_METRICS: 5 * 60 * 1000,
  /** Session data refresh: 10 seconds */
  SESSION_REFRESH: 10 * 1000,
};

/**
 * SignalR Configuration
 */
export const SIGNALR_CONFIG = {
  /** Hub name for autopilot monitoring */
  HUB_NAME: "autopilotmonitor",
  /** Reconnect delay in milliseconds */
  RECONNECT_DELAY: 5000,
};

/**
 * UI Configuration
 */
export const UI_CONFIG = {
  /** Number of sessions per page in pagination */
  SESSIONS_PER_PAGE: 10,
  /** Auto-hide duration for success messages (in milliseconds) */
  SUCCESS_MESSAGE_DURATION: 3000,
  /** Maintenance success message duration (in milliseconds) */
  MAINTENANCE_SUCCESS_DURATION: 5000,
};
