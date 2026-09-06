// Ratchet baseline for lib/__tests__/apiClient.guard.test.ts — per-file counts of the legacy
// call-site patterns on 2026-09-06. Only ever shrinks: a migrated file lowers or drops its entry,
// a new raw site anywhere fails the guard. Regenerate a shrunken map by hand, never by script over
// the whole tree (the guard's stale check tells you which entries to lower).

/** Files that keep a raw site for a stated reason; they never leave the baseline. */
export const PERMANENT: Record<string, string> = {
  "contexts/AuthContext.tsx": "auth/me bootstrap runs before the auth context exists: raw fetch by design",
  "components/landing/StatsBand.tsx": "unauthenticated public blob manifest, not the API",
  "app/health-check/page.tsx": "/version.json of the SWA itself, not the API",
  "app/dashboard/hooks/deleteSessionResponse.ts": "safeJson on the 202 Response of the delete-cascade classifier (refusals arrive as ApiError)",
  "app/settings/TenantConfigContext.tsx": "consent return path routes on TokenExpiredError (router.replace); the access check rethrows it as a non-outcome",
  "app/admin/ops/session-cleanup/components/MaintenanceStatusBanner.tsx": "auxiliary banner: only a token expiry is surfaced, backend refusals stay silent",
  "app/dashboard/hooks/useBlockDevice.ts": "bulk runner rethrows the expiry so it is toasted once, not per device",
  "app/dashboard/hooks/useDeleteSession.ts": "bulk runner collects the expiry and toasts once after the batch",
  "app/dashboard/hooks/useTenantSecurityConfig.ts": "fail-soft banner data; the expiry is the one failure the user must hear about",
  "app/sessions/hooks/useSessionDetail.ts": "a network failure keeps the retry flag, an expiry must not",
  "app/settings/components/OffboardingSection.tsx": "expiry message tells the user the offboarding continues in the background",
};

export const AUTHENTICATEDFETCH_BASELINE: Record<string, number> = {
};

export const DEDUPEDAUTHFETCH_BASELINE: Record<string, number> = {
};

export const JSONPARSE_BASELINE: Record<string, number> = {
  "app/dashboard/hooks/deleteSessionResponse.ts": 1,
  "app/health-check/page.tsx": 1,
  "components/landing/StatsBand.tsx": 2,
  "contexts/AuthContext.tsx": 3,
};

export const TOKENEXPIRED_BASELINE: Record<string, number> = {
  "app/admin/ops/session-cleanup/components/MaintenanceStatusBanner.tsx": 1,
  "app/dashboard/hooks/useBlockDevice.ts": 1,
  "app/dashboard/hooks/useDeleteSession.ts": 1,
  "app/dashboard/hooks/useTenantSecurityConfig.ts": 1,
  "app/sessions/hooks/useSessionDetail.ts": 1,
  "app/settings/TenantConfigContext.tsx": 2,
  "app/settings/components/OffboardingSection.tsx": 1,
};
