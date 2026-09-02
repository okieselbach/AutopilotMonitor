/**
 * Documentation page for each portal section that links to the customer docs, as a path below DOCS_URL.
 *
 * GitBook publishes the first path segment as the slugified SUMMARY.md group heading, not the
 * folder name: `troubleshooting/` → `/troubleshooting-and-support/`, `trust/` → `/trust-and-security/`.
 * Anchors are GitBook heading slugs (single hyphens). `docsPaths.test.ts` guards the shape.
 */
export const DOCS_PATHS = {
  // Settings → Tenant
  enrollmentDeviceValidation: "/reference/settings#enrollment-device-validation",
  hardwareWhitelist: "/reference/settings#hardware-whitelist",
  notifications: "/integrations/notifications",
  accessManagement: "/concepts/roles-and-permissions",
  slaTargets: "/portal-guide/sla-compliance#configuring-targets",
  plan: "/plans",
  contact: "/reference/settings#contact",
  bootstrapSessions: "/reference/bootstrap-script-and-tokens",
  mcpUsers: "/integrations/ai-integration-mcp",
  // Settings → Agent
  agentParameters: "/reference/settings#agent-parameters",
  agentCollectors: "/reference/settings#agent-collectors",
  agentAnalyzers: "/reference/settings#agent-analyzers",
  diagnosticsPackage: "/troubleshooting-and-support/diagnostics-and-log-collection",
  unrestrictedMode: "/reference/settings#unrestricted-mode-pro-plan-on-request",
  // Settings → Maintenance
  dataManagement: "/reference/settings#data-management",
  dangerZone: "/reference/settings#danger-zone",
  // Settings → Reporting
  mcpUsage: "/integrations/ai-integration-mcp#rate-limits-and-usage-plans",
  // Rules
  analyzeRules: "/rules/analyze-rules",
  gatherRules: "/rules/gather-rules",
  // Dual app-registration window (sign-in banners, failed-sign-in screen)
  appRegistrationMigration: "/troubleshooting-and-support/app-registration-migration",
  appRegistrationMigrationAfter: "/troubleshooting-and-support/app-registration-migration#after-the-migration",
} as const;

/** Top-level URL segments GitBook publishes for docs.autopilotmonitor.com (SUMMARY.md groups + root pages). */
export const DOCS_TOP_LEVEL_SEGMENTS = [
  "getting-started",
  "concepts",
  "portal-guide",
  "rules",
  "integrations",
  "reference",
  "trust-and-security",
  "troubleshooting-and-support",
  "changelog",
  "plans",
] as const;
