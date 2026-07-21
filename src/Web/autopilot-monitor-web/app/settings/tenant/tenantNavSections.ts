export const TENANT_NAV_SECTIONS = [
  { id: "access-management", label: "Access Management", description: "Manage tenant admins and operators" },
  { id: "autopilot", label: "Autopilot Validation", description: "Autopilot device and corporate identifier validation" },
  { id: "hardware-whitelist", label: "Hardware Whitelist", description: "Manufacturer and model whitelist configuration" },
  { id: "notifications", label: "Notifications", description: "Webhook notification configuration" },
  { id: "sla-targets", label: "SLA Targets", description: "SLA targets and breach notification settings" },
  { id: "bootstrap-sessions", label: "Bootstrap Sessions", description: "Create and manage bootstrap tokens" },
  { id: "graph-permissions", label: "Optional Graph capabilities", description: "Grant additional Microsoft Graph permissions for optional features" },
  { id: "support", label: "Submit Logs", description: "Send diagnostic files to the Autopilot Monitor team" },
  { id: "plan", label: "Plan", description: "Your current plan and what Enterprise adds" },
  { id: "contact", label: "Contact", description: "Where we reach you about the service" },
] as const;

export type TenantSectionId = (typeof TENANT_NAV_SECTIONS)[number]["id"];
