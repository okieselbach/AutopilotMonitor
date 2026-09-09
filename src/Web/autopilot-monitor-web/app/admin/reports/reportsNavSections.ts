export const REPORTS_NAV_SECTIONS = [
  { id: "session-reports", label: "Session Reports", description: "Sessions reported by Tenant Admins for analysis" },
  { id: "rule-submissions", label: "Rule Submissions", description: "Custom rules submitted by Tenant Admins for the community pool" },
  { id: "distress-reports", label: "Distress Reports", description: "Pre-auth agent distress signals (cert/whitelist/registration failures)" },
  { id: "user-feedback", label: "User Feedback", description: "Product feedback from users" },
  { id: "session-export", label: "Session Export", description: "Export session event data" },
] as const;

export type ReportsSectionId = (typeof REPORTS_NAV_SECTIONS)[number]["id"];
