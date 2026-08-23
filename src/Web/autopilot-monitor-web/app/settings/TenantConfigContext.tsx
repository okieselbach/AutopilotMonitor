"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type SetStateAction } from "react";
import { useRouter } from "next/navigation";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { useNotifications } from "../../contexts/NotificationContext";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { trackEvent } from "@/lib/appInsights";
import { classifyAccessCheck, type AccessCheckOutcome, type AccessCheckPayload } from "@/lib/accessCheck";
import { primaryClientId } from "@/lib/authApp";

type ValidationTrigger = "autopilot" | "corporate" | "device-preparation";

// Human label for a validation trigger.
const validationLabel = (t: ValidationTrigger) =>
  t === "corporate" ? "Corporate Identifier Validation"
    : t === "device-preparation" ? "DevPrep Device Association Validation"
      : "Autopilot Device Validation";

// Enable-confirmation suffix. DevPrep is shadow-mode (no hard gate), so its wording differs
// from the agent-gating validations.
const validationEnabledSuffix = (t: ValidationTrigger) =>
  t === "device-preparation"
    ? " enabled (shadow mode — does not block enrollment)."
    : " enabled. Backend agent endpoints are now unlocked for this tenant.";
import { parseSasExpiry } from "./components/DiagnosticsSection";
import { COMMUNITY_DEFAULT, parseEditionInfo, type EditionInfo } from "@/lib/edition";
import { TenantConfiguration, TenantAdmin, DiagnosticsLogPath, NotificationChannel, LEGACY_CHANNEL_ID } from "./types";
import { SECTION_FIELD_MAP, type SectionFieldSpec, type SettingsSectionName } from "./sectionFieldMap";
import { type BootstrapSessionItem } from "./components/BootstrapSessionsSection";

/**
 * Channels for display/editing from a loaded config: prefers notificationChannelsJson; while
 * unset, synthesizes ONE channel from the legacy single-webhook fields under the stable
 * "legacy" id (mirrors backend TenantConfiguration.GetNotificationChannels so the UI shows
 * exactly what the backend dispatches). First save materializes the synthesized channel.
 */
function channelsFromConfig(data: TenantConfiguration): NotificationChannel[] {
  if (data.notificationChannelsJson) {
    try {
      const parsed = JSON.parse(data.notificationChannelsJson);
      if (Array.isArray(parsed)) return parsed as NotificationChannel[];
    } catch {
      // Malformed (hand-edited) JSON — fall through to legacy synthesis.
    }
  }
  if (data.webhookUrl && data.webhookProviderType) {
    return [{
      id: LEGACY_CHANNEL_ID,
      name: "Default",
      providerType: data.webhookProviderType,
      url: data.webhookUrl,
      customHeadersJson: data.webhookCustomHeadersJson || undefined,
      enabled: true,
      notifyOnStart: data.webhookNotifyOnStart ?? false,
      notifyOnSuccess: data.webhookNotifyOnSuccess ?? true,
      notifyOnFailure: data.webhookNotifyOnFailure ?? true,
      notifyOnHardwareRejection: data.webhookNotifyOnHardwareRejection ?? false,
      notifyOnSlaEvents: true, // legacy behavior: SLA alerts always went to the single webhook
    }];
  }
  if (data.teamsWebhookUrl) {
    return [{
      id: LEGACY_CHANNEL_ID,
      name: "Default",
      providerType: 1, // TeamsLegacyConnector
      url: data.teamsWebhookUrl,
      enabled: true,
      notifyOnStart: data.teamsNotifyOnStart ?? false,
      notifyOnSuccess: data.teamsNotifyOnSuccess ?? true,
      notifyOnFailure: data.teamsNotifyOnFailure ?? true,
      notifyOnHardwareRejection: data.webhookNotifyOnHardwareRejection ?? false,
      notifyOnSlaEvents: true,
    }];
  }
  return [];
}

// ---------------------------------------------------------------------------
// Context value interface
// ---------------------------------------------------------------------------
/**
 * State surfaced by handleOffboard once the DELETE returns 202. Drives the post-confirm
 * banner in OffboardingSection. EarliestProcessingAt is rendered as a live countdown;
 * when it elapses, handleDrainBarrierElapsed() runs and logs the user out.
 */
export interface OffboardingInProgressInfo {
  status: string;
  historyRowKey: string;
  earliestProcessingAt?: string | null;
  message: string;
}

interface TenantConfigContextValue {
  // Core
  config: TenantConfiguration | null;
  loading: boolean;
  /**
   * True when the current user may CHANGE tenant configuration (Tenant Admin or Global Admin).
   * Operators reach the settings area read-only: sections must render no Save/Reset bar and
   * disable their inputs when this is false. Mirrors the backend write gate (PUT config is
   * TenantAdminOrGA), so hiding the affordances is UX — the server enforces regardless.
   */
  canEditConfig: boolean;
  savingSection: string | null;
  error: string | null;
  setError: (e: string | null) => void;
  successMessage: string | null;
  setSuccessMessage: (m: string | null) => void;

  // Edition / trial (read-time server resolution via feature-flags; fail-closed Community)
  editionInfo: EditionInfo;
  startingTrial: boolean;
  /** Self-service 30-day Pro trial (once per tenant). Returns success. */
  startTrial: () => Promise<boolean>;

  // Dual app-reg self-service migration (from feature-flags / consent-flow responses)
  /** True when the consent flow will grant the NEW app registration and auto-switch this tenant. */
  appHomingFunnelActive: boolean;
  /** True when this session's consent/verify flow just switched the tenant to the new app. */
  homingFlipped: boolean;

  // Validation
  validateAutopilotDevice: boolean;
  setValidateAutopilotDevice: (v: boolean) => void;
  validateCorporateIdentifier: boolean;
  setValidateCorporateIdentifier: (v: boolean) => void;
  validateDeviceAssociation: boolean;
  setValidateDeviceAssociation: (v: boolean) => void;
  /** Toggle + persist DevPrep Device Association validation in one shot (no consent flow needed). */
  handleToggleDeviceAssociationValidation: (newValue: boolean) => Promise<void>;
  validateCloudPcDevice: boolean;
  setValidateCloudPcDevice: (v: boolean) => void;
  /**
   * Toggle + persist W365 Cloud PC validation in one shot. No consent flow — the backing
   * CloudPC.Read.All permission is granted via the Optional Graph capabilities add-on script.
   */
  handleToggleCloudPcValidation: (newValue: boolean) => Promise<void>;
  /**
   * Persist a validation-gate change immediately. The validation section has NO save bar —
   * every gate change that doesn't run the consent flow (disable, and enabling the second
   * gate while the first already carries the consent) must persist through this, or it stays
   * local component state and silently reverts on the next config load (prod report
   * 2026-08-01: corporate-identifier "came back on" after every disable — the off toggle
   * had never reached the server).
   */
  saveValidationGate: (changes: { validateAutopilotDevice?: boolean; validateCorporateIdentifier?: boolean }) => Promise<boolean>;
  autopilotConsentInProgress: boolean;
  beginDeviceValidationConsentFlow: (trigger: "autopilot" | "corporate" | "device-preparation") => Promise<void>;
  /**
   * Probe whether the multi-tenant app is already pre-approved in this tenant (by someone with
   * consent rights) and, if so, enable validation without running the /adminconsent redirect —
   * the "rights-less admin" escape hatch.
   */
  detectExistingAccess: (trigger: "autopilot" | "corporate" | "device-preparation") => Promise<void>;

  // Hardware whitelist
  manufacturerWhitelist: string;
  setManufacturerWhitelist: (v: string) => void;
  modelWhitelist: string;
  setModelWhitelist: (v: string) => void;
  webhookNotifyOnHardwareRejection: boolean;
  setWebhookNotifyOnHardwareRejection: (v: boolean) => void;
  handleSaveHardwareWhitelist: () => void;
  handleResetHardwareWhitelist: () => void;

  // Agent settings
  enablePerformanceCollector: boolean;
  setEnablePerformanceCollector: (v: boolean) => void;
  performanceCollectorInterval: number;
  setPerformanceCollectorInterval: (v: number) => void;
  helloWaitTimeoutSeconds: number;
  setHelloWaitTimeoutSeconds: (v: number) => void;
  selfDestructOnComplete: boolean;
  setSelfDestructOnComplete: (v: boolean) => void;
  keepLogFile: boolean;
  setKeepLogFile: (v: boolean) => void;
  rebootOnComplete: boolean;
  setRebootOnComplete: (v: boolean) => void;
  rebootDelaySeconds: number;
  setRebootDelaySeconds: (v: number) => void;
  contactEmail: string;
  setContactEmail: (v: string) => void;
  handleSaveContact: () => void;
  handleResetContact: () => void;
  enableGeoLocation: boolean;
  setEnableGeoLocation: (v: boolean) => void;
  enableTimezoneAutoSet: boolean;
  setEnableTimezoneAutoSet: (v: boolean) => void;
  enableDoGroupIdAutoSet: boolean;
  setEnableDoGroupIdAutoSet: (v: boolean) => void;
  keepAwakeDuringUserEsp: boolean;
  setKeepAwakeDuringUserEsp: (v: boolean) => void;
  enableImeMatchLog: boolean;
  setEnableImeMatchLog: (v: boolean) => void;
  enableGatherRuleDebugLog: boolean;
  setEnableGatherRuleDebugLog: (v: boolean) => void;
  logLevel: string;
  setLogLevel: (v: string) => void;
  showScriptOutput: boolean;
  setShowScriptOutput: (v: boolean) => void;
  showEnrollmentSummary: boolean;
  setShowEnrollmentSummary: (v: boolean) => void;
  enrollmentSummaryTimeoutSeconds: number;
  setEnrollmentSummaryTimeoutSeconds: (v: number) => void;
  enrollmentSummaryBrandingImageUrl: string;
  setEnrollmentSummaryBrandingImageUrl: (v: string) => void;
  enrollmentSummaryLaunchRetrySeconds: number;
  setEnrollmentSummaryLaunchRetrySeconds: (v: number) => void;
  enableRealmJoinWatcher: boolean;
  setEnableRealmJoinWatcher: (v: boolean) => void;
  handleSaveAgentSettings: () => void;
  handleResetAgentSettings: () => void;

  // Agent analyzers
  enableLocalAdminAnalyzer: boolean;
  setEnableLocalAdminAnalyzer: (v: boolean) => void;
  localAdminAllowedAccounts: string[];
  setLocalAdminAllowedAccounts: (v: string[]) => void;
  newAllowedAccount: string;
  setNewAllowedAccount: (v: string) => void;
  enableSoftwareInventoryAnalyzer: boolean;
  setEnableSoftwareInventoryAnalyzer: (v: boolean) => void;
  enableIntegrityBypassAnalyzer: boolean;
  setEnableIntegrityBypassAnalyzer: (v: boolean) => void;
  enableConsoleBypassDetection: boolean;
  setEnableConsoleBypassDetection: (v: boolean) => void;
  handleSaveAgentAnalyzers: () => void;
  handleResetAgentAnalyzers: () => void;

  // Unrestricted mode
  unrestrictedMode: boolean;
  setUnrestrictedMode: (v: boolean) => void;
  handleSaveUnrestrictedMode: (value: boolean) => Promise<boolean>;

  // Notifications / channels
  notificationChannels: NotificationChannel[];
  setNotificationChannels: (v: SetStateAction<NotificationChannel[]>) => void;
  testingChannelId: string | null;
  testChannelResult: { channelId: string; success: boolean; message: string } | null;
  handleTestChannel: (channelId: string) => Promise<void>;
  handleSaveNotifications: () => void;
  handleResetNotifications: () => void;

  // SLA Targets
  slaTargetSuccessRate: number | null;
  setSlaTargetSuccessRate: (v: number | null) => void;
  slaTargetMaxDurationMinutes: number | null;
  setSlaTargetMaxDurationMinutes: (v: number | null) => void;
  slaTargetAppInstallSuccessRate: number | null;
  setSlaTargetAppInstallSuccessRate: (v: number | null) => void;
  slaNotifyOnSuccessRateBreach: boolean;
  setSlaNotifyOnSuccessRateBreach: (v: boolean) => void;
  slaSuccessRateNotifyThreshold: number | null;
  setSlaSuccessRateNotifyThreshold: (v: number | null) => void;
  slaNotifyOnDurationBreach: boolean;
  setSlaNotifyOnDurationBreach: (v: boolean) => void;
  slaNotifyOnAppInstallBreach: boolean;
  setSlaNotifyOnAppInstallBreach: (v: boolean) => void;
  slaNotifyOnConsecutiveFailures: boolean;
  setSlaNotifyOnConsecutiveFailures: (v: boolean) => void;
  slaConsecutiveFailureThreshold: number;
  setSlaConsecutiveFailureThreshold: (v: number) => void;
  handleSaveSlaTargets: () => void;
  handleResetSlaTargets: () => void;

  // Diagnostics
  diagnosticsBlobSasUrl: string;
  setDiagnosticsBlobSasUrl: (v: string) => void;
  diagnosticsUploadMode: string;
  setDiagnosticsUploadMode: (v: string) => void;
  diagnosticsUploadDestination: string;
  setDiagnosticsUploadDestination: (v: string) => void;
  tenantDiagPaths: DiagnosticsLogPath[];
  setTenantDiagPaths: (v: DiagnosticsLogPath[]) => void;
  newDiagPath: string;
  setNewDiagPath: (v: string) => void;
  newDiagDesc: string;
  setNewDiagDesc: (v: string) => void;
  handleSaveDiagnostics: () => void;
  handleResetDiagnostics: () => void;

  // Admin management
  admins: TenantAdmin[];
  loadingAdmins: boolean;
  newAdminEmail: string;
  setNewAdminEmail: (v: string) => void;
  newMemberRole: string;
  setNewMemberRole: (v: string) => void;
  addingAdmin: boolean;
  removingAdmin: string | null;
  togglingAdmin: string | null;
  adminSearchQuery: string;
  setAdminSearchQuery: (v: string) => void;
  currentAdminPage: number;
  setCurrentAdminPage: (v: SetStateAction<number>) => void;
  handleAddAdmin: () => Promise<void>;
  handleRemoveAdmin: (adminUpn: string) => Promise<void>;
  handleToggleTenantAdmin: (adminUpn: string, isEnabled: boolean) => Promise<void>;
  handleUpdatePermissions: (adminUpn: string, role: string, canManageBootstrapTokens: boolean) => Promise<void>;

  // Bootstrap sessions
  bootstrapSessions: BootstrapSessionItem[];
  bootstrapLoading: boolean;
  fetchBootstrapSessions: () => Promise<void>;
  createBootstrapSession: (validityHours: number, label: string) => Promise<string | null>;
  revokeBootstrapSession: (code: string) => Promise<void>;

  // Data management
  dataRetentionDays: number;
  setDataRetentionDays: (v: number) => void;
  sessionTimeoutHours: number;
  setSessionTimeoutHours: (v: number) => void;
  handleSaveDataManagement: () => void;
  handleResetDataManagement: () => void;

  // Offboarding
  showOffboardDialog: boolean;
  setShowOffboardDialog: (v: boolean) => void;
  offboardConfirmText: string;
  setOffboardConfirmText: (v: string) => void;
  offboarding: boolean;
  offboardError: string | null;
  setOffboardError: (v: string | null) => void;
  handleOffboard: () => Promise<void>;

  /** Set after the DELETE returns 202; drives the post-confirm drain-barrier banner. */
  offboardingInProgress: OffboardingInProgressInfo | null;

  /** Called by the banner countdown when the cache-drain barrier elapses → triggers logout. */
  handleDrainBarrierElapsed: () => void;

  // Auth helpers
  user: ReturnType<typeof useAuth>["user"];
  getAccessToken: () => Promise<string | null>;
}

const TenantConfigContext = createContext<TenantConfigContextValue | null>(null);

export function useTenantConfig() {
  const ctx = useContext(TenantConfigContext);
  if (!ctx) throw new Error("useTenantConfig must be used within TenantConfigProvider");
  return ctx;
}

// ---------------------------------------------------------------------------
// Provider
// ---------------------------------------------------------------------------
export function TenantConfigProvider({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const { tenantId } = useTenant();
  const { getAccessToken, user, logout } = useAuth();
  const { addNotification } = useNotifications();

  // Operators reach the settings area read-only — only a Tenant Admin or Global Admin may
  // change configuration. Drives section rendering (no Save/Reset bar, disabled inputs)
  // and the defensive guard in saveConfiguration; the backend enforces the same gate.
  const canEditConfig = user?.isTenantAdmin === true || user?.isGlobalAdmin === true;

  // --- State (mirrors old page.tsx lines 35-112) ---
  const [config, setConfig] = useState<TenantConfiguration | null>(null);
  const [admins, setAdmins] = useState<TenantAdmin[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadingAdmins, setLoadingAdmins] = useState(false);
  const [savingSection, setSavingSection] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);
  const [newAdminEmail, setNewAdminEmail] = useState("");
  const [newMemberRole, setNewMemberRole] = useState<string>("Admin");
  const [addingAdmin, setAddingAdmin] = useState(false);
  const [removingAdmin, setRemovingAdmin] = useState<string | null>(null);
  const [togglingAdmin, setTogglingAdmin] = useState<string | null>(null);
  const [adminSearchQuery, setAdminSearchQuery] = useState("");
  const [currentAdminPage, setCurrentAdminPage] = useState(0);

  // Offboard
  const [showOffboardDialog, setShowOffboardDialog] = useState(false);
  const [offboardConfirmText, setOffboardConfirmText] = useState("");
  const [offboarding, setOffboarding] = useState(false);
  const [offboardError, setOffboardError] = useState<string | null>(null);
  const [offboardingInProgress, setOffboardingInProgress] = useState<OffboardingInProgressInfo | null>(null);

  // Bootstrap sessions
  const [bootstrapSessions, setBootstrapSessions] = useState<BootstrapSessionItem[]>([]);
  const [bootstrapLoading, setBootstrapLoading] = useState(false);

  // Form state
  const [manufacturerWhitelist, setManufacturerWhitelist] = useState("Dell*,HP*,Lenovo*,Microsoft Corporation");
  const [modelWhitelist, setModelWhitelist] = useState("*");
  const [webhookNotifyOnHardwareRejection, setWebhookNotifyOnHardwareRejection] = useState(false);
  const [validateAutopilotDevice, setValidateAutopilotDevice] = useState(false);
  const [validateCorporateIdentifier, setValidateCorporateIdentifier] = useState(false);
  const [validateDeviceAssociation, setValidateDeviceAssociation] = useState(false);
  const [validateCloudPcDevice, setValidateCloudPcDevice] = useState(false);
  const [dataRetentionDays, setDataRetentionDays] = useState(90);
  const [sessionTimeoutHours, setSessionTimeoutHours] = useState(5);

  // Collector settings
  const [enablePerformanceCollector, setEnablePerformanceCollector] = useState(true);
  const [performanceCollectorInterval, setPerformanceCollectorInterval] = useState(30);
  const [helloWaitTimeoutSeconds, setHelloWaitTimeoutSeconds] = useState(30);
  const [autopilotConsentInProgress, setAutopilotConsentInProgress] = useState(false);

  // Agent behavior
  const [selfDestructOnComplete, setSelfDestructOnComplete] = useState(true);
  const [keepLogFile, setKeepLogFile] = useState(false);
  const [rebootOnComplete, setRebootOnComplete] = useState(false);
  const [rebootDelaySeconds, setRebootDelaySeconds] = useState(10);
  const [contactEmail, setContactEmail] = useState("");
  const [enableGeoLocation, setEnableGeoLocation] = useState(true);
  const [enableTimezoneAutoSet, setEnableTimezoneAutoSet] = useState(false);
  const [enableDoGroupIdAutoSet, setEnableDoGroupIdAutoSet] = useState(false);
  const [enableImeMatchLog, setEnableImeMatchLog] = useState(false);
  const [enableGatherRuleDebugLog, setEnableGatherRuleDebugLog] = useState(false);
  const [logLevel, setLogLevel] = useState("Info");
  const [showScriptOutput, setShowScriptOutput] = useState(true);
  const [showEnrollmentSummary, setShowEnrollmentSummary] = useState(false);
  const [enrollmentSummaryTimeoutSeconds, setEnrollmentSummaryTimeoutSeconds] = useState(60);
  const [enrollmentSummaryBrandingImageUrl, setEnrollmentSummaryBrandingImageUrl] = useState("");
  const [enrollmentSummaryLaunchRetrySeconds, setEnrollmentSummaryLaunchRetrySeconds] = useState(120);
  const [enableRealmJoinWatcher, setEnableRealmJoinWatcher] = useState(false);
  const [keepAwakeDuringUserEsp, setKeepAwakeDuringUserEsp] = useState(false);

  // Notification channels
  const [notificationChannels, setNotificationChannels] = useState<NotificationChannel[]>([]);
  const [testingChannelId, setTestingChannelId] = useState<string | null>(null);
  const [testChannelResult, setTestChannelResult] = useState<{ channelId: string; success: boolean; message: string } | null>(null);

  // SLA Targets
  const [slaTargetSuccessRate, setSlaTargetSuccessRate] = useState<number | null>(null);
  const [slaTargetMaxDurationMinutes, setSlaTargetMaxDurationMinutes] = useState<number | null>(null);
  const [slaTargetAppInstallSuccessRate, setSlaTargetAppInstallSuccessRate] = useState<number | null>(null);
  const [slaNotifyOnSuccessRateBreach, setSlaNotifyOnSuccessRateBreach] = useState(false);
  const [slaSuccessRateNotifyThreshold, setSlaSuccessRateNotifyThreshold] = useState<number | null>(null);
  const [slaNotifyOnDurationBreach, setSlaNotifyOnDurationBreach] = useState(false);
  const [slaNotifyOnAppInstallBreach, setSlaNotifyOnAppInstallBreach] = useState(false);
  const [slaNotifyOnConsecutiveFailures, setSlaNotifyOnConsecutiveFailures] = useState(false);
  const [slaConsecutiveFailureThreshold, setSlaConsecutiveFailureThreshold] = useState(5);

  // Diagnostics
  const [diagnosticsBlobSasUrl, setDiagnosticsBlobSasUrl] = useState("");
  const [diagnosticsUploadMode, setDiagnosticsUploadMode] = useState("Off");
  // Destination defaults to "CustomerSas" so legacy rows behave exactly as before;
  // Hosted requires an explicit admin click (no silent flip).
  const [diagnosticsUploadDestination, setDiagnosticsUploadDestination] = useState("CustomerSas");
  const [tenantDiagPaths, setTenantDiagPaths] = useState<DiagnosticsLogPath[]>([]);
  const [newDiagPath, setNewDiagPath] = useState("");
  const [newDiagDesc, setNewDiagDesc] = useState("");

  // Agent analyzers
  const [enableLocalAdminAnalyzer, setEnableLocalAdminAnalyzer] = useState(true);
  const [localAdminAllowedAccounts, setLocalAdminAllowedAccounts] = useState<string[]>([]);
  const [newAllowedAccount, setNewAllowedAccount] = useState("");
  const [enableSoftwareInventoryAnalyzer, setEnableSoftwareInventoryAnalyzer] = useState(false);
  const [enableIntegrityBypassAnalyzer, setEnableIntegrityBypassAnalyzer] = useState(true);
  const [enableConsoleBypassDetection, setEnableConsoleBypassDetection] = useState(true);

  // Unrestricted mode
  const [unrestrictedMode, setUnrestrictedMode] = useState(false);

  // Edition / trial surface (from feature-flags; fail-closed Community until loaded)
  const [editionInfo, setEditionInfo] = useState<EditionInfo>(COMMUNITY_DEFAULT);
  const [appHomingFunnelActive, setAppHomingFunnelActive] = useState(false);
  const [homingFlipped, setHomingFlipped] = useState(false);

  // The backend auto-flipped this tenant's app-reg homing during consent-status/access-check.
  // Reflect it locally without a refetch: badge/one-liner surfaces read config.homedAppClientId,
  // and the funnel banner must drop immediately (the tenant is primary-homed now).
  const markHomingFlipped = useCallback((source: "consent-status" | "access-check") => {
    // Strategic trace point: the tenant just moved to the new app registration.
    trackEvent("app_homing_flipped", { source });
    setHomingFlipped(true);
    setAppHomingFunnelActive(false);
    setConfig(prev => (prev ? { ...prev, homedAppClientId: primaryClientId() ?? prev.homedAppClientId } : prev));
  }, []);
  const [startingTrial, setStartingTrial] = useState(false);

  // -----------------------------------------------------------------------
  // Fetch configuration
  // -----------------------------------------------------------------------
  useEffect(() => {
    if (!tenantId) return;

    // Operators reach the settings area read-only (no Save affordances); admins may edit.
    const isAdminOrGA = user?.isTenantAdmin || user?.isGlobalAdmin;

    const fetchConfiguration = async () => {
      try {
        setLoading(true);
        setError(null);

        // Everyone entitled to the settings area loads the full config + feature flags in
        // parallel — the edition surface is resolved SERVER-side (trial expiry math), so it
        // comes from flags, not the raw config. Non-admins (Operators) receive the
        // server-REDACTED copy: the backend clears secrets for every caller without write
        // authority over the target tenant, so showing it read-only is safe.
        const [response, flagsResponse] = await Promise.all([
          authenticatedFetch(api.config.tenant(tenantId), getAccessToken),
          authenticatedFetch(api.config.featureFlags(tenantId), getAccessToken),
        ]);

        let flags: unknown = null;
        if (flagsResponse.ok) {
          try {
            flags = await flagsResponse.json();
            setEditionInfo(parseEditionInfo(flags));
            setAppHomingFunnelActive(
              (flags as { appHomingFunnelActive?: boolean }).appHomingFunnelActive === true);
          } catch { /* fail-closed: keep Community default */ }
        }

        if (!response.ok) {
          // Deploy-order safety net: a backend that still gates the config GET admin-tier
          // 403s an Operator. Fall back to the feature-flags minimal view (pre-change
          // behavior for non-admins) instead of surfacing a load error.
          if (response.status === 403 && !isAdminOrGA && flags && typeof flags === "object") {
            setConfig({
              bootstrapTokenEnabled: (flags as { bootstrapTokenEnabled?: boolean }).bootstrapTokenEnabled,
            } as TenantConfiguration);
            return;
          }
          throw new Error(`Failed to load configuration: ${response.statusText}`);
        }

        const data: TenantConfiguration = await response.json();
        setConfig(data);

        // Update form state
        setManufacturerWhitelist(data.manufacturerWhitelist);
        setModelWhitelist(data.modelWhitelist);
        setWebhookNotifyOnHardwareRejection(data.webhookNotifyOnHardwareRejection ?? false);
        setValidateAutopilotDevice(data.validateAutopilotDevice);
        setValidateCorporateIdentifier(data.validateCorporateIdentifier ?? false);
        setValidateDeviceAssociation(data.validateDeviceAssociation ?? false);
        setValidateCloudPcDevice(data.validateCloudPcDevice ?? false);
        setDataRetentionDays(data.dataRetentionDays ?? 90);
        setSessionTimeoutHours(data.sessionTimeoutHours ?? 5);
        setEnablePerformanceCollector(data.enablePerformanceCollector ?? true);
        setPerformanceCollectorInterval(data.performanceCollectorIntervalSeconds ?? 30);
        setHelloWaitTimeoutSeconds(data.helloWaitTimeoutSeconds ?? 30);
        setSelfDestructOnComplete(data.selfDestructOnComplete ?? true);
        setKeepLogFile(data.keepLogFile ?? false);
        setRebootOnComplete(data.rebootOnComplete ?? false);
        setRebootDelaySeconds(data.rebootDelaySeconds ?? 10);
        setContactEmail(data.contactEmail ?? "");
        setEnableGeoLocation(data.enableGeoLocation ?? true);
        setEnableTimezoneAutoSet(data.enableTimezoneAutoSet ?? false);
        setEnableDoGroupIdAutoSet(data.enableDoGroupIdAutoSet ?? false);
        setEnableImeMatchLog(data.enableImeMatchLog ?? false);
        setEnableGatherRuleDebugLog(data.enableGatherRuleDebugLog ?? false);
        setLogLevel(data.logLevel ?? "Info");
        setShowScriptOutput(data.showScriptOutput ?? true);
        setShowEnrollmentSummary(data.showEnrollmentSummary ?? false);
        setEnrollmentSummaryTimeoutSeconds(data.enrollmentSummaryTimeoutSeconds ?? 60);
        setEnrollmentSummaryBrandingImageUrl(data.enrollmentSummaryBrandingImageUrl ?? "");
        setEnrollmentSummaryLaunchRetrySeconds(data.enrollmentSummaryLaunchRetrySeconds ?? 120);
        setEnableRealmJoinWatcher(data.enableRealmJoinWatcher ?? false);
        setKeepAwakeDuringUserEsp(data.keepAwakeDuringUserEsp ?? false);
        // Notification channels (auto-migrates from the legacy single-webhook fields)
        setNotificationChannels(channelsFromConfig(data));
        // SLA Targets
        setSlaTargetSuccessRate(data.slaTargetSuccessRate ?? null);
        setSlaTargetMaxDurationMinutes(data.slaTargetMaxDurationMinutes ?? null);
        setSlaTargetAppInstallSuccessRate(data.slaTargetAppInstallSuccessRate ?? null);
        setSlaNotifyOnSuccessRateBreach(data.slaNotifyOnSuccessRateBreach ?? false);
        setSlaSuccessRateNotifyThreshold(data.slaSuccessRateNotifyThreshold ?? null);
        setSlaNotifyOnDurationBreach(data.slaNotifyOnDurationBreach ?? false);
        setSlaNotifyOnAppInstallBreach(data.slaNotifyOnAppInstallBreach ?? false);
        setSlaNotifyOnConsecutiveFailures(data.slaNotifyOnConsecutiveFailures ?? false);
        setSlaConsecutiveFailureThreshold(data.slaConsecutiveFailureThreshold ?? 5);
        const sasUrl = data.diagnosticsBlobSasUrl ?? "";
        setDiagnosticsBlobSasUrl(sasUrl);
        setDiagnosticsUploadMode(data.diagnosticsUploadMode ?? "Off");
        setDiagnosticsUploadDestination(data.diagnosticsUploadDestination ?? "CustomerSas");
        try {
          setTenantDiagPaths(data.diagnosticsLogPathsJson ? JSON.parse(data.diagnosticsLogPathsJson) : []);
        } catch {
          setTenantDiagPaths([]);
        }
        setEnableLocalAdminAnalyzer(data.enableLocalAdminAnalyzer ?? true);
        try {
          setLocalAdminAllowedAccounts(data.localAdminAllowedAccountsJson ? JSON.parse(data.localAdminAllowedAccountsJson) : []);
        } catch {
          setLocalAdminAllowedAccounts([]);
        }
        setEnableSoftwareInventoryAnalyzer(data.enableSoftwareInventoryAnalyzer ?? false);
        setEnableIntegrityBypassAnalyzer(data.enableIntegrityBypassAnalyzer ?? true);
        setEnableConsoleBypassDetection(data.enableConsoleBypassDetection ?? true);
        setUnrestrictedMode(data.unrestrictedMode ?? false);

        // Parse SAS expiry and fire notification to bell if needed
        if (sasUrl) {
          const expiry = parseSasExpiry(sasUrl);
          if (expiry) {
            const now = new Date();
            const daysRemaining = Math.ceil((expiry.getTime() - now.getTime()) / (1000 * 60 * 60 * 24));
            if (daysRemaining <= 0) {
              addNotification(
                'error',
                'Diagnostics SAS URL Expired',
                `The Diagnostics SAS URL expired on ${expiry.toLocaleDateString()}. Diagnostics upload is non-functional.`,
                'diagnostics-sas-expiry',
                '/settings/agent/diagnostics'
              );
            } else if (daysRemaining <= 7) {
              addNotification(
                'warning',
                'Diagnostics SAS URL Expiring Soon',
                `The Diagnostics SAS URL expires on ${expiry.toLocaleDateString()} (${daysRemaining} day${daysRemaining === 1 ? '' : 's'} remaining). Please update it soon.`,
                'diagnostics-sas-expiry',
                '/settings/agent/diagnostics'
              );
            }
          }
        }
      } catch (err) {
        if (err instanceof TokenExpiredError) {
          addNotification('error', 'Session Expired', err.message, 'session-expired-error');
        } else {
          console.error("Error fetching configuration:", err);
          setError(err instanceof Error ? err.message : "Failed to load configuration");
        }
      } finally {
        setLoading(false);
      }
    };

    fetchConfiguration();
  }, [tenantId, getAccessToken, addNotification, user?.isTenantAdmin, user?.isGlobalAdmin]);

  // -----------------------------------------------------------------------
  // Fetch admins
  // -----------------------------------------------------------------------
  const fetchAdmins = useCallback(async () => {
    if (!tenantId) return;
    try {
      setLoadingAdmins(true);
      const response = await authenticatedFetch(api.tenants.admins(tenantId), getAccessToken);
      if (!response.ok) {
        throw new Error(`Failed to load admins: ${response.statusText}`);
      }
      const data: TenantAdmin[] = await response.json();
      setAdmins(data);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error("Error fetching admins:", err);
        setError(err instanceof Error ? err.message : "Failed to load admins");
      }
    } finally {
      setLoadingAdmins(false);
    }
  }, [tenantId, getAccessToken, addNotification]);

  useEffect(() => {
    if (!tenantId) return;
    if (!user?.isTenantAdmin && !user?.isGlobalAdmin) return;
    const run = async () => {
      await fetchAdmins();
    };
    void run();
  }, [tenantId, user?.isTenantAdmin, user?.isGlobalAdmin, fetchAdmins]);

  // -----------------------------------------------------------------------
  // Fetch bootstrap sessions
  // -----------------------------------------------------------------------
  const fetchBootstrapSessions = useCallback(async () => {
    if (!tenantId) return;
    try {
      setBootstrapLoading(true);
      const response = await authenticatedFetch(
        api.bootstrap.sessions(tenantId),
        getAccessToken,
      );
      if (response.ok) {
        const data = await response.json();
        setBootstrapSessions(data.sessions || []);
      }
    } catch (err) {
      console.error("Failed to fetch bootstrap sessions:", err);
    } finally {
      setBootstrapLoading(false);
    }
  }, [tenantId, getAccessToken]);

  useEffect(() => {
    // Effective availability (mirrors backend IsBootstrapEnabled): Pro plan includes bootstrap;
    // the per-tenant GA flag is the additive Community enable.
    const bootstrapAvailable = editionInfo.edition === "pro" || config?.bootstrapTokenEnabled;
    if (!tenantId || !bootstrapAvailable) return;
    const run = async () => {
      await fetchBootstrapSessions();
    };
    void run();
  }, [tenantId, editionInfo.edition, config?.bootstrapTokenEnabled, fetchBootstrapSessions]);

  // Built-in sections + global diagnostics paths are read-only context for every role and
  // live in useDiagnosticsPathsCatalog (GET /api/diagnostics/paths), not in this provider.

  // -----------------------------------------------------------------------
  // Save configuration (shared by all sections)
  // -----------------------------------------------------------------------
  const saveConfiguration = useCallback(async (sectionName: SettingsSectionName, overrides?: { validateAutopilotDevice?: boolean; validateCorporateIdentifier?: boolean; validateDeviceAssociation?: boolean; validateCloudPcDevice?: boolean; unrestrictedMode?: boolean }): Promise<boolean> => {
    // Read-only viewers (Operators) have no save affordances; this guard covers any path
    // that still reaches a save (the backend would 403 the PATCH regardless).
    if (!tenantId || !config || !canEditConfig) return false;

    try {
      setSavingSection(sectionName);
      setError(null);
      setSuccessMessage(null);

      const autopilotDeviceValidationValue = overrides?.validateAutopilotDevice ?? validateAutopilotDevice;
      const corporateIdentifierValidationValue = overrides?.validateCorporateIdentifier ?? validateCorporateIdentifier;
      const deviceAssociationValidationValue = overrides?.validateDeviceAssociation ?? validateDeviceAssociation;
      const cloudPcValidationValue = overrides?.validateCloudPcDevice ?? validateCloudPcDevice;
      const unrestrictedModeValue = overrides?.unrestrictedMode ?? unrestrictedMode;

      const updatedConfig: TenantConfiguration = {
        ...config,
        manufacturerWhitelist,
        modelWhitelist,
        webhookNotifyOnHardwareRejection,
        validateAutopilotDevice: autopilotDeviceValidationValue,
        validateCorporateIdentifier: corporateIdentifierValidationValue,
        validateDeviceAssociation: deviceAssociationValidationValue,
        validateCloudPcDevice: cloudPcValidationValue,
        dataRetentionDays,
        sessionTimeoutHours,
        enablePerformanceCollector,
        performanceCollectorIntervalSeconds: performanceCollectorInterval,
        helloWaitTimeoutSeconds,
        selfDestructOnComplete,
        keepLogFile,
        rebootOnComplete,
        rebootDelaySeconds,
        contactEmail: contactEmail.trim(),
        enableGeoLocation,
        enableTimezoneAutoSet,
        enableDoGroupIdAutoSet,
        enableImeMatchLog,
        enableGatherRuleDebugLog,
        logLevel,
        showScriptOutput,
        showEnrollmentSummary,
        enrollmentSummaryTimeoutSeconds,
        enrollmentSummaryBrandingImageUrl: enrollmentSummaryBrandingImageUrl || undefined,
        enrollmentSummaryLaunchRetrySeconds,
        enableRealmJoinWatcher,
        keepAwakeDuringUserEsp,
        // Notification channels are the authoritative config; the legacy single-webhook
        // fields are cleared on save so deleting the last channel can't resurrect a zombie
        // webhook via the backend's legacy-synthesis fallback.
        notificationChannelsJson: notificationChannels.length > 0 ? JSON.stringify(notificationChannels) : "",
        webhookProviderType: 0,
        webhookUrl: undefined,
        webhookCustomHeadersJson: undefined,
        teamsWebhookUrl: undefined,
        // SLA targets
        slaTargetSuccessRate: slaTargetSuccessRate ?? undefined,
        slaTargetMaxDurationMinutes: slaTargetMaxDurationMinutes ?? undefined,
        slaTargetAppInstallSuccessRate: slaTargetAppInstallSuccessRate ?? undefined,
        slaNotifyOnSuccessRateBreach,
        slaSuccessRateNotifyThreshold: slaSuccessRateNotifyThreshold ?? undefined,
        slaNotifyOnDurationBreach,
        slaNotifyOnAppInstallBreach,
        slaNotifyOnConsecutiveFailures,
        slaConsecutiveFailureThreshold,
        diagnosticsBlobSasUrl: diagnosticsBlobSasUrl || undefined,
        diagnosticsUploadMode,
        diagnosticsUploadDestination,
        diagnosticsLogPathsJson: tenantDiagPaths.length > 0 ? JSON.stringify(tenantDiagPaths) : "",
        enableLocalAdminAnalyzer,
        localAdminAllowedAccountsJson: localAdminAllowedAccounts.length > 0 ? JSON.stringify(localAdminAllowedAccounts) : "",
        enableSoftwareInventoryAnalyzer,
        enableIntegrityBypassAnalyzer,
        enableConsoleBypassDetection,
        unrestrictedMode: unrestrictedModeValue,
      };

      // Per-section PATCH: send ONLY this section's fields (plus documented write-throughs)
      // that actually differ from the loaded config. The other ~90 fields never round-trip,
      // so a stale read cannot revert unrelated fields (the 2026-07-31 incident class), and
      // GA-only toggles a tenant admin cannot edit are simply never in the payload.
      const spec: SectionFieldSpec = SECTION_FIELD_MAP[sectionName];
      const patchFields: Record<string, unknown> = {};
      for (const field of [...spec.fields, ...(spec.alsoWrites ?? [])]) {
        // undefined and null both mean "cleared" on the wire; PATCH expresses a clear as
        // an explicit JSON null (an omitted key would leave the stored value untouched).
        const next = (updatedConfig as unknown as Record<string, unknown>)[field] ?? null;
        const prev = (config as unknown as Record<string, unknown>)[field] ?? null;
        if (JSON.stringify(next) !== JSON.stringify(prev)) {
          patchFields[field] = next;
        }
      }

      if (Object.keys(patchFields).length > 0) {
        const response = await authenticatedFetch(api.config.fields(tenantId), getAccessToken, {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ fields: patchFields, reason: `settings:${sectionName}` }),
        });

        if (!response.ok) {
          const errorData = await response.json().catch(() => ({}));
          throw new Error(errorData.message || errorData.error || `Failed to save configuration: ${response.statusText}`);
        }

        // The PATCH response carries applied field names + masked diff, not the config —
        // the backend verified exactly these fields changed, so merge them locally.
        await response.json().catch(() => ({}));
        setConfig({ ...config, ...patchFields } as TenantConfiguration);
      }

      // Sync the gate-relevant form state with what is now persisted (merge of loaded
      // config + this patch) — mirrors the old PUT-response sync.
      const persisted = { ...config, ...patchFields } as TenantConfiguration;
      setValidateAutopilotDevice(persisted.validateAutopilotDevice);
      setValidateCorporateIdentifier(persisted.validateCorporateIdentifier ?? false);
      setValidateDeviceAssociation(persisted.validateDeviceAssociation ?? false);
      setValidateCloudPcDevice(persisted.validateCloudPcDevice ?? false);
      setUnrestrictedMode(persisted.unrestrictedMode ?? false);
      trackEvent("settings_saved", { section: sectionName, fieldCount: Object.keys(patchFields).length });
      setSuccessMessage("Configuration saved successfully!");
      setTimeout(() => setSuccessMessage(null), 3000);
      return true;
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        const msg = err instanceof Error ? err.message : "Failed to save configuration";
        trackEvent("settings_error", { action: "save", section: sectionName, error: msg });
        setError(msg);
      }
      return false;
    } finally {
      setSavingSection(null);
    }
  }, [
    tenantId, config, canEditConfig, getAccessToken, addNotification,
    manufacturerWhitelist, modelWhitelist, webhookNotifyOnHardwareRejection, validateAutopilotDevice, validateCorporateIdentifier, validateDeviceAssociation, validateCloudPcDevice,
    dataRetentionDays, sessionTimeoutHours, enablePerformanceCollector, performanceCollectorInterval,
    helloWaitTimeoutSeconds, selfDestructOnComplete, keepLogFile, rebootOnComplete, rebootDelaySeconds,
    contactEmail, enableGeoLocation, enableTimezoneAutoSet, enableDoGroupIdAutoSet, enableImeMatchLog, enableGatherRuleDebugLog, logLevel, showScriptOutput, showEnrollmentSummary,
    enrollmentSummaryTimeoutSeconds, enrollmentSummaryBrandingImageUrl, enrollmentSummaryLaunchRetrySeconds,
    enableRealmJoinWatcher, keepAwakeDuringUserEsp,
    notificationChannels,
    slaTargetSuccessRate, slaTargetMaxDurationMinutes, slaTargetAppInstallSuccessRate,
    slaNotifyOnSuccessRateBreach, slaSuccessRateNotifyThreshold, slaNotifyOnDurationBreach,
    slaNotifyOnAppInstallBreach, slaNotifyOnConsecutiveFailures, slaConsecutiveFailureThreshold,
    diagnosticsBlobSasUrl, diagnosticsUploadMode, diagnosticsUploadDestination, tenantDiagPaths,
    enableLocalAdminAnalyzer, localAdminAllowedAccounts, enableSoftwareInventoryAnalyzer,
    enableIntegrityBypassAnalyzer, enableConsoleBypassDetection, unrestrictedMode,
  ]);

  // -----------------------------------------------------------------------
  // Consent flow
  // -----------------------------------------------------------------------

  // Probe-only: fetch the access-check and classify it. No persistence, no messaging.
  // A non-ok response or thrown error (except token-expiry) is treated as "transient" (retryable),
  // never "absent" — we must not conclude "no access" from an inconclusive probe.
  const probeAccessCheck = useCallback(async (): Promise<AccessCheckOutcome> => {
    if (!tenantId) return "absent";

    let ok = false;
    let payload: AccessCheckPayload | undefined;
    try {
      const response = await authenticatedFetch(api.config.autopilotAccessCheck(tenantId), getAccessToken);
      ok = response.ok;
      if (ok) payload = await response.json();
    } catch (err) {
      if (err instanceof TokenExpiredError) throw err;
      return "transient";
    }
    // Side signal, orthogonal to the access classification: the probe may have auto-flipped
    // the tenant's app-reg homing (self-service migration).
    if (payload?.homingFlipped) markHomingFlipped("access-check");
    return classifyAccessCheck(ok, payload);
  }, [tenantId, getAccessToken, markHomingFlipped]);

  // Direct gate persist for the toggle UI (disable + second-gate enable): same shared config
  // PUT, explicit override values. saveConfiguration re-syncs the local gate state from the
  // server response, so the toggle reflects the persisted truth (or snaps back on failure).
  const saveValidationGate = useCallback(
    (changes: { validateAutopilotDevice?: boolean; validateCorporateIdentifier?: boolean }): Promise<boolean> =>
      saveConfiguration("autopilotValidation", changes),
    [saveConfiguration],
  );

  // Persist the validation gate bool for a trigger via the shared config PUT. Returns true ONLY
  // on a confirmed server persist — saveConfiguration reports failure as false (and sets the
  // error). Callers MUST NOT claim success without checking this, or the admin sees "enabled"
  // while the gate bool never landed. Message is caller-owned (reconcile vs normal-consent differ).
  const persistValidation = useCallback(
    async (trigger: ValidationTrigger): Promise<boolean> => {
      if (trigger === "corporate") return saveConfiguration("autopilotValidation", { validateCorporateIdentifier: true });
      if (trigger === "device-preparation") return saveConfiguration("autopilotValidation", { validateDeviceAssociation: true });
      return saveConfiguration("autopilotValidation", { validateAutopilotDevice: true });
    },
    [saveConfiguration],
  );

  // Rights-less-admin reconcile: probe whether the app's core validation permission is already
  // effectively granted in this tenant (pre-approved by someone with consent rights). If so,
  // persist the gate bool — opening the UI badge AND the agent hard gate — without ever running
  // the /adminconsent redirect the rights-less admin can't complete. Returns "reconciled"
  // (enabled), "transient" (inconclusive, retry), "absent" (no access), or "failed" (access
  // present but the persist did not land — so callers never show success on a swallowed save error).
  const tryReconcilePreApprovedConsent = useCallback(
    async (trigger: ValidationTrigger): Promise<AccessCheckOutcome | "failed"> => {
      const outcome = await probeAccessCheck();
      if (outcome !== "reconciled") return outcome;

      const saved = await persistValidation(trigger);
      if (!saved) return "failed";

      setSuccessMessage(`Access is already approved by your organization — ${validationLabel(trigger)}${validationEnabledSuffix(trigger)}`);
      return "reconciled";
    },
    [probeAccessCheck, persistValidation],
  );

  const beginDeviceValidationConsentFlow = useCallback(async (trigger: "autopilot" | "corporate" | "device-preparation") => {
    if (!tenantId) return;
    try {
      setAutopilotConsentInProgress(true);
      setError(null);
      setSuccessMessage(null);

      const redirectUri = `${window.location.origin}/settings/tenant/autopilot`;
      const response = await authenticatedFetch(
        api.config.autopilotConsentUrl(tenantId, redirectUri),
        getAccessToken,
      );

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.error || `Failed to start consent flow: ${response.statusText}`);
      }

      const data = await response.json();
      if (!data.consentUrl) {
        throw new Error("Backend did not return a consent URL.");
      }

      // Trace the funnel entry: this consent redirect targets the NEW app and will auto-flip
      // the tenant on verified return. AI's pagehide beacon carries it out past the redirect.
      trackEvent("consent_flow_started", { trigger, funnel: data.willAutoFlipHoming === true });

      sessionStorage.setItem("deviceValidationConsentPending", "true");
      sessionStorage.setItem("consentTrigger", trigger);
      window.location.href = data.consentUrl;
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        trackEvent("consent_flow_start_failed", {
          trigger,
          error: err instanceof Error ? err.message : String(err),
        });
        setError(err instanceof Error ? err.message : "Failed to start admin consent flow");
      }
      setAutopilotConsentInProgress(false);
    }
  }, [tenantId, getAccessToken, addNotification]);

  // Handle consent callback
  useEffect(() => {
    const handleConsentCallback = async () => {
      if (!tenantId || !config) return;

      const wasPendingNew = sessionStorage.getItem("deviceValidationConsentPending") === "true";
      const wasPendingOld = sessionStorage.getItem("autopilotDeviceValidationPending") === "true";
      if (!wasPendingNew && !wasPendingOld) return;

      const queryParams = new URLSearchParams(window.location.search);
      const adminConsent = queryParams.get("admin_consent");
      const consentError = queryParams.get("error");
      const consentErrorDescription = queryParams.get("error_description");

      if (!adminConsent && !consentError) return;

      const trigger = sessionStorage.getItem("consentTrigger") ?? "autopilot";
      sessionStorage.removeItem("deviceValidationConsentPending");
      sessionStorage.removeItem("autopilotDeviceValidationPending");
      sessionStorage.removeItem("consentTrigger");

      if (consentError) {
        // Browser-side trace of the AAD error (backend gets the detailed report below):
        // queryable next to app_homing_flipped so a broken funnel shows up immediately.
        trackEvent("consent_flow_error", { trigger, error: consentError });

        // Report consent failure to backend for observability —
        // without this, Azure AD errors (e.g. AADSTS50011 redirect mismatch)
        // are invisible to our monitoring.
        try {
          await authenticatedFetch(api.config.autopilotConsentFailure(tenantId), getAccessToken, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              error: consentError,
              errorDescription: consentErrorDescription ? decodeURIComponent(consentErrorDescription) : undefined,
            }),
          });
        } catch {
          // Best-effort — don't block the UI if reporting fails
        }

        // Rights-less-admin path: the redirect may have failed only because THIS admin lacks
        // consent rights, while the app is already pre-approved in the tenant. Probe and, if the
        // permission is effectively present, silently enable instead of surfacing the error.
        try {
          const reconcileTrigger: ValidationTrigger =
            trigger === "corporate" ? "corporate" : trigger === "device-preparation" ? "device-preparation" : "autopilot";
          const outcome = await tryReconcilePreApprovedConsent(reconcileTrigger);
          if (outcome === "reconciled" || outcome === "failed") {
            // "reconciled": silently enabled. "failed": access was present but the config persist
            // failed — saveConfiguration already surfaced that error; don't overwrite it with the
            // (now misleading) consent error.
            setAutopilotConsentInProgress(false);
            router.replace("/settings/tenant/autopilot");
            return;
          }
        } catch (err) {
          if (err instanceof TokenExpiredError) {
            addNotification('error', 'Session Expired', err.message, 'session-expired-error');
            setAutopilotConsentInProgress(false);
            router.replace("/settings/tenant/autopilot");
            return;
          }
          // otherwise fall through to surface the original consent error
        }

        const errorText = consentErrorDescription
          ? `${consentError}: ${decodeURIComponent(consentErrorDescription)}`
          : consentError;
        setError(`Admin consent failed: ${errorText}`);
        setAutopilotConsentInProgress(false);
        router.replace("/settings/tenant/autopilot");
        return;
      }

      try {
        setAutopilotConsentInProgress(true);

        const statusResponse = await authenticatedFetch(
          api.config.autopilotConsentStatus(tenantId),
          getAccessToken,
        );

        if (!statusResponse.ok) {
          const errorData = await statusResponse.json().catch(() => ({}));
          throw new Error(errorData.error || `Consent validation failed: ${statusResponse.statusText}`);
        }

        const statusData = await statusResponse.json();
        if (statusData.homingFlipped) markHomingFlipped("consent-status");
        if (!statusData.isConsented) {
          throw new Error(statusData.message || "Consent is not active yet for this tenant.");
        }

        // Best-effort ops-event: pairs with ConsentFlowStarted/Failed so admins can see
        // whether repeated failures eventually resolved. Don't block the UI if it fails.
        try {
          await authenticatedFetch(api.config.autopilotConsentSuccess(tenantId), getAccessToken, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ trigger }),
          });
        } catch {
          // swallow — observability only
        }

        // Verify the concrete role is present before opening the gate. consent-status only proves
        // a token is acquirable (SP provisioned) — NOT that DeviceManagementServiceConfig.Read.All
        // is in the roles claim. Persist ONLY on an authoritative "reconciled": a transient probe
        // (timeout / Graph 5xx / propagation lag) is inconclusive, so opening the gate on it would
        // re-introduce the exact risk this check closes (gate open while the role is unproven).
        const reconcileTrigger: ValidationTrigger =
          trigger === "corporate" ? "corporate" : trigger === "device-preparation" ? "device-preparation" : "autopilot";

        // AAD consent-propagation awareness: right after a successful admin consent the app-only
        // token often mints with an EMPTY roles claim for 30-90s (observed live 2026-07-31,
        // tenant 5ca2b350: token 43s post-consent with GrantedRoleCount=0) — a single-shot probe
        // misclassified that window as "permission not granted" and showed a hard error while
        // everything was actually fine. The access-check invalidates + re-mints fresh on every
        // call, so a bounded poll converges as soon as Microsoft has propagated the role. The
        // consent-in-progress spinner stays up for the duration (finally-block clears it).
        let probe: AccessCheckOutcome = "absent";
        let attempts = 0;
        for (attempts = 1; attempts <= 5; attempts++) {
          if (attempts > 1) await new Promise((resolve) => setTimeout(resolve, 15000));
          probe = await probeAccessCheck();
          if (probe === "reconciled") break;
        }

        if (probe === "reconciled") {
          if (attempts > 1) trackEvent("consent_verify_propagated", { trigger, attempts: String(attempts) });
          const saved = await persistValidation(reconcileTrigger);
          if (saved) {
            setSuccessMessage(`${validationLabel(reconcileTrigger)}${validationEnabledSuffix(reconcileTrigger)}`);
          }
        } else if (probe === "transient") {
          trackEvent("consent_verify_failed", { trigger, stage: "role-propagating", attempts: String(attempts - 1) });
          setError("Admin consent succeeded, but the required permission could not be confirmed yet (access is still propagating). Please retry in a moment — toggle the option again or use 'Detect existing access'.");
        } else {
          // "absent" after ~a minute of polling — either the role really is missing from the
          // consent, or Microsoft's propagation is unusually slow. Keep the message actionable
          // for both without sounding fatal.
          trackEvent("consent_verify_failed", { trigger, stage: "role-missing", attempts: String(attempts - 1) });
          setError("Admin consent succeeded, but the permission (DeviceManagementServiceConfig.Read.All) has not shown up on the app in this tenant yet. This can simply be Microsoft still propagating the consent — wait a minute and use 'Detect existing access'. If it persists, re-run the consent and ensure the permission is included.");
        }
        router.replace("/settings/tenant/autopilot");
      } catch (err) {
        if (err instanceof TokenExpiredError) {
          addNotification('error', 'Session Expired', err.message, 'session-expired-error');
        } else {
          // Covers consent-status non-ok and "not consented yet" (both throw above).
          trackEvent("consent_verify_failed", {
            trigger,
            stage: "status-check",
            error: err instanceof Error ? err.message : String(err),
          });
          setError(err instanceof Error ? err.message : "Failed to verify consent");
        }
      } finally {
        setAutopilotConsentInProgress(false);
      }
    };

    handleConsentCallback();
  }, [tenantId, config, router, getAccessToken, addNotification, tryReconcilePreApprovedConsent, probeAccessCheck, persistValidation, markHomingFlipped]);

  // Manual "Detect existing access" affordance — for admins who never even attempt the consent
  // redirect because they know they lack consent rights. Probes and, on success, enables
  // validation silently; otherwise surfaces an actionable message.
  const detectExistingAccess = useCallback(async (trigger: ValidationTrigger) => {
    if (!tenantId) return;
    try {
      setAutopilotConsentInProgress(true);
      setError(null);
      setSuccessMessage(null);

      const outcome = await tryReconcilePreApprovedConsent(trigger);
      if (outcome === "transient") {
        trackEvent("detect_access_failed", { trigger, outcome });
        setError("Couldn't verify access right now (timed out). Please try again in a moment.");
      } else if (outcome === "absent") {
        trackEvent("detect_access_failed", { trigger, outcome });
        setError("No existing access detected for this tenant. Complete admin consent, or ask someone with consent rights (Global Administrator or Privileged Role Administrator) to approve the app first.");
      }
      // "reconciled" => success message already set by the helper.
      // "failed" => access present but persist failed; saveConfiguration already set the error.
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        setError(err instanceof Error ? err.message : "Failed to detect existing access");
      }
    } finally {
      setAutopilotConsentInProgress(false);
    }
  }, [tenantId, tryReconcilePreApprovedConsent, addNotification]);

  // -----------------------------------------------------------------------
  // Test webhook channel
  // -----------------------------------------------------------------------
  const handleTestChannel = useCallback(async (channelId: string) => {
    if (!tenantId) return;
    setTestingChannelId(channelId);
    setTestChannelResult(null);
    try {
      const response = await authenticatedFetch(api.config.testNotification(tenantId), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ channelId }),
      });
      const data = await response.json();
      setTestChannelResult({ channelId, success: data.success, message: data.message });
    } catch (err) {
      setTestChannelResult({ channelId, success: false, message: err instanceof Error ? err.message : "Failed to send test notification." });
    } finally {
      setTestingChannelId(null);
    }
  }, [tenantId, getAccessToken]);

  // -----------------------------------------------------------------------
  // Per-section save/reset handlers
  // -----------------------------------------------------------------------
  const handleSaveHardwareWhitelist = useCallback(() => saveConfiguration("hardwareWhitelist"), [saveConfiguration]);
  const handleResetHardwareWhitelist = useCallback(() => {
    if (!config) return;
    setManufacturerWhitelist(config.manufacturerWhitelist);
    setModelWhitelist(config.modelWhitelist);
    setWebhookNotifyOnHardwareRejection(config.webhookNotifyOnHardwareRejection ?? false);
    // The hardware-rejection toggle writes through to the per-channel flags — restore those
    // to their saved values too, without disturbing other unsaved channel edits.
    const savedById = new Map(channelsFromConfig(config).map((c) => [c.id, c]));
    setNotificationChannels((prev) => prev.map((c) => {
      const saved = savedById.get(c.id);
      return saved ? { ...c, notifyOnHardwareRejection: saved.notifyOnHardwareRejection } : c;
    }));
  }, [config]);

  // Hardware-rejection notify: with channels configured, the hardware-section toggle is a
  // convenience view over the per-channel flags (checked = any enabled channel opted in;
  // toggling writes through to every channel). Without channels it edits the legacy tenant
  // flag, which the backend maps into the synthesized channel.
  const effectiveHwRejectionNotify = notificationChannels.length > 0
    ? notificationChannels.some((c) => c.enabled && c.notifyOnHardwareRejection)
    : webhookNotifyOnHardwareRejection;
  const setHwRejectionNotifyWriteThrough = useCallback((v: boolean) => {
    setWebhookNotifyOnHardwareRejection(v);
    setNotificationChannels((prev) => prev.map((c) => ({ ...c, notifyOnHardwareRejection: v })));
  }, []);

  const handleSaveAgentSettings = useCallback(() => saveConfiguration("agentSettings"), [saveConfiguration]);
  const handleResetAgentSettings = useCallback(() => {
    if (!config) return;
    setEnablePerformanceCollector(config.enablePerformanceCollector ?? true);
    setPerformanceCollectorInterval(config.performanceCollectorIntervalSeconds ?? 30);
    setHelloWaitTimeoutSeconds(config.helloWaitTimeoutSeconds ?? 30);
    setSelfDestructOnComplete(config.selfDestructOnComplete ?? true);
    setKeepLogFile(config.keepLogFile ?? false);
    setRebootOnComplete(config.rebootOnComplete ?? false);
    setRebootDelaySeconds(config.rebootDelaySeconds ?? 10);
    // contactEmail deliberately NOT reset here — it is owned by the Contact section
    // (resetting it from the Agent Settings card silently discarded unsaved Contact edits).
    setEnableGeoLocation(config.enableGeoLocation ?? true);
    setEnableTimezoneAutoSet(config.enableTimezoneAutoSet ?? false);
    setEnableDoGroupIdAutoSet(config.enableDoGroupIdAutoSet ?? false);
    setEnableImeMatchLog(config.enableImeMatchLog ?? false);
    setEnableGatherRuleDebugLog(config.enableGatherRuleDebugLog ?? false);
    setLogLevel(config.logLevel ?? "Info");
    setShowScriptOutput(config.showScriptOutput ?? true);
    setShowEnrollmentSummary(config.showEnrollmentSummary ?? false);
    setEnrollmentSummaryTimeoutSeconds(config.enrollmentSummaryTimeoutSeconds ?? 60);
    setEnrollmentSummaryBrandingImageUrl(config.enrollmentSummaryBrandingImageUrl ?? "");
    setEnrollmentSummaryLaunchRetrySeconds(config.enrollmentSummaryLaunchRetrySeconds ?? 120);
    setEnableRealmJoinWatcher(config.enableRealmJoinWatcher ?? false);
    setKeepAwakeDuringUserEsp(config.keepAwakeDuringUserEsp ?? false);
  }, [config]);

  const handleSaveAgentAnalyzers = useCallback(() => saveConfiguration("agentAnalyzers"), [saveConfiguration]);
  const handleResetAgentAnalyzers = useCallback(() => {
    if (!config) return;
    setEnableLocalAdminAnalyzer(config.enableLocalAdminAnalyzer ?? true);
    try {
      setLocalAdminAllowedAccounts(config.localAdminAllowedAccountsJson ? JSON.parse(config.localAdminAllowedAccountsJson) : []);
    } catch { setLocalAdminAllowedAccounts([]); }
    setNewAllowedAccount("");
    setEnableSoftwareInventoryAnalyzer(config.enableSoftwareInventoryAnalyzer ?? false);
    setEnableIntegrityBypassAnalyzer(config.enableIntegrityBypassAnalyzer ?? true);
    setEnableConsoleBypassDetection(config.enableConsoleBypassDetection ?? true);
  }, [config]);

  const handleSaveNotifications = useCallback(() => saveConfiguration("notifications"), [saveConfiguration]);
  const handleResetNotifications = useCallback(() => {
    if (!config) return;
    setNotificationChannels(channelsFromConfig(config));
    setTestChannelResult(null);
  }, [config]);

  const handleSaveSlaTargets = useCallback(() => saveConfiguration("slaTargets"), [saveConfiguration]);
  const handleResetSlaTargets = useCallback(() => {
    if (!config) return;
    setSlaTargetSuccessRate(config.slaTargetSuccessRate ?? null);
    setSlaTargetMaxDurationMinutes(config.slaTargetMaxDurationMinutes ?? null);
    setSlaTargetAppInstallSuccessRate(config.slaTargetAppInstallSuccessRate ?? null);
    setSlaNotifyOnSuccessRateBreach(config.slaNotifyOnSuccessRateBreach ?? false);
    setSlaSuccessRateNotifyThreshold(config.slaSuccessRateNotifyThreshold ?? null);
    setSlaNotifyOnDurationBreach(config.slaNotifyOnDurationBreach ?? false);
    setSlaNotifyOnAppInstallBreach(config.slaNotifyOnAppInstallBreach ?? false);
    setSlaNotifyOnConsecutiveFailures(config.slaNotifyOnConsecutiveFailures ?? false);
    setSlaConsecutiveFailureThreshold(config.slaConsecutiveFailureThreshold ?? 5);
  }, [config]);

  const handleSaveContact = useCallback(() => saveConfiguration("contact"), [saveConfiguration]);
  const handleResetContact = useCallback(() => {
    if (!config) return;
    setContactEmail(config.contactEmail ?? "");
  }, [config]);

  const handleSaveDiagnostics = useCallback(() => saveConfiguration("diagnostics"), [saveConfiguration]);
  const handleResetDiagnostics = useCallback(() => {
    if (!config) return;
    setDiagnosticsBlobSasUrl(config.diagnosticsBlobSasUrl ?? "");
    setDiagnosticsUploadMode(config.diagnosticsUploadMode ?? "Off");
    setDiagnosticsUploadDestination(config.diagnosticsUploadDestination ?? "CustomerSas");
    try {
      setTenantDiagPaths(config.diagnosticsLogPathsJson ? JSON.parse(config.diagnosticsLogPathsJson) : []);
    } catch { setTenantDiagPaths([]); }
  }, [config]);

  const handleSaveDataManagement = useCallback(() => saveConfiguration("dataManagement"), [saveConfiguration]);
  const handleResetDataManagement = useCallback(() => {
    if (!config) return;
    setDataRetentionDays(config.dataRetentionDays ?? 90);
    setSessionTimeoutHours(config.sessionTimeoutHours ?? 5);
  }, [config]);

  // Persist the toggle with the new value passed explicitly. The on/off value MUST be threaded
  // through as an override rather than read from `unrestrictedMode` state: the toggle calls this
  // synchronously after setUnrestrictedMode(...), so the closed-over state is still stale at the
  // time the PUT body is built (matches the handleToggleDeviceAssociationValidation pattern).
  const handleSaveUnrestrictedMode = useCallback(
    (value: boolean) => saveConfiguration("unrestrictedMode", { unrestrictedMode: value }),
    [saveConfiguration],
  );

  /**
   * Toggle the DevPrep "Device association" shadow validation. No consent flow needed —
   * the Graph permission is already covered by the existing Autopilot/Corporate validators
   * and the result is observational (does not gate enrollment in Phase A).
   */
  const handleToggleDeviceAssociationValidation = useCallback(async (newValue: boolean) => {
    setValidateDeviceAssociation(newValue);
    await saveConfiguration("autopilotValidation", { validateDeviceAssociation: newValue });
  }, [saveConfiguration]);

  /**
   * Toggle the Windows 365 Cloud PC validation fallback. No consent flow — the backing
   * CloudPC.Read.All permission is an Optional Graph capabilities add-on (grant script);
   * without the grant the backend simply keeps rejecting Cloud PCs with a pointer to the
   * add-on, so enabling early is safe.
   */
  const handleToggleCloudPcValidation = useCallback(async (newValue: boolean) => {
    setValidateCloudPcDevice(newValue);
    await saveConfiguration("autopilotValidation", { validateCloudPcDevice: newValue });
  }, [saveConfiguration]);

  // -----------------------------------------------------------------------
  // Admin management handlers
  // -----------------------------------------------------------------------
  const handleAddAdmin = useCallback(async () => {
    if (!tenantId || !newAdminEmail.trim()) return;
    try {
      setAddingAdmin(true);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(api.tenants.admins(tenantId), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ upn: newAdminEmail.trim(), role: newMemberRole }),
      });

      if (!response.ok) {
        const errorData = await response.json();
        throw new Error(errorData.error || `Failed to add member: ${response.statusText}`);
      }

      trackEvent("admin_member_added", { role: newMemberRole });
      setSuccessMessage(`${newMemberRole} ${newAdminEmail} added successfully!`);
      setNewAdminEmail("");
      setNewMemberRole("Admin");
      await fetchAdmins();
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error("Error adding admin:", err);
        const msg = err instanceof Error ? err.message : "Failed to add admin";
        trackEvent("settings_error", { action: "add_admin", error: msg });
        setError(msg);
      }
    } finally {
      setAddingAdmin(false);
    }
  }, [tenantId, newAdminEmail, newMemberRole, getAccessToken, addNotification, fetchAdmins]);

  const handleRemoveAdmin = useCallback(async (adminUpn: string) => {
    if (!tenantId) return;
    if (!confirm(`Are you sure you want to remove ${adminUpn} as an admin?`)) return;

    try {
      setRemovingAdmin(adminUpn);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(api.tenants.admin(tenantId, adminUpn), getAccessToken, {
        method: "DELETE",
      });

      if (!response.ok) {
        const errorData = await response.json();
        throw new Error(errorData.error || `Failed to remove admin: ${response.statusText}`);
      }

      trackEvent("admin_member_removed");
      setSuccessMessage(`Admin ${adminUpn} removed successfully!`);
      await fetchAdmins();
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error("Error removing admin:", err);
        const msg = err instanceof Error ? err.message : "Failed to remove admin";
        trackEvent("settings_error", { action: "remove_admin", error: msg });
        setError(msg);
      }
    } finally {
      setRemovingAdmin(null);
    }
  }, [tenantId, getAccessToken, addNotification, fetchAdmins]);

  const handleToggleTenantAdmin = useCallback(async (adminUpn: string, isEnabled: boolean) => {
    if (!tenantId) return;
    const action = isEnabled ? "disable" : "enable";

    try {
      setTogglingAdmin(adminUpn);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(
        api.tenants.adminAction(tenantId, adminUpn, action),
        getAccessToken,
        { method: "PATCH" },
      );

      if (!response.ok) {
        let errorData;
        try { errorData = await response.json(); } catch { errorData = { error: `Failed to ${action} admin: ${response.statusText}` }; }
        throw new Error(errorData.error || `Failed to ${action} admin: ${response.statusText}`);
      }

      trackEvent("admin_member_toggled", { action });
      setSuccessMessage(`Admin ${adminUpn} ${action}d successfully!`);
      await fetchAdmins();
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error(`Error ${action}ing admin:`, err);
        const msg = err instanceof Error ? err.message : `Failed to ${action} admin`;
        trackEvent("settings_error", { action: `${action}_admin`, error: msg });
        setError(msg);
      }
    } finally {
      setTogglingAdmin(null);
    }
  }, [tenantId, getAccessToken, addNotification, fetchAdmins]);

  const handleUpdatePermissions = useCallback(async (adminUpn: string, role: string, canManageBootstrapTokens: boolean) => {
    if (!tenantId) return;
    try {
      setTogglingAdmin(adminUpn);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(
        api.tenants.adminPermissions(tenantId, adminUpn),
        getAccessToken,
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ role, canManageBootstrapTokens }),
        },
      );

      if (!response.ok) {
        const errorData = await response.json();
        throw new Error(errorData.error || `Failed to update permissions: ${response.statusText}`);
      }

      trackEvent("admin_permissions_updated", { role });
      setSuccessMessage(`Permissions for ${adminUpn} updated successfully!`);
      await fetchAdmins();
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error("Error updating permissions:", err);
        const msg = err instanceof Error ? err.message : "Failed to update permissions";
        trackEvent("settings_error", { action: "update_permissions", error: msg });
        setError(msg);
      }
    } finally {
      setTogglingAdmin(null);
    }
  }, [tenantId, getAccessToken, addNotification, fetchAdmins]);

  // -----------------------------------------------------------------------
  // Bootstrap session handlers
  // -----------------------------------------------------------------------
  const createBootstrapSession = useCallback(async (validityHours: number, label: string): Promise<string | null> => {
    if (!tenantId) return null;
    try {
      const response = await authenticatedFetch(api.bootstrap.sessions(), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ tenantId, validityHours, label }),
      });
      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error((data as Record<string, string>).error || "Failed to create session");
      }
      const data = await response.json();
      trackEvent("bootstrap_session_created", { validityHours });
      await fetchBootstrapSessions();
      return data.bootstrapUrl || null;
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        const msg = err instanceof Error ? err.message : "Failed to create bootstrap session";
        trackEvent("settings_error", { action: "create_bootstrap", error: msg });
        setError(msg);
      }
      return null;
    }
  }, [tenantId, getAccessToken, addNotification, fetchBootstrapSessions]);

  const revokeBootstrapSession = useCallback(async (code: string) => {
    if (!tenantId) return;
    try {
      const response = await authenticatedFetch(
        api.bootstrap.session(code, tenantId),
        getAccessToken,
        { method: "DELETE" },
      );
      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error((data as Record<string, string>).error || "Failed to revoke session");
      }
      trackEvent("bootstrap_session_revoked");
      await fetchBootstrapSessions();
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        const msg = err instanceof Error ? err.message : "Failed to revoke bootstrap session";
        trackEvent("settings_error", { action: "revoke_bootstrap", error: msg });
        setError(msg);
      }
    }
  }, [tenantId, getAccessToken, addNotification, fetchBootstrapSessions]);

  // -----------------------------------------------------------------------
  // Offboard
  // -----------------------------------------------------------------------
  const handleOffboard = useCallback(async () => {
    if (!tenantId) return;
    try {
      setOffboarding(true);
      setOffboardError(null);

      const response = await authenticatedFetch(api.tenants.offboard(tenantId), getAccessToken, {
        method: 'DELETE',
      });

      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error(data?.error || `Offboard failed: ${response.statusText}`);
      }

      // Backend returns 202 (or 200 for idempotent re-clicks) with the History row pointer
      // and EarliestProcessingAt (cache-drain barrier deadline). Switch the UI into the
      // drain-barrier banner state; the banner's countdown will auto-logout once the
      // barrier elapses (by then the worker has started Phase 2 and the auth pipeline
      // returns 403 via the existing Disabled-flag gate).
      const body = await response.json().catch(() => ({}));
      trackEvent("tenant_offboarded");

      setOffboardingInProgress({
        status: body?.status ?? "Queued",
        historyRowKey: body?.historyRowKey ?? "",
        earliestProcessingAt: body?.earliestProcessingAt ?? null,
        message: body?.message ?? "Tenant offboarding queued.",
      });
      // Dismiss the confirmation dialog now that the banner has taken over.
      setShowOffboardDialog(false);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        setOffboardError(err instanceof Error ? err.message : 'Offboard failed');
      }
    } finally {
      setOffboarding(false);
    }
  }, [tenantId, getAccessToken, addNotification]);

  const handleDrainBarrierElapsed = useCallback(() => {
    // The cache-drain barrier has expired. The worker is starting Phase 2 right now and
    // all function-host instances have refreshed their TenantConfiguration cache to see
    // Disabled=true. Sign the user out — any further authenticated call will fail with
    // 403 anyway.
    logout();
  }, [logout]);

  // -----------------------------------------------------------------------
  // Self-service Pro trial (once per tenant — backend enforces via 409)
  // -----------------------------------------------------------------------
  const startTrial = useCallback(async (): Promise<boolean> => {
    if (!tenantId) return false;
    try {
      setStartingTrial(true);
      setError(null);

      const response = await authenticatedFetch(api.config.trial(tenantId), getAccessToken, {
        method: "POST",
      });

      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error(data.message || data.error || `Failed to start trial: ${response.statusText}`);
      }

      // Refetch the authoritative edition surface (server-resolved).
      const flagsResponse = await authenticatedFetch(api.config.featureFlags(tenantId), getAccessToken);
      if (flagsResponse.ok) {
        setEditionInfo(parseEditionInfo(await flagsResponse.json()));
      }
      setSuccessMessage("Pro trial started — all Pro features are now active for 30 days.");
      setTimeout(() => setSuccessMessage(null), 5000);
      trackEvent("ProTrialStarted", { tenantId });
      return true;
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        setError(err instanceof Error ? err.message : "Failed to start trial");
      }
      return false;
    } finally {
      setStartingTrial(false);
    }
  }, [tenantId, getAccessToken, addNotification]);

  // -----------------------------------------------------------------------
  // Provider value
  // -----------------------------------------------------------------------
  // Memoized (P6.3): the provider re-renders whenever ANY upstream context emits
  // (Auth account refresh, a notification anywhere in the app, tenant/theme) — without
  // the memo every such render minted a fresh value object and re-rendered all 16
  // settings sections for nothing. Own-state changes still fan out (the value honestly
  // depends on nearly every field); the memo only cuts the externally-triggered cascade.
  // The dependency array is enforced by react-hooks/exhaustive-deps (lint cap = 0).
  const value = useMemo<TenantConfigContextValue>(() => ({
      config, loading, canEditConfig, savingSection,
      error, setError, successMessage, setSuccessMessage,

      // Edition / trial
      editionInfo, startingTrial, startTrial,
      appHomingFunnelActive, homingFlipped,

      // Validation
      validateAutopilotDevice, setValidateAutopilotDevice,
      validateCorporateIdentifier, setValidateCorporateIdentifier,
      validateDeviceAssociation, setValidateDeviceAssociation,
      handleToggleDeviceAssociationValidation,
      validateCloudPcDevice, setValidateCloudPcDevice,
      handleToggleCloudPcValidation,
      saveValidationGate,
      autopilotConsentInProgress, beginDeviceValidationConsentFlow, detectExistingAccess,

      // Hardware whitelist
      manufacturerWhitelist, setManufacturerWhitelist,
      modelWhitelist, setModelWhitelist,
      webhookNotifyOnHardwareRejection: effectiveHwRejectionNotify,
      setWebhookNotifyOnHardwareRejection: setHwRejectionNotifyWriteThrough,
      handleSaveHardwareWhitelist, handleResetHardwareWhitelist,

      // Agent settings
      enablePerformanceCollector, setEnablePerformanceCollector,
      performanceCollectorInterval, setPerformanceCollectorInterval,
      helloWaitTimeoutSeconds, setHelloWaitTimeoutSeconds,
      selfDestructOnComplete, setSelfDestructOnComplete,
      keepLogFile, setKeepLogFile,
      rebootOnComplete, setRebootOnComplete,
      rebootDelaySeconds, setRebootDelaySeconds,
      contactEmail, setContactEmail,
      handleSaveContact, handleResetContact,
      enableGeoLocation, setEnableGeoLocation,
      enableTimezoneAutoSet, setEnableTimezoneAutoSet,
      enableDoGroupIdAutoSet, setEnableDoGroupIdAutoSet,
      enableImeMatchLog, setEnableImeMatchLog,
      enableGatherRuleDebugLog, setEnableGatherRuleDebugLog,
      logLevel, setLogLevel,
      showScriptOutput, setShowScriptOutput,
      showEnrollmentSummary, setShowEnrollmentSummary,
      enrollmentSummaryTimeoutSeconds, setEnrollmentSummaryTimeoutSeconds,
      enrollmentSummaryBrandingImageUrl, setEnrollmentSummaryBrandingImageUrl,
      enrollmentSummaryLaunchRetrySeconds, setEnrollmentSummaryLaunchRetrySeconds,
      enableRealmJoinWatcher, setEnableRealmJoinWatcher,
      keepAwakeDuringUserEsp, setKeepAwakeDuringUserEsp,
      handleSaveAgentSettings, handleResetAgentSettings,

      // Agent analyzers
      enableLocalAdminAnalyzer, setEnableLocalAdminAnalyzer,
      localAdminAllowedAccounts, setLocalAdminAllowedAccounts,
      newAllowedAccount, setNewAllowedAccount,
      enableSoftwareInventoryAnalyzer, setEnableSoftwareInventoryAnalyzer,
      enableIntegrityBypassAnalyzer, setEnableIntegrityBypassAnalyzer,
      enableConsoleBypassDetection, setEnableConsoleBypassDetection,
      handleSaveAgentAnalyzers, handleResetAgentAnalyzers,

      // Unrestricted mode
      unrestrictedMode, setUnrestrictedMode,
      handleSaveUnrestrictedMode,

      // Notifications
      notificationChannels, setNotificationChannels,
      testingChannelId, testChannelResult,
      handleTestChannel, handleSaveNotifications, handleResetNotifications,

      // SLA Targets
      slaTargetSuccessRate, setSlaTargetSuccessRate,
      slaTargetMaxDurationMinutes, setSlaTargetMaxDurationMinutes,
      slaTargetAppInstallSuccessRate, setSlaTargetAppInstallSuccessRate,
      slaNotifyOnSuccessRateBreach, setSlaNotifyOnSuccessRateBreach,
      slaSuccessRateNotifyThreshold, setSlaSuccessRateNotifyThreshold,
      slaNotifyOnDurationBreach, setSlaNotifyOnDurationBreach,
      slaNotifyOnAppInstallBreach, setSlaNotifyOnAppInstallBreach,
      slaNotifyOnConsecutiveFailures, setSlaNotifyOnConsecutiveFailures,
      slaConsecutiveFailureThreshold, setSlaConsecutiveFailureThreshold,
      handleSaveSlaTargets, handleResetSlaTargets,

      // Diagnostics
      diagnosticsBlobSasUrl, setDiagnosticsBlobSasUrl,
      diagnosticsUploadMode, setDiagnosticsUploadMode,
      diagnosticsUploadDestination, setDiagnosticsUploadDestination,
      tenantDiagPaths, setTenantDiagPaths,
      newDiagPath, setNewDiagPath,
      newDiagDesc, setNewDiagDesc,
      handleSaveDiagnostics, handleResetDiagnostics,

      // Admin management
      admins, loadingAdmins,
      newAdminEmail, setNewAdminEmail,
      newMemberRole, setNewMemberRole,
      addingAdmin, removingAdmin, togglingAdmin,
      adminSearchQuery, setAdminSearchQuery,
      currentAdminPage, setCurrentAdminPage,
      handleAddAdmin, handleRemoveAdmin, handleToggleTenantAdmin, handleUpdatePermissions,

      // Bootstrap sessions
      bootstrapSessions, bootstrapLoading,
      fetchBootstrapSessions, createBootstrapSession, revokeBootstrapSession,

      // Data management
      dataRetentionDays, setDataRetentionDays,
      sessionTimeoutHours, setSessionTimeoutHours,
      handleSaveDataManagement, handleResetDataManagement,

      // Offboarding
      showOffboardDialog, setShowOffboardDialog,
      offboardConfirmText, setOffboardConfirmText,
      offboarding, offboardError, setOffboardError,
      handleOffboard,
      offboardingInProgress, handleDrainBarrierElapsed,

      // Auth
      user, getAccessToken,
  }), [
    config, loading, canEditConfig, savingSection, error, successMessage,
    editionInfo, startingTrial, startTrial, appHomingFunnelActive, homingFlipped,
    validateAutopilotDevice, validateCorporateIdentifier, validateDeviceAssociation,
    handleToggleDeviceAssociationValidation, validateCloudPcDevice, handleToggleCloudPcValidation,
    saveValidationGate, autopilotConsentInProgress, beginDeviceValidationConsentFlow, detectExistingAccess,
    manufacturerWhitelist, modelWhitelist, effectiveHwRejectionNotify, setHwRejectionNotifyWriteThrough,
    handleSaveHardwareWhitelist, handleResetHardwareWhitelist,
    enablePerformanceCollector, performanceCollectorInterval, helloWaitTimeoutSeconds,
    selfDestructOnComplete, keepLogFile, rebootOnComplete, rebootDelaySeconds,
    contactEmail, handleSaveContact, handleResetContact,
    enableGeoLocation, enableTimezoneAutoSet, enableDoGroupIdAutoSet, enableImeMatchLog, enableGatherRuleDebugLog,
    logLevel, showScriptOutput, showEnrollmentSummary, enrollmentSummaryTimeoutSeconds,
    enrollmentSummaryBrandingImageUrl, enrollmentSummaryLaunchRetrySeconds,
    enableRealmJoinWatcher, keepAwakeDuringUserEsp,
    handleSaveAgentSettings, handleResetAgentSettings,
    enableLocalAdminAnalyzer, localAdminAllowedAccounts, newAllowedAccount,
    enableSoftwareInventoryAnalyzer, enableIntegrityBypassAnalyzer, enableConsoleBypassDetection,
    handleSaveAgentAnalyzers, handleResetAgentAnalyzers,
    unrestrictedMode, handleSaveUnrestrictedMode,
    notificationChannels, testingChannelId, testChannelResult,
    handleTestChannel, handleSaveNotifications, handleResetNotifications,
    slaTargetSuccessRate, slaTargetMaxDurationMinutes, slaTargetAppInstallSuccessRate,
    slaNotifyOnSuccessRateBreach, slaSuccessRateNotifyThreshold, slaNotifyOnDurationBreach,
    slaNotifyOnAppInstallBreach, slaNotifyOnConsecutiveFailures, slaConsecutiveFailureThreshold,
    handleSaveSlaTargets, handleResetSlaTargets,
    diagnosticsBlobSasUrl, diagnosticsUploadMode, diagnosticsUploadDestination,
    tenantDiagPaths, newDiagPath, newDiagDesc,
    handleSaveDiagnostics, handleResetDiagnostics,
    admins, loadingAdmins, newAdminEmail, newMemberRole, addingAdmin, removingAdmin, togglingAdmin,
    adminSearchQuery, currentAdminPage,
    handleAddAdmin, handleRemoveAdmin, handleToggleTenantAdmin, handleUpdatePermissions,
    bootstrapSessions, bootstrapLoading, fetchBootstrapSessions, createBootstrapSession, revokeBootstrapSession,
    dataRetentionDays, sessionTimeoutHours, handleSaveDataManagement, handleResetDataManagement,
    showOffboardDialog, offboardConfirmText, offboarding, offboardError, handleOffboard,
    offboardingInProgress, handleDrainBarrierElapsed,
    user, getAccessToken,
  ]);

  return (
    <TenantConfigContext.Provider value={value}>
      {children}
    </TenantConfigContext.Provider>
  );
}
