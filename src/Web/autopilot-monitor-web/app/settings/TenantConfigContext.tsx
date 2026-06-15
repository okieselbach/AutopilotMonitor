"use client";

import { createContext, useCallback, useContext, useEffect, useState, type SetStateAction } from "react";
import { useRouter } from "next/navigation";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { useNotifications } from "../../contexts/NotificationContext";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { trackEvent } from "@/lib/appInsights";
import { parseSasExpiry } from "./components/DiagnosticsSection";
import { TenantConfiguration, TenantAdmin, DiagnosticsLogPath } from "./types";
import { type BootstrapSessionItem } from "./components/BootstrapSessionsSection";

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
  savingSection: string | null;
  error: string | null;
  setError: (e: string | null) => void;
  successMessage: string | null;
  setSuccessMessage: (m: string | null) => void;

  // Validation
  validateAutopilotDevice: boolean;
  setValidateAutopilotDevice: (v: boolean) => void;
  validateCorporateIdentifier: boolean;
  setValidateCorporateIdentifier: (v: boolean) => void;
  validateDeviceAssociation: boolean;
  setValidateDeviceAssociation: (v: boolean) => void;
  /** Toggle + persist DevPrep Device Association validation in one shot (no consent flow needed). */
  handleToggleDeviceAssociationValidation: (newValue: boolean) => Promise<void>;
  autopilotConsentInProgress: boolean;
  beginDeviceValidationConsentFlow: (trigger: "autopilot" | "corporate" | "device-preparation") => Promise<void>;

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
  enableGeoLocation: boolean;
  setEnableGeoLocation: (v: boolean) => void;
  enableTimezoneAutoSet: boolean;
  setEnableTimezoneAutoSet: (v: boolean) => void;
  enableImeMatchLog: boolean;
  setEnableImeMatchLog: (v: boolean) => void;
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
  enableRealmJoinWatcher: boolean;
  setEnableRealmJoinWatcher: (v: boolean) => void;
  handleSaveAgentAnalyzers: () => void;
  handleResetAgentAnalyzers: () => void;

  // Unrestricted mode
  unrestrictedMode: boolean;
  setUnrestrictedMode: (v: boolean) => void;
  handleSaveUnrestrictedMode: () => void;

  // Notifications / Webhook
  webhookProviderType: number;
  setWebhookProviderType: (v: number) => void;
  webhookUrl: string;
  setWebhookUrl: (v: string) => void;
  webhookNotifyOnSuccess: boolean;
  setWebhookNotifyOnSuccess: (v: boolean) => void;
  webhookNotifyOnFailure: boolean;
  setWebhookNotifyOnFailure: (v: boolean) => void;
  webhookNotifyOnStart: boolean;
  setWebhookNotifyOnStart: (v: boolean) => void;
  testingWebhook: boolean;
  testWebhookResult: { success: boolean; message: string } | null;
  handleTestWebhook: () => Promise<void>;
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
  globalDiagPaths: DiagnosticsLogPath[];
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
  const [enableGeoLocation, setEnableGeoLocation] = useState(true);
  const [enableTimezoneAutoSet, setEnableTimezoneAutoSet] = useState(false);
  const [enableImeMatchLog, setEnableImeMatchLog] = useState(false);
  const [logLevel, setLogLevel] = useState("Info");
  const [showScriptOutput, setShowScriptOutput] = useState(true);
  const [showEnrollmentSummary, setShowEnrollmentSummary] = useState(false);
  const [enrollmentSummaryTimeoutSeconds, setEnrollmentSummaryTimeoutSeconds] = useState(60);
  const [enrollmentSummaryBrandingImageUrl, setEnrollmentSummaryBrandingImageUrl] = useState("");
  const [enrollmentSummaryLaunchRetrySeconds, setEnrollmentSummaryLaunchRetrySeconds] = useState(120);

  // Webhook notifications
  const [webhookProviderType, setWebhookProviderType] = useState(0);
  const [webhookUrl, setWebhookUrl] = useState("");
  const [webhookNotifyOnSuccess, setWebhookNotifyOnSuccess] = useState(true);
  const [webhookNotifyOnFailure, setWebhookNotifyOnFailure] = useState(true);
  const [webhookNotifyOnStart, setWebhookNotifyOnStart] = useState(false);
  const [testingWebhook, setTestingWebhook] = useState(false);
  const [testWebhookResult, setTestWebhookResult] = useState<{ success: boolean; message: string } | null>(null);

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
  const [globalDiagPaths, setGlobalDiagPaths] = useState<DiagnosticsLogPath[]>([]);
  const [newDiagPath, setNewDiagPath] = useState("");
  const [newDiagDesc, setNewDiagDesc] = useState("");

  // Agent analyzers
  const [enableLocalAdminAnalyzer, setEnableLocalAdminAnalyzer] = useState(true);
  const [localAdminAllowedAccounts, setLocalAdminAllowedAccounts] = useState<string[]>([]);
  const [newAllowedAccount, setNewAllowedAccount] = useState("");
  const [enableSoftwareInventoryAnalyzer, setEnableSoftwareInventoryAnalyzer] = useState(false);
  const [enableIntegrityBypassAnalyzer, setEnableIntegrityBypassAnalyzer] = useState(true);
  const [enableRealmJoinWatcher, setEnableRealmJoinWatcher] = useState(false);

  // Unrestricted mode
  const [unrestrictedMode, setUnrestrictedMode] = useState(false);

  // -----------------------------------------------------------------------
  // Fetch configuration
  // -----------------------------------------------------------------------
  useEffect(() => {
    if (!tenantId) return;

    // Operators (non-admin) only need feature flags, not the full config
    const isAdminOrGA = user?.isTenantAdmin || user?.isGlobalAdmin;

    const fetchConfiguration = async () => {
      try {
        setLoading(true);
        setError(null);

        if (!isAdminOrGA) {
          // Operator with canManageBootstrapTokens — load only feature flags
          const response = await authenticatedFetch(api.config.featureFlags(tenantId), getAccessToken);
          if (!response.ok) {
            throw new Error(`Failed to load feature flags: ${response.statusText}`);
          }
          const flags = await response.json();
          // Create a minimal config object with just the feature flag
          setConfig({ bootstrapTokenEnabled: flags.bootstrapTokenEnabled } as TenantConfiguration);
          return;
        }

        const response = await authenticatedFetch(api.config.tenant(tenantId), getAccessToken);

        if (!response.ok) {
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
        setDataRetentionDays(data.dataRetentionDays ?? 90);
        setSessionTimeoutHours(data.sessionTimeoutHours ?? 5);
        setEnablePerformanceCollector(data.enablePerformanceCollector ?? true);
        setPerformanceCollectorInterval(data.performanceCollectorIntervalSeconds ?? 30);
        setHelloWaitTimeoutSeconds(data.helloWaitTimeoutSeconds ?? 30);
        setSelfDestructOnComplete(data.selfDestructOnComplete ?? true);
        setKeepLogFile(data.keepLogFile ?? false);
        setRebootOnComplete(data.rebootOnComplete ?? false);
        setRebootDelaySeconds(data.rebootDelaySeconds ?? 10);
        setEnableGeoLocation(data.enableGeoLocation ?? true);
        setEnableTimezoneAutoSet(data.enableTimezoneAutoSet ?? false);
        setEnableImeMatchLog(data.enableImeMatchLog ?? false);
        setLogLevel(data.logLevel ?? "Info");
        setShowScriptOutput(data.showScriptOutput ?? true);
        setShowEnrollmentSummary(data.showEnrollmentSummary ?? false);
        setEnrollmentSummaryTimeoutSeconds(data.enrollmentSummaryTimeoutSeconds ?? 60);
        setEnrollmentSummaryBrandingImageUrl(data.enrollmentSummaryBrandingImageUrl ?? "");
        setEnrollmentSummaryLaunchRetrySeconds(data.enrollmentSummaryLaunchRetrySeconds ?? 120);
        // Webhook notifications: auto-migrate from legacy fields
        if (data.webhookUrl && data.webhookProviderType) {
          setWebhookProviderType(data.webhookProviderType);
          setWebhookUrl(data.webhookUrl);
          setWebhookNotifyOnSuccess(data.webhookNotifyOnSuccess ?? true);
          setWebhookNotifyOnFailure(data.webhookNotifyOnFailure ?? true);
          setWebhookNotifyOnStart(data.webhookNotifyOnStart ?? false);
        } else if (data.teamsWebhookUrl) {
          setWebhookProviderType(1); // TeamsLegacyConnector
          setWebhookUrl(data.teamsWebhookUrl);
          setWebhookNotifyOnSuccess(data.teamsNotifyOnSuccess ?? true);
          setWebhookNotifyOnFailure(data.teamsNotifyOnFailure ?? true);
          setWebhookNotifyOnStart(data.teamsNotifyOnStart ?? false);
        } else {
          setWebhookProviderType(0);
          setWebhookUrl("");
          setWebhookNotifyOnSuccess(true);
          setWebhookNotifyOnFailure(true);
          setWebhookNotifyOnStart(false);
        }
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
        setEnableRealmJoinWatcher(data.enableRealmJoinWatcher ?? false);
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
  }, [tenantId]);

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
    fetchAdmins();
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
    if (!tenantId || !config?.bootstrapTokenEnabled) return;
    fetchBootstrapSessions();
  }, [tenantId, config?.bootstrapTokenEnabled, fetchBootstrapSessions]);

  // -----------------------------------------------------------------------
  // Fetch global diagnostics paths (global-admin only)
  // -----------------------------------------------------------------------
  useEffect(() => {
    if (!user?.isGlobalAdmin) return;
    const fetchGlobalDiagPaths = async () => {
      try {
        const res = await authenticatedFetch(api.globalConfig.get(), getAccessToken);
        if (!res.ok) return;
        const data = await res.json();
        if (data.diagnosticsGlobalLogPathsJson) {
          setGlobalDiagPaths(JSON.parse(data.diagnosticsGlobalLogPathsJson));
        }
      } catch {
        // Non-fatal
      }
    };
    fetchGlobalDiagPaths();
  }, [user?.isGlobalAdmin, getAccessToken]);

  // -----------------------------------------------------------------------
  // Save configuration (shared by all sections)
  // -----------------------------------------------------------------------
  const saveConfiguration = useCallback(async (sectionName: string, overrides?: { validateAutopilotDevice?: boolean; validateCorporateIdentifier?: boolean; validateDeviceAssociation?: boolean }) => {
    if (!tenantId || !config) return;

    try {
      setSavingSection(sectionName);
      setError(null);
      setSuccessMessage(null);

      const autopilotDeviceValidationValue = overrides?.validateAutopilotDevice ?? validateAutopilotDevice;
      const corporateIdentifierValidationValue = overrides?.validateCorporateIdentifier ?? validateCorporateIdentifier;
      const deviceAssociationValidationValue = overrides?.validateDeviceAssociation ?? validateDeviceAssociation;

      const updatedConfig: TenantConfiguration = {
        ...config,
        manufacturerWhitelist,
        modelWhitelist,
        webhookNotifyOnHardwareRejection,
        validateAutopilotDevice: autopilotDeviceValidationValue,
        validateCorporateIdentifier: corporateIdentifierValidationValue,
        validateDeviceAssociation: deviceAssociationValidationValue,
        dataRetentionDays,
        sessionTimeoutHours,
        enablePerformanceCollector,
        performanceCollectorIntervalSeconds: performanceCollectorInterval,
        helloWaitTimeoutSeconds,
        selfDestructOnComplete,
        keepLogFile,
        rebootOnComplete,
        rebootDelaySeconds,
        enableGeoLocation,
        enableTimezoneAutoSet,
        enableImeMatchLog,
        logLevel,
        showScriptOutput,
        showEnrollmentSummary,
        enrollmentSummaryTimeoutSeconds,
        enrollmentSummaryBrandingImageUrl: enrollmentSummaryBrandingImageUrl || undefined,
        enrollmentSummaryLaunchRetrySeconds,
        // New webhook fields
        webhookProviderType,
        webhookUrl: webhookUrl || undefined,
        webhookNotifyOnSuccess,
        webhookNotifyOnFailure,
        webhookNotifyOnStart,
        // Legacy compat: mirror to old fields during transition
        teamsWebhookUrl: webhookProviderType === 1 ? (webhookUrl || undefined) : undefined,
        teamsNotifyOnSuccess: webhookNotifyOnSuccess,
        teamsNotifyOnFailure: webhookNotifyOnFailure,
        teamsNotifyOnStart: webhookNotifyOnStart,
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
        enableRealmJoinWatcher,
        unrestrictedMode,
      };

      const response = await authenticatedFetch(api.config.tenant(tenantId), getAccessToken, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(updatedConfig),
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || errorData.error || `Failed to save configuration: ${response.statusText}`);
      }

      const result = await response.json();
      setConfig(result.config);
      // Sync form state variables with the server response
      setValidateAutopilotDevice(result.config.validateAutopilotDevice);
      setValidateCorporateIdentifier(result.config.validateCorporateIdentifier ?? false);
      setValidateDeviceAssociation(result.config.validateDeviceAssociation ?? false);
      setUnrestrictedMode(result.config.unrestrictedMode ?? false);
      trackEvent("settings_saved", { section: sectionName });
      setSuccessMessage("Configuration saved successfully!");
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        const msg = err instanceof Error ? err.message : "Failed to save configuration";
        trackEvent("settings_error", { action: "save", section: sectionName, error: msg });
        setError(msg);
      }
    } finally {
      setSavingSection(null);
    }
  }, [
    tenantId, config, getAccessToken, addNotification,
    manufacturerWhitelist, modelWhitelist, validateAutopilotDevice, validateCorporateIdentifier, validateDeviceAssociation,
    dataRetentionDays, sessionTimeoutHours, enablePerformanceCollector, performanceCollectorInterval,
    helloWaitTimeoutSeconds, selfDestructOnComplete, keepLogFile, rebootOnComplete, rebootDelaySeconds,
    enableGeoLocation, enableTimezoneAutoSet, enableImeMatchLog, logLevel, showScriptOutput, showEnrollmentSummary,
    enrollmentSummaryTimeoutSeconds, enrollmentSummaryBrandingImageUrl, enrollmentSummaryLaunchRetrySeconds,
    webhookProviderType, webhookUrl, webhookNotifyOnSuccess, webhookNotifyOnFailure, webhookNotifyOnStart,
    slaTargetSuccessRate, slaTargetMaxDurationMinutes, slaTargetAppInstallSuccessRate,
    slaNotifyOnSuccessRateBreach, slaSuccessRateNotifyThreshold, slaNotifyOnDurationBreach,
    slaNotifyOnAppInstallBreach, slaNotifyOnConsecutiveFailures, slaConsecutiveFailureThreshold,
    diagnosticsBlobSasUrl, diagnosticsUploadMode, diagnosticsUploadDestination, tenantDiagPaths,
    enableLocalAdminAnalyzer, localAdminAllowedAccounts, enableSoftwareInventoryAnalyzer,
    enableIntegrityBypassAnalyzer, enableRealmJoinWatcher, unrestrictedMode,
  ]);

  // -----------------------------------------------------------------------
  // Consent flow
  // -----------------------------------------------------------------------
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

      sessionStorage.setItem("deviceValidationConsentPending", "true");
      sessionStorage.setItem("consentTrigger", trigger);
      window.location.href = data.consentUrl;
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
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
        const errorText = consentErrorDescription
          ? `${consentError}: ${decodeURIComponent(consentErrorDescription)}`
          : consentError;
        setError(`Admin consent failed: ${errorText}`);
        setAutopilotConsentInProgress(false);

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

        if (trigger === "corporate") {
          await saveConfiguration("autopilotValidation", { validateCorporateIdentifier: true });
          setSuccessMessage("Corporate Identifier Validation enabled. Backend agent endpoints are now unlocked for this tenant.");
        } else {
          await saveConfiguration("autopilotValidation", { validateAutopilotDevice: true });
          setSuccessMessage("Autopilot Device Validation enabled. Backend agent endpoints are now unlocked for this tenant.");
        }
        router.replace("/settings/tenant/autopilot");
      } catch (err) {
        if (err instanceof TokenExpiredError) {
          addNotification('error', 'Session Expired', err.message, 'session-expired-error');
        } else {
          setError(err instanceof Error ? err.message : "Failed to verify consent");
        }
      } finally {
        setAutopilotConsentInProgress(false);
      }
    };

    handleConsentCallback();
  }, [tenantId, config, router, getAccessToken, addNotification, saveConfiguration]);

  // -----------------------------------------------------------------------
  // Test webhook
  // -----------------------------------------------------------------------
  const handleTestWebhook = useCallback(async () => {
    if (!tenantId) return;
    setTestingWebhook(true);
    setTestWebhookResult(null);
    try {
      const response = await authenticatedFetch(api.config.testNotification(tenantId), getAccessToken, {
        method: "POST",
      });
      const data = await response.json();
      setTestWebhookResult({ success: data.success, message: data.message });
    } catch (err) {
      setTestWebhookResult({ success: false, message: err instanceof Error ? err.message : "Failed to send test notification." });
    } finally {
      setTestingWebhook(false);
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
  }, [config]);

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
    setEnableGeoLocation(config.enableGeoLocation ?? true);
    setEnableTimezoneAutoSet(config.enableTimezoneAutoSet ?? false);
    setEnableImeMatchLog(config.enableImeMatchLog ?? false);
    setLogLevel(config.logLevel ?? "Info");
    setShowScriptOutput(config.showScriptOutput ?? true);
    setShowEnrollmentSummary(config.showEnrollmentSummary ?? false);
    setEnrollmentSummaryTimeoutSeconds(config.enrollmentSummaryTimeoutSeconds ?? 60);
    setEnrollmentSummaryBrandingImageUrl(config.enrollmentSummaryBrandingImageUrl ?? "");
    setEnrollmentSummaryLaunchRetrySeconds(config.enrollmentSummaryLaunchRetrySeconds ?? 120);
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
    setEnableRealmJoinWatcher(config.enableRealmJoinWatcher ?? false);
  }, [config]);

  const handleSaveNotifications = useCallback(() => saveConfiguration("notifications"), [saveConfiguration]);
  const handleResetNotifications = useCallback(() => {
    if (!config) return;
    if (config.webhookUrl && config.webhookProviderType) {
      setWebhookProviderType(config.webhookProviderType);
      setWebhookUrl(config.webhookUrl);
      setWebhookNotifyOnSuccess(config.webhookNotifyOnSuccess ?? true);
      setWebhookNotifyOnFailure(config.webhookNotifyOnFailure ?? true);
      setWebhookNotifyOnStart(config.webhookNotifyOnStart ?? false);
    } else if (config.teamsWebhookUrl) {
      setWebhookProviderType(1);
      setWebhookUrl(config.teamsWebhookUrl);
      setWebhookNotifyOnSuccess(config.teamsNotifyOnSuccess ?? true);
      setWebhookNotifyOnFailure(config.teamsNotifyOnFailure ?? true);
      setWebhookNotifyOnStart(config.teamsNotifyOnStart ?? false);
    } else {
      setWebhookProviderType(0);
      setWebhookUrl("");
      setWebhookNotifyOnSuccess(true);
      setWebhookNotifyOnFailure(true);
      setWebhookNotifyOnStart(false);
    }
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

  const handleSaveUnrestrictedMode = useCallback(() => saveConfiguration("unrestrictedMode"), [saveConfiguration]);

  /**
   * Toggle the DevPrep "Device association" shadow validation. No consent flow needed —
   * the Graph permission is already covered by the existing Autopilot/Corporate validators
   * and the result is observational (does not gate enrollment in Phase A).
   */
  const handleToggleDeviceAssociationValidation = useCallback(async (newValue: boolean) => {
    setValidateDeviceAssociation(newValue);
    await saveConfiguration("autopilotValidation", { validateDeviceAssociation: newValue });
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
  // Provider value
  // -----------------------------------------------------------------------
  return (
    <TenantConfigContext.Provider value={{
      config, loading, savingSection,
      error, setError, successMessage, setSuccessMessage,

      // Validation
      validateAutopilotDevice, setValidateAutopilotDevice,
      validateCorporateIdentifier, setValidateCorporateIdentifier,
      validateDeviceAssociation, setValidateDeviceAssociation,
      handleToggleDeviceAssociationValidation,
      autopilotConsentInProgress, beginDeviceValidationConsentFlow,

      // Hardware whitelist
      manufacturerWhitelist, setManufacturerWhitelist,
      modelWhitelist, setModelWhitelist,
      webhookNotifyOnHardwareRejection, setWebhookNotifyOnHardwareRejection,
      handleSaveHardwareWhitelist, handleResetHardwareWhitelist,

      // Agent settings
      enablePerformanceCollector, setEnablePerformanceCollector,
      performanceCollectorInterval, setPerformanceCollectorInterval,
      helloWaitTimeoutSeconds, setHelloWaitTimeoutSeconds,
      selfDestructOnComplete, setSelfDestructOnComplete,
      keepLogFile, setKeepLogFile,
      rebootOnComplete, setRebootOnComplete,
      rebootDelaySeconds, setRebootDelaySeconds,
      enableGeoLocation, setEnableGeoLocation,
      enableTimezoneAutoSet, setEnableTimezoneAutoSet,
      enableImeMatchLog, setEnableImeMatchLog,
      logLevel, setLogLevel,
      showScriptOutput, setShowScriptOutput,
      showEnrollmentSummary, setShowEnrollmentSummary,
      enrollmentSummaryTimeoutSeconds, setEnrollmentSummaryTimeoutSeconds,
      enrollmentSummaryBrandingImageUrl, setEnrollmentSummaryBrandingImageUrl,
      enrollmentSummaryLaunchRetrySeconds, setEnrollmentSummaryLaunchRetrySeconds,
      handleSaveAgentSettings, handleResetAgentSettings,

      // Agent analyzers
      enableLocalAdminAnalyzer, setEnableLocalAdminAnalyzer,
      localAdminAllowedAccounts, setLocalAdminAllowedAccounts,
      newAllowedAccount, setNewAllowedAccount,
      enableSoftwareInventoryAnalyzer, setEnableSoftwareInventoryAnalyzer,
      enableIntegrityBypassAnalyzer, setEnableIntegrityBypassAnalyzer,
      enableRealmJoinWatcher, setEnableRealmJoinWatcher,
      handleSaveAgentAnalyzers, handleResetAgentAnalyzers,

      // Unrestricted mode
      unrestrictedMode, setUnrestrictedMode,
      handleSaveUnrestrictedMode,

      // Notifications
      webhookProviderType, setWebhookProviderType,
      webhookUrl, setWebhookUrl,
      webhookNotifyOnSuccess, setWebhookNotifyOnSuccess,
      webhookNotifyOnFailure, setWebhookNotifyOnFailure,
      webhookNotifyOnStart, setWebhookNotifyOnStart,
      testingWebhook, testWebhookResult,
      handleTestWebhook, handleSaveNotifications, handleResetNotifications,

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
      globalDiagPaths,
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
    }}>
      {children}
    </TenantConfigContext.Provider>
  );
}
