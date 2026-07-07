"use client";

import { createContext, useCallback, useContext, useEffect, useState } from "react";
import { useAuth } from "../../contexts/AuthContext";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import type { AdminConfiguration, OpsAlertRule } from "@/types/adminConfig";

// Re-export so existing `import { AdminConfiguration } from "../AdminConfigContext"` consumers keep working
export type { AdminConfiguration, OpsAlertRule };

interface AdminConfigContextValue {
  // Admin config
  adminConfig: AdminConfiguration | null;
  setAdminConfig: React.Dispatch<React.SetStateAction<AdminConfiguration | null>>;
  loadingConfig: boolean;
  savingConfig: boolean;
  setSavingConfig: React.Dispatch<React.SetStateAction<boolean>>;

  // Config field state
  globalRateLimit: number;
  setGlobalRateLimit: (value: number) => void;
  userRateLimit: number;
  setUserRateLimit: (value: number) => void;
  globalAdminRateLimit: number;
  setGlobalAdminRateLimit: (value: number) => void;
  platformStatsBlobSasUrl: string;
  setPlatformStatsBlobSasUrl: (value: string) => void;
  collectorIdleTimeoutMinutes: number;
  setCollectorIdleTimeoutMinutes: (value: number) => void;
  desktopDetectorNoCandidateTimeoutMinutes: number;
  setDesktopDetectorNoCandidateTimeoutMinutes: (value: number) => void;
  maxSessionWindowHours: number;
  setMaxSessionWindowHours: (value: number) => void;
  maintenanceBlockDurationHours: number;
  setMaintenanceBlockDurationHours: (value: number) => void;
  opsEventRetentionDays: number;
  setOpsEventRetentionDays: (value: number) => void;
  slaNotificationCooldownHours: number;
  setSlaNotificationCooldownHours: (value: number) => void;
  allowAgentDowngrade: boolean;
  setAllowAgentDowngrade: (value: boolean) => void;
  modernDeploymentHarmlessEventIds: string;
  setModernDeploymentHarmlessEventIds: (value: string) => void;
  enableIndexDualWrite: boolean;
  setEnableIndexDualWrite: (value: boolean) => void;
  sessionDeletionKillSwitch: boolean;
  setSessionDeletionKillSwitch: (value: boolean) => void;

  // Diagnostics log paths
  globalDiagPaths: DiagnosticsLogPath[];
  setGlobalDiagPaths: React.Dispatch<React.SetStateAction<DiagnosticsLogPath[]>>;
  savingDiagPaths: boolean;

  // Ops Alert settings
  opsAlertRules: OpsAlertRule[];
  setOpsAlertRules: React.Dispatch<React.SetStateAction<OpsAlertRule[]>>;
  opsAlertTelegramEnabled: boolean;
  setOpsAlertTelegramEnabled: (value: boolean) => void;
  opsAlertTelegramChatId: string;
  setOpsAlertTelegramChatId: (value: string) => void;
  opsAlertTeamsEnabled: boolean;
  setOpsAlertTeamsEnabled: (value: boolean) => void;
  opsAlertTeamsWebhookUrl: string;
  setOpsAlertTeamsWebhookUrl: (value: string) => void;
  opsAlertSlackEnabled: boolean;
  setOpsAlertSlackEnabled: (value: boolean) => void;
  opsAlertSlackWebhookUrl: string;
  setOpsAlertSlackWebhookUrl: (value: string) => void;
  excessiveEventCountThreshold: number;
  excessiveEventAutoActionMode: "Off" | "Block" | "Kill";
  excessiveEventAutoActionThreshold: number;
  excessiveEventAutoActionDurationHours: number;
  savingOpsAlerts: boolean;
  handleSaveOpsAlertConfig: (
    rules: OpsAlertRule[],
    telegramEnabled: boolean, telegramChatId: string,
    teamsEnabled: boolean, teamsWebhookUrl: string,
    slackEnabled: boolean, slackWebhookUrl: string,
    excessiveEventCountThreshold: number,
    excessiveEventAutoActionMode: "Off" | "Block" | "Kill",
    excessiveEventAutoActionThreshold: number,
    excessiveEventAutoActionDurationHours: number,
  ) => Promise<void>;

  // Tenants
  tenants: TenantConfiguration[];
  setTenants: React.Dispatch<React.SetStateAction<TenantConfiguration[]>>;
  loadingTenants: boolean;
  fetchTenants: () => void;
  previewApproved: Set<string>;
  setPreviewApproved: React.Dispatch<React.SetStateAction<Set<string>>>;

  // Notifications
  error: string | null;
  setError: (error: string | null) => void;
  successMessage: string | null;
  setSuccessMessage: (message: string | null) => void;

  // Auth
  getAccessToken: () => Promise<string | null>;

  // Actions
  handleSaveAdminConfig: () => Promise<void>;
  handleResetAdminConfig: () => void;
  handleSaveDiagPaths: (paths: DiagnosticsLogPath[]) => Promise<void>;
}

import { type DiagnosticsLogPath } from "./components/DiagnosticsLogPathsSection";
import { type TenantConfiguration } from "./components/TenantManagementSection";

import { parseHarmlessEventIdsJson, serializeHarmlessEventIds } from "./harmlessEventIds";

const AdminConfigContext = createContext<AdminConfigContextValue | null>(null);

export function useAdminConfig() {
  const ctx = useContext(AdminConfigContext);
  if (!ctx) throw new Error("useAdminConfig must be used within AdminConfigProvider");
  return ctx;
}

export function AdminConfigProvider({ children }: { children: React.ReactNode }) {
  const { getAccessToken, user } = useAuth();

  // Admin Configuration state
  const [adminConfig, setAdminConfig] = useState<AdminConfiguration | null>(null);
  const [loadingConfig, setLoadingConfig] = useState(false);
  const [savingConfig, setSavingConfig] = useState(false);
  const [globalRateLimit, setGlobalRateLimit] = useState(100);
  const [userRateLimit, setUserRateLimit] = useState(120);
  const [globalAdminRateLimit, setGlobalAdminRateLimit] = useState(600);
  const [platformStatsBlobSasUrl, setPlatformStatsBlobSasUrl] = useState("");
  const [collectorIdleTimeoutMinutes, setCollectorIdleTimeoutMinutes] = useState(15);
  const [desktopDetectorNoCandidateTimeoutMinutes, setDesktopDetectorNoCandidateTimeoutMinutes] = useState(10);
  const [maxSessionWindowHours, setMaxSessionWindowHours] = useState(24);
  const [maintenanceBlockDurationHours, setMaintenanceBlockDurationHours] = useState(12);
  const [opsEventRetentionDays, setOpsEventRetentionDays] = useState(90);
  const [slaNotificationCooldownHours, setSlaNotificationCooldownHours] = useState(24);
  const [allowAgentDowngrade, setAllowAgentDowngrade] = useState(false);
  const [modernDeploymentHarmlessEventIds, setModernDeploymentHarmlessEventIds] = useState("100, 1005, 1010");
  const [enableIndexDualWrite, setEnableIndexDualWrite] = useState(false);
  const [sessionDeletionKillSwitch, setSessionDeletionKillSwitch] = useState(false);

  // Diagnostics Log Paths state
  const [globalDiagPaths, setGlobalDiagPaths] = useState<DiagnosticsLogPath[]>([]);
  const [savingDiagPaths, setSavingDiagPaths] = useState(false);

  // Ops Alert state
  const [opsAlertRules, setOpsAlertRules] = useState<OpsAlertRule[]>([]);
  const [opsAlertTelegramEnabled, setOpsAlertTelegramEnabled] = useState(false);
  const [opsAlertTelegramChatId, setOpsAlertTelegramChatId] = useState("");
  const [opsAlertTeamsEnabled, setOpsAlertTeamsEnabled] = useState(false);
  const [opsAlertTeamsWebhookUrl, setOpsAlertTeamsWebhookUrl] = useState("");
  const [opsAlertSlackEnabled, setOpsAlertSlackEnabled] = useState(false);
  const [opsAlertSlackWebhookUrl, setOpsAlertSlackWebhookUrl] = useState("");
  const [excessiveEventCountThreshold, setExcessiveEventCountThreshold] = useState(2000);
  const [excessiveEventAutoActionMode, setExcessiveEventAutoActionMode] = useState<"Off" | "Block" | "Kill">("Off");
  const [excessiveEventAutoActionThreshold, setExcessiveEventAutoActionThreshold] = useState(2500);
  const [excessiveEventAutoActionDurationHours, setExcessiveEventAutoActionDurationHours] = useState(24);
  const [savingOpsAlerts, setSavingOpsAlerts] = useState(false);

  // Tenant Management state
  const [tenants, setTenants] = useState<TenantConfiguration[]>([]);
  const [loadingTenants, setLoadingTenants] = useState(false);
  const [previewApproved, setPreviewApproved] = useState<Set<string>>(new Set());

  // Notifications
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  const isGlobalAdmin = user?.isGlobalAdmin === true;

  // Fetch admin configuration
  useEffect(() => {
    if (!isGlobalAdmin) return;

    const fetchAdminConfig = async () => {
      try {
        setLoadingConfig(true);
        setError(null);

        const response = await authenticatedFetch(api.globalConfig.get(), getAccessToken);

        if (!response.ok) {
          throw new Error(`Failed to load admin configuration: ${response.statusText}`);
        }

        const data: AdminConfiguration = await response.json();
        setAdminConfig(data);
        setGlobalRateLimit(data.globalRateLimitRequestsPerMinute);
        setUserRateLimit(data.userRateLimitRequestsPerMinute ?? 120);
        setGlobalAdminRateLimit(data.globalAdminRateLimitRequestsPerMinute ?? 600);
        setPlatformStatsBlobSasUrl(data.platformStatsBlobSasUrl ?? "");
        setCollectorIdleTimeoutMinutes(data.collectorIdleTimeoutMinutes ?? 15);
        setDesktopDetectorNoCandidateTimeoutMinutes(data.desktopDetectorNoCandidateTimeoutMinutes ?? 10);
        setMaxSessionWindowHours(data.maxSessionWindowHours ?? 24);
        setMaintenanceBlockDurationHours(data.maintenanceBlockDurationHours ?? 12);
        setOpsEventRetentionDays(data.opsEventRetentionDays ?? 90);
        setSlaNotificationCooldownHours(data.slaNotificationCooldownHours ?? 24);
        setAllowAgentDowngrade(data.allowAgentDowngrade ?? false);
        setModernDeploymentHarmlessEventIds(parseHarmlessEventIdsJson(data.modernDeploymentHarmlessEventIdsJson));
        setEnableIndexDualWrite(data.enableIndexDualWrite ?? false);
        setSessionDeletionKillSwitch(data.sessionDeletionKillSwitch ?? false);
        try {
          setGlobalDiagPaths(data.diagnosticsGlobalLogPathsJson ? JSON.parse(data.diagnosticsGlobalLogPathsJson) : []);
        } catch {
          setGlobalDiagPaths([]);
        }
        // Ops Alert state hydration
        try {
          setOpsAlertRules(data.opsAlertRulesJson ? JSON.parse(data.opsAlertRulesJson) : []);
        } catch {
          setOpsAlertRules([]);
        }
        setOpsAlertTelegramEnabled(data.opsAlertTelegramEnabled ?? false);
        setOpsAlertTelegramChatId(data.opsAlertTelegramChatId ?? "");
        setOpsAlertTeamsEnabled(data.opsAlertTeamsEnabled ?? false);
        setOpsAlertTeamsWebhookUrl(data.opsAlertTeamsWebhookUrl ?? "");
        setOpsAlertSlackEnabled(data.opsAlertSlackEnabled ?? false);
        setOpsAlertSlackWebhookUrl(data.opsAlertSlackWebhookUrl ?? "");
        setExcessiveEventCountThreshold(data.excessiveEventCountThreshold ?? 2000);
        setExcessiveEventAutoActionMode((data.excessiveEventAutoActionMode ?? "Off") as "Off" | "Block" | "Kill");
        setExcessiveEventAutoActionThreshold(data.excessiveEventAutoActionThreshold ?? 2500);
        setExcessiveEventAutoActionDurationHours(data.excessiveEventAutoActionDurationHours ?? 24);
      } catch (err) {
        if (err instanceof TokenExpiredError) {
          console.error("Session expired while fetching admin configuration");
        } else {
          console.error("Error fetching admin configuration:", err);
        }
        setError(err instanceof Error ? err.message : "Failed to load admin configuration");
      } finally {
        setLoadingConfig(false);
      }
    };

    fetchAdminConfig();
  }, [isGlobalAdmin, getAccessToken]);

  // Fetch tenants + preview whitelist
  const fetchTenants = useCallback(async () => {
    if (!isGlobalAdmin) return;
    try {
      setLoadingTenants(true);

      const [tenantsRes, previewRes] = await Promise.all([
        authenticatedFetch(api.config.all(), getAccessToken),
        authenticatedFetch(api.preview.whitelist(), getAccessToken)
      ]);

      if (!tenantsRes.ok) {
        throw new Error(`Failed to load tenants: ${tenantsRes.statusText}`);
      }

      const data: TenantConfiguration[] = await tenantsRes.json();
      setTenants(data);

      if (previewRes.ok) {
        const previewData = await previewRes.json();
        const approvedIds = new Set<string>(
          (previewData.tenants || []).map((t: { partitionKey: string }) => t.partitionKey)
        );
        setPreviewApproved(approvedIds);
      }
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while fetching tenants");
      } else {
        console.error("Error fetching tenants:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to load tenants");
    } finally {
      setLoadingTenants(false);
    }
  }, [isGlobalAdmin, getAccessToken]);

  useEffect(() => {
    fetchTenants();
  }, [fetchTenants]);

  // Save admin config
  const handleSaveAdminConfig = useCallback(async () => {
    if (!adminConfig) return;
    // Platform settings are GA-only. A read-only Global Reader can reach the admin area (view scope)
    // but must never persist global config — guard here too (the backend also enforces GlobalAdminOnly).
    if (!isGlobalAdmin) return;

    try {
      setSavingConfig(true);
      setError(null);
      setSuccessMessage(null);

      const updatedConfig: AdminConfiguration = {
        ...adminConfig,
        globalRateLimitRequestsPerMinute: globalRateLimit,
        userRateLimitRequestsPerMinute: userRateLimit,
        globalAdminRateLimitRequestsPerMinute: globalAdminRateLimit,
        platformStatsBlobSasUrl: platformStatsBlobSasUrl.trim(),
        collectorIdleTimeoutMinutes,
        desktopDetectorNoCandidateTimeoutMinutes,
        maxSessionWindowHours,
        maintenanceBlockDurationHours,
        opsEventRetentionDays,
        slaNotificationCooldownHours,
        allowAgentDowngrade,
        modernDeploymentHarmlessEventIdsJson: serializeHarmlessEventIds(modernDeploymentHarmlessEventIds),
        enableIndexDualWrite,
        sessionDeletionKillSwitch,
      };

      const response = await authenticatedFetch(api.globalConfig.get(), getAccessToken, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(updatedConfig),
      });

      if (!response.ok) {
        throw new Error(`Failed to save admin configuration: ${response.statusText}`);
      }

      const result = await response.json();
      setAdminConfig(result.config);
      setSuccessMessage("Admin configuration saved successfully!");
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while saving admin configuration");
      } else {
        console.error("Error saving admin configuration:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to save admin configuration");
    } finally {
      setSavingConfig(false);
    }
  }, [isGlobalAdmin, adminConfig, globalRateLimit, userRateLimit, globalAdminRateLimit, platformStatsBlobSasUrl, collectorIdleTimeoutMinutes, desktopDetectorNoCandidateTimeoutMinutes, maxSessionWindowHours, maintenanceBlockDurationHours, opsEventRetentionDays, slaNotificationCooldownHours, allowAgentDowngrade, modernDeploymentHarmlessEventIds, enableIndexDualWrite, sessionDeletionKillSwitch, getAccessToken]);

  // Reset admin config
  const handleResetAdminConfig = useCallback(() => {
    if (!adminConfig) return;
    setGlobalRateLimit(adminConfig.globalRateLimitRequestsPerMinute);
    setUserRateLimit(adminConfig.userRateLimitRequestsPerMinute ?? 120);
    setGlobalAdminRateLimit(adminConfig.globalAdminRateLimitRequestsPerMinute ?? 600);
    setPlatformStatsBlobSasUrl(adminConfig.platformStatsBlobSasUrl ?? "");
    setCollectorIdleTimeoutMinutes(adminConfig.collectorIdleTimeoutMinutes ?? 15);
    setDesktopDetectorNoCandidateTimeoutMinutes(adminConfig.desktopDetectorNoCandidateTimeoutMinutes ?? 10);
    setMaxSessionWindowHours(adminConfig.maxSessionWindowHours ?? 24);
    setMaintenanceBlockDurationHours(adminConfig.maintenanceBlockDurationHours ?? 12);
    setOpsEventRetentionDays(adminConfig.opsEventRetentionDays ?? 90);
    setSlaNotificationCooldownHours(adminConfig.slaNotificationCooldownHours ?? 24);
    setAllowAgentDowngrade(adminConfig.allowAgentDowngrade ?? false);
    setModernDeploymentHarmlessEventIds(parseHarmlessEventIdsJson(adminConfig.modernDeploymentHarmlessEventIdsJson));
    setEnableIndexDualWrite(adminConfig.enableIndexDualWrite ?? false);
    setSessionDeletionKillSwitch(adminConfig.sessionDeletionKillSwitch ?? false);
    try {
      setGlobalDiagPaths(adminConfig.diagnosticsGlobalLogPathsJson ? JSON.parse(adminConfig.diagnosticsGlobalLogPathsJson) : []);
    } catch {
      setGlobalDiagPaths([]);
    }
    try {
      setOpsAlertRules(adminConfig.opsAlertRulesJson ? JSON.parse(adminConfig.opsAlertRulesJson) : []);
    } catch {
      setOpsAlertRules([]);
    }
    setOpsAlertTelegramEnabled(adminConfig.opsAlertTelegramEnabled ?? false);
    setOpsAlertTelegramChatId(adminConfig.opsAlertTelegramChatId ?? "");
    setOpsAlertTeamsEnabled(adminConfig.opsAlertTeamsEnabled ?? false);
    setOpsAlertTeamsWebhookUrl(adminConfig.opsAlertTeamsWebhookUrl ?? "");
    setOpsAlertSlackEnabled(adminConfig.opsAlertSlackEnabled ?? false);
    setOpsAlertSlackWebhookUrl(adminConfig.opsAlertSlackWebhookUrl ?? "");
    setExcessiveEventCountThreshold(adminConfig.excessiveEventCountThreshold ?? 2000);
    setExcessiveEventAutoActionMode((adminConfig.excessiveEventAutoActionMode ?? "Off") as "Off" | "Block" | "Kill");
    setExcessiveEventAutoActionThreshold(adminConfig.excessiveEventAutoActionThreshold ?? 2500);
    setExcessiveEventAutoActionDurationHours(adminConfig.excessiveEventAutoActionDurationHours ?? 24);
    setSuccessMessage(null);
    setError(null);
  }, [adminConfig]);

  // Save diagnostics paths
  const handleSaveDiagPaths = useCallback(async (paths: DiagnosticsLogPath[]) => {
    if (!adminConfig) return;
    if (!isGlobalAdmin) return; // platform settings are GA-only (also route-gated + backend-enforced)
    try {
      setSavingDiagPaths(true);
      setError(null);
      setSuccessMessage(null);

      const updatedConfig: AdminConfiguration = {
        ...adminConfig,
        diagnosticsGlobalLogPathsJson: JSON.stringify(paths),
      };

      const response = await authenticatedFetch(api.globalConfig.get(), getAccessToken, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(updatedConfig),
      });

      if (!response.ok) throw new Error(`Failed to save diagnostics paths: ${response.statusText}`);

      const result = await response.json();
      setAdminConfig(result.config);
      setGlobalDiagPaths(paths);
      setSuccessMessage("Global diagnostics log paths saved successfully!");
      setTimeout(() => setSuccessMessage(null), 4000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while saving diagnostics paths");
      }
      setError(err instanceof Error ? err.message : "Failed to save diagnostics paths");
    } finally {
      setSavingDiagPaths(false);
    }
  }, [isGlobalAdmin, adminConfig, getAccessToken]);

  // Save ops alert config (rules + providers)
  const handleSaveOpsAlertConfig = useCallback(async (
    rules: OpsAlertRule[],
    telegramEnabled: boolean, telegramChatId: string,
    teamsEnabled: boolean, teamsWebhookUrl: string,
    slackEnabled: boolean, slackWebhookUrl: string,
    newExcessiveThreshold: number,
    newAutoActionMode: "Off" | "Block" | "Kill",
    newAutoActionThreshold: number,
    newAutoActionDurationHours: number,
  ) => {
    if (!adminConfig) return;
    if (!isGlobalAdmin) return; // platform settings are GA-only (also route-gated + backend-enforced)
    try {
      setSavingOpsAlerts(true);
      setError(null);
      setSuccessMessage(null);

      const updatedConfig: AdminConfiguration = {
        ...adminConfig,
        opsAlertRulesJson: JSON.stringify(rules),
        opsAlertTelegramEnabled: telegramEnabled,
        opsAlertTelegramChatId: telegramChatId.trim(),
        opsAlertTeamsEnabled: teamsEnabled,
        opsAlertTeamsWebhookUrl: teamsWebhookUrl.trim(),
        opsAlertSlackEnabled: slackEnabled,
        opsAlertSlackWebhookUrl: slackWebhookUrl.trim(),
        excessiveEventCountThreshold: newExcessiveThreshold,
        excessiveEventAutoActionMode: newAutoActionMode,
        excessiveEventAutoActionThreshold: newAutoActionThreshold,
        excessiveEventAutoActionDurationHours: newAutoActionDurationHours,
      };

      const response = await authenticatedFetch(api.globalConfig.get(), getAccessToken, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(updatedConfig),
      });

      if (!response.ok) throw new Error(`Failed to save alert configuration: ${response.statusText}`);

      const result = await response.json();
      setAdminConfig(result.config);
      setOpsAlertRules(rules);
      setOpsAlertTelegramEnabled(telegramEnabled);
      setOpsAlertTelegramChatId(telegramChatId.trim());
      setOpsAlertTeamsEnabled(teamsEnabled);
      setOpsAlertTeamsWebhookUrl(teamsWebhookUrl.trim());
      setOpsAlertSlackEnabled(slackEnabled);
      setOpsAlertSlackWebhookUrl(slackWebhookUrl.trim());
      setExcessiveEventCountThreshold(newExcessiveThreshold);
      setExcessiveEventAutoActionMode(newAutoActionMode);
      setExcessiveEventAutoActionThreshold(newAutoActionThreshold);
      setExcessiveEventAutoActionDurationHours(newAutoActionDurationHours);
      setSuccessMessage("Alert configuration saved successfully!");
      setTimeout(() => setSuccessMessage(null), 4000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while saving alert configuration");
      }
      setError(err instanceof Error ? err.message : "Failed to save alert configuration");
    } finally {
      setSavingOpsAlerts(false);
    }
  }, [isGlobalAdmin, adminConfig, getAccessToken]);

  return (
    <AdminConfigContext.Provider value={{
      adminConfig, setAdminConfig, loadingConfig, savingConfig, setSavingConfig,
      globalRateLimit, setGlobalRateLimit,
      userRateLimit, setUserRateLimit,
      globalAdminRateLimit, setGlobalAdminRateLimit,
      platformStatsBlobSasUrl, setPlatformStatsBlobSasUrl,
      collectorIdleTimeoutMinutes, setCollectorIdleTimeoutMinutes,
      desktopDetectorNoCandidateTimeoutMinutes, setDesktopDetectorNoCandidateTimeoutMinutes,
      maxSessionWindowHours, setMaxSessionWindowHours,
      maintenanceBlockDurationHours, setMaintenanceBlockDurationHours,
      opsEventRetentionDays, setOpsEventRetentionDays,
      slaNotificationCooldownHours, setSlaNotificationCooldownHours,
      allowAgentDowngrade, setAllowAgentDowngrade,
      modernDeploymentHarmlessEventIds, setModernDeploymentHarmlessEventIds,
      enableIndexDualWrite, setEnableIndexDualWrite,
      sessionDeletionKillSwitch, setSessionDeletionKillSwitch,
      globalDiagPaths, setGlobalDiagPaths, savingDiagPaths,
      opsAlertRules, setOpsAlertRules,
      opsAlertTelegramEnabled, setOpsAlertTelegramEnabled,
      opsAlertTelegramChatId, setOpsAlertTelegramChatId,
      opsAlertTeamsEnabled, setOpsAlertTeamsEnabled,
      opsAlertTeamsWebhookUrl, setOpsAlertTeamsWebhookUrl,
      opsAlertSlackEnabled, setOpsAlertSlackEnabled,
      opsAlertSlackWebhookUrl, setOpsAlertSlackWebhookUrl,
      excessiveEventCountThreshold,
      excessiveEventAutoActionMode,
      excessiveEventAutoActionThreshold,
      excessiveEventAutoActionDurationHours,
      savingOpsAlerts,
      tenants, setTenants, loadingTenants, fetchTenants,
      previewApproved, setPreviewApproved,
      error, setError, successMessage, setSuccessMessage,
      getAccessToken,
      handleSaveAdminConfig, handleResetAdminConfig, handleSaveDiagPaths, handleSaveOpsAlertConfig,
    }}>
      {children}
    </AdminConfigContext.Provider>
  );
}
