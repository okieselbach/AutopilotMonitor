"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { useNotifications } from "../../contexts/NotificationContext";
import { api } from "@/lib/api";
import { TokenExpiredError } from "@/lib/authenticatedFetch";
import { ApiError, apiErrorText, fetchJson, fetchOk, jsonBody } from "@/lib/apiClient";
import { CONFIG_PATH_PREFIX, invalidateCachedAuthFetch } from "@/lib/cachedAuthFetch";
import { trackEvent } from "@/lib/appInsights";
import { classifyAccessCheck, type AccessCheckOutcome, type AccessCheckPayload } from "@/lib/accessCheck";
import { primaryClientId } from "@/lib/authApp";
import { canManageBootstrapSessions } from "@/lib/tenantScope";
import { gateChangeFor, parseConsentTrigger, type ConsentTrigger, type ValidationTrigger } from "./lib/consentTrigger";

// Human label for a validation trigger.
const validationLabel = (t: ValidationTrigger) =>
  t === "corporate" ? "Corporate Identifier Validation"
    : t === "device-preparation" ? "Device Association Validation"
      : "Autopilot Device Validation";

// Enable-confirmation suffix: every consent-driven validation is an accepting agent gate.
const VALIDATION_ENABLED_SUFFIX = " enabled. Backend agent endpoints are now unlocked for this tenant.";

// The one moment someone waits in front of the page: other backend instances hold the config up to 5 minutes.
const ACCESS_RESTORED_MESSAGE = "Access restored. The notice on the dashboard can take a few minutes to disappear.";
import { parseSasExpiry } from "./components/DiagnosticsSection";
import { COMMUNITY_DEFAULT, parseEditionInfo, type EditionInfo } from "@/lib/edition";
import { TenantConfiguration, TenantAdmin } from "./types";
import { SECTION_FIELD_MAP, type SectionFieldSpec, type SettingsSectionName } from "./sectionFieldMap";
import {
  DEFAULT_SETTINGS_FORM,
  configFromForm,
  formFromConfig,
  hardwareRejectionNotify,
  sectionsWithUnsavedEdits,
  settingsFormSetters,
  type SettingsForm,
  type SettingsFormSetters,
} from "./lib/settingsForm";
import { changedTenantConfigFields, patchTenantConfigFields } from "@/lib/tenantConfigSave";
import { leaveOrAsk } from "@/lib/unsavedChanges";
import { looksLikeGuid, type MemberKind } from "@/lib/principalKeys";
import { type BootstrapSessionItem } from "./components/BootstrapSessionsSection";
import type {
  AddTenantAdminRequest,
  AutopilotConsentFailureRequest,
  AutopilotConsentStatusResponse,
  AutopilotConsentSuccessRequest,
  AutopilotConsentUrlResponse,
  CreateBootstrapSessionRequest,
  OffboardResponse,
  TenantFeatureFlagsResponse,
  TestNotificationChannelRequest,
  TestWebhookNotificationResponse,
  UpdateMemberPermissionsRequest,
} from "@/lib/generated/wire-types.generated";

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

interface TenantConfigContextValue
  extends SettingsForm, Omit<SettingsFormSetters, "setWebhookNotifyOnHardwareRejection"> {
  // Core
  config: TenantConfiguration | null;
  loading: boolean;
  /**
   * True when the current user may CHANGE tenant configuration (Tenant Admin or Global Admin).
   * Operators reach the settings area read-only: sections must render no Save bar and
   * disable their inputs when this is false. Mirrors the backend write gate (PUT config is
   * TenantAdminOrGA), so hiding the affordances is UX — the server enforces regardless.
   */
  canEditConfig: boolean;
  savingSection: string | null;
  /** Sections whose Save would write an edit. Never a section that saves on change; always empty without write access. */
  unsavedSections: ReadonlySet<SettingsSectionName>;
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
  /**
   * Optional Graph add-on roles the previous app holds but the new app still lacks — the
   * backend refused the switch until the admin grants exactly these on the new app. Learned from
   * this session's consent-status / access-check responses; cleared once the tenant switches.
   */
  homingMissingRoles: string[] | null;
  /** Microsoft Entra keeps refusing the tenant's app token: the section shows the repair instead of the migration nudge. */
  graphAccessRejected: boolean;
  /** Only with graphAccessRejected: the refusal says the enterprise application is missing. */
  graphAccessAppMissing: boolean;

  // Validation
  /**
   * Toggle + persist W365 Cloud PC validation in one shot. No consent flow — the backing
   * CloudPC.Read.All permission is granted via the Optional Graph capabilities add-on script.
   */
  handleToggleCloudPcValidation: (newValue: boolean) => Promise<void>;
  handleToggleIntuneDeviceBinding: (newValue: boolean) => Promise<void>;
  /**
   * Persist a validation-gate change immediately. The validation section has NO save bar —
   * every gate change that doesn't run the consent flow (disable, and enabling the second
   * gate while the first already carries the consent) must persist through this, or it stays
   * local component state and silently reverts on the next config load (prod report
   * 2026-08-01: corporate-identifier "came back on" after every disable — the off toggle
   * had never reached the server).
   */
  saveValidationGate: (changes: { validateAutopilotDevice?: boolean; validateCorporateIdentifier?: boolean; validateDeviceAssociation?: boolean }) => Promise<boolean>;
  autopilotConsentInProgress: boolean;
  beginDeviceValidationConsentFlow: (trigger: ConsentTrigger) => Promise<void>;
  /**
   * Probe whether the multi-tenant app is already pre-approved in this tenant (by someone with
   * consent rights) and, if so, enable validation without running the /adminconsent redirect —
   * the "rights-less admin" escape hatch. With "restore" it enables nothing.
   */
  detectExistingAccess: (trigger: ConsentTrigger) => Promise<void>;

  // Hardware whitelist
  /** What the "notify on rejection" switch shows (hardwareRejectionNotify). */
  webhookNotifyOnHardwareRejection: boolean;
  /** With channels configured, writes the flag through to every channel. */
  setWebhookNotifyOnHardwareRejection: (v: boolean) => void;
  handleSaveHardwareWhitelist: () => Promise<void>;

  // Agent settings and contact
  handleSaveContact: () => Promise<void>;
  handleSaveAgentParameters: () => Promise<void>;
  handleSaveAgentCollectors: () => Promise<void>;

  // Agent analyzers
  newAllowedAccount: string;
  setNewAllowedAccount: (v: string) => void;
  handleSaveAgentAnalyzers: () => Promise<void>;

  // Unrestricted mode
  handleSaveUnrestrictedMode: (value: boolean) => Promise<boolean>;

  // Notifications / channels
  testingChannelId: string | null;
  testChannelResult: { channelId: string; success: boolean; message: string } | null;
  handleTestChannel: (channelId: string) => Promise<void>;
  handleSaveNotifications: () => Promise<void>;

  // SLA Targets
  handleSaveSlaTargets: () => Promise<void>;

  // Diagnostics
  newDiagPath: string;
  setNewDiagPath: (v: string) => void;
  newDiagDesc: string;
  setNewDiagDesc: (v: string) => void;
  handleSaveDiagnostics: () => Promise<void>;

  // Admin management
  admins: TenantAdmin[];
  loadingAdmins: boolean;
  newAdminEmail: string;
  setNewAdminEmail: (v: string) => void;
  newMemberRole: string;
  setNewMemberRole: (v: string) => void;
  /** Person (UPN) or service principal (application id) — see lib/principalKeys. */
  newMemberKind: MemberKind;
  setNewMemberKind: (v: MemberKind) => void;
  addingAdmin: boolean;
  removingAdmin: string | null;
  togglingAdmin: string | null;
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
  handleSaveDataManagement: () => Promise<void>;

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

const NO_UNSAVED_SECTIONS: ReadonlySet<SettingsSectionName> = new Set();

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
  // change configuration. Drives section rendering (no Save bar, disabled inputs)
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
  const [newMemberKind, setNewMemberKind] = useState<MemberKind>("user");
  const [addingAdmin, setAddingAdmin] = useState(false);
  const [removingAdmin, setRemovingAdmin] = useState<string | null>(null);
  const [togglingAdmin, setTogglingAdmin] = useState<string | null>(null);

  // Offboard
  const [showOffboardDialog, setShowOffboardDialog] = useState(false);
  const [offboardConfirmText, setOffboardConfirmText] = useState("");
  const [offboarding, setOffboarding] = useState(false);
  const [offboardError, setOffboardError] = useState<string | null>(null);
  const [offboardingInProgress, setOffboardingInProgress] = useState<OffboardingInProgressInfo | null>(null);

  // Bootstrap sessions
  const [bootstrapSessions, setBootstrapSessions] = useState<BootstrapSessionItem[]>([]);
  const [bootstrapLoading, setBootstrapLoading] = useState(false);

  // Form state: every value the sections edit, in one object (lib/settingsForm.ts).
  const [form, setForm] = useState<SettingsForm>(DEFAULT_SETTINGS_FORM);
  const formSetters = useMemo(() => settingsFormSetters(setForm), []);
  const [autopilotConsentInProgress, setAutopilotConsentInProgress] = useState(false);

  // Notification channel test
  const [testingChannelId, setTestingChannelId] = useState<string | null>(null);
  const [testChannelResult, setTestChannelResult] = useState<{ channelId: string; success: boolean; message: string } | null>(null);

  // Draft inputs of the list editors: not part of the configuration until added to their list.
  const [newDiagPath, setNewDiagPath] = useState("");
  const [newDiagDesc, setNewDiagDesc] = useState("");
  const [newAllowedAccount, setNewAllowedAccount] = useState("");

  // Edition / trial surface (from feature-flags; fail-closed Community until loaded)
  const [editionInfo, setEditionInfo] = useState<EditionInfo>(COMMUNITY_DEFAULT);
  const [appHomingFunnelActive, setAppHomingFunnelActive] = useState(false);
  const [homingFlipped, setHomingFlipped] = useState(false);
  const [homingMissingRoles, setHomingMissingRoles] = useState<string[] | null>(null);
  const [graphAccessRejected, setGraphAccessRejected] = useState(false);
  const [graphAccessAppMissing, setGraphAccessAppMissing] = useState(false);
  // Last probe said the flip is still converging (consent on the new app propagating). Read by
  // the post-consent poll loop — a ref, because the loop runs inside one async callback.
  const homingPendingRef = useRef(false);

  // The backend auto-flipped this tenant's app-reg homing during consent-status/access-check.
  // Reflect it locally without a refetch: badge/one-liner surfaces read config.homedAppClientId,
  // and the funnel banner must drop immediately (the tenant is primary-homed now).
  const markHomingFlipped = useCallback((source: "consent-status" | "access-check") => {
    // Strategic trace point: the tenant just moved to the new app registration.
    trackEvent("app_homing_flipped", { source });
    setHomingFlipped(true);
    setAppHomingFunnelActive(false);
    // The flip changed appHomingFunnelActive in the feature flags the dashboard banner reads.
    invalidateCachedAuthFetch(CONFIG_PATH_PREFIX);
    setHomingMissingRoles(null);
    homingPendingRef.current = false;
    setConfig(prev => (prev ? { ...prev, homedAppClientId: primaryClientId() ?? prev.homedAppClientId } : prev));
  }, []);

  // Every consent-status / access-check response carries the homing probe's verdict as a side
  // signal: flipped, still converging, or blocked by add-on roles the new app lacks. The blocked
  // list drives the funnel's "one more step" banner (grant command against the NEW app) and is
  // only ever cleared by the flip itself — a later transient probe must not hide the to-do.
  const noteHomingProbe = useCallback((payload: AccessCheckPayload | undefined, source: "consent-status" | "access-check") => {
    if (!payload) return;
    if (payload.homingFlipped) {
      markHomingFlipped(source);
      return;
    }
    homingPendingRef.current = payload.appHomingPending === true;
    const roles = payload.appHomingMissingRoles;
    if (roles && roles.length > 0) {
      // Funnel trace point: consent done, switch held back by tenant-side add-on grants.
      trackEvent("app_homing_blocked_by_roles", { source, roleCount: roles.length });
      setHomingMissingRoles(roles);
    }
  }, [markHomingFlipped]);
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
        // Flags and config load in parallel; the flags are best-effort (fail-closed: Community default).
        const flagsPromise = fetchJson<TenantFeatureFlagsResponse>(api.config.featureFlags(tenantId), getAccessToken).catch(() => null);
        const applyFlags = (flags: TenantFeatureFlagsResponse | null) => {
          if (!flags) return;
          setEditionInfo(parseEditionInfo(flags));
          setAppHomingFunnelActive(flags.appHomingFunnelActive === true);
          setGraphAccessRejected(flags.graphAccessRejected === true);
          setGraphAccessAppMissing(flags.graphAccessAppMissing === true);
        };

        let data: TenantConfiguration;
        try {
          data = await fetchJson<TenantConfiguration>(api.config.tenant(tenantId), getAccessToken);
        } catch (err) {
          const flags = await flagsPromise;
          applyFlags(flags);
          // Deploy-order safety net: a backend that still gates the config GET admin-tier
          // 403s an Operator. Fall back to the feature-flags minimal view (pre-change
          // behavior for non-admins) instead of surfacing a load error.
          if (err instanceof ApiError && err.status === 403 && !isAdminOrGA && flags) {
            setConfig({ bootstrapTokenEnabled: flags.bootstrapTokenEnabled } as TenantConfiguration);
            return;
          }
          throw err;
        }
        applyFlags(await flagsPromise);
        setConfig(data);
        setForm(formFromConfig(data));

        // Parse SAS expiry and fire notification to bell if needed
        const sasUrl = data.diagnosticsBlobSasUrl ?? "";
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
        console.error("Error fetching configuration:", err);
        setError(apiErrorText(err, "Failed to load configuration"));
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
      const data = await fetchJson<TenantAdmin[]>(api.tenants.admins(tenantId), getAccessToken);
      setAdmins(data);
    } catch (err) {
      console.error("Error fetching admins:", err);
      setError(apiErrorText(err, "Failed to load admins"));
    } finally {
      setLoadingAdmins(false);
    }
  }, [tenantId, getAccessToken]);

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
      const data = await fetchJson<{ sessions?: BootstrapSessionItem[] }>(api.bootstrap.sessions(tenantId), getAccessToken);
      setBootstrapSessions(data.sessions || []);
    } catch (err) {
      console.error("Failed to fetch bootstrap sessions:", err);
    } finally {
      setBootstrapLoading(false);
    }
  }, [tenantId, getAccessToken]);

  // The list route is the BootstrapManagerOrGA tier: any other role only ever received a 403.
  const canManageBootstrap = canManageBootstrapSessions(user);

  useEffect(() => {
    // Effective availability (mirrors backend IsBootstrapEnabled): Pro plan includes bootstrap;
    // the per-tenant GA flag is the additive Community enable.
    const bootstrapAvailable = editionInfo.edition === "pro" || config?.bootstrapTokenEnabled;
    if (!tenantId || !bootstrapAvailable || !canManageBootstrap) return;
    const run = async () => {
      await fetchBootstrapSessions();
    };
    void run();
  }, [tenantId, editionInfo.edition, config?.bootstrapTokenEnabled, canManageBootstrap, fetchBootstrapSessions]);

  // Built-in sections + global diagnostics paths are read-only context for every role and
  // live in useDiagnosticsPathsCatalog (GET /api/diagnostics/paths), not in this provider.

  // -----------------------------------------------------------------------
  // Save configuration (shared by all sections)
  // -----------------------------------------------------------------------
  // Resolves to null when the save went through, otherwise to the text of the failure.
  const runSectionSave = useCallback(async (sectionName: SettingsSectionName, overrides?: { validateAutopilotDevice?: boolean; validateCorporateIdentifier?: boolean; validateDeviceAssociation?: boolean; validateCloudPcDevice?: boolean; validateIntuneDeviceBinding?: boolean; unrestrictedMode?: boolean }): Promise<string | null> => {
    // Read-only viewers (Operators) have no save affordances; this guard covers any path
    // that still reaches a save (the backend would 403 the PATCH regardless).
    if (!tenantId || !config || !canEditConfig) return "This configuration cannot be edited right now.";

    // The validation gates and Unrestricted Mode have no save bar, so their form state must always
    // equal the stored row. Some of their toggles move the state before this save runs, so a failed
    // save puts it back: success syncs to the merged row, failure to the loaded one.
    const syncGateState = (stored: TenantConfiguration) => {
      const gates = formFromConfig(stored);
      setForm((current) => ({
        ...current,
        validateAutopilotDevice: gates.validateAutopilotDevice,
        validateCorporateIdentifier: gates.validateCorporateIdentifier,
        validateDeviceAssociation: gates.validateDeviceAssociation,
        validateCloudPcDevice: gates.validateCloudPcDevice,
        validateIntuneDeviceBinding: gates.validateIntuneDeviceBinding,
        unrestrictedMode: gates.unrestrictedMode,
      }));
    };

    try {
      setSavingSection(sectionName);
      setError(null);
      setSuccessMessage(null);

      // A gate toggle passes its new value explicitly: the state update it made before calling
      // has not rendered yet, so the form still holds the previous value.
      const gateOverrides = Object.fromEntries(Object.entries(overrides ?? {}).filter(([, value]) => value != null));
      const updatedConfig: TenantConfiguration = { ...configFromForm(config, form), ...gateOverrides };

      // Per-section PATCH: send ONLY this section's fields (plus documented write-throughs)
      // that actually differ from the loaded config. The other ~90 fields never round-trip,
      // so a stale read cannot revert unrelated fields (the 2026-07-31 incident class), and
      // GA-only toggles a tenant admin cannot edit are simply never in the payload.
      const spec: SectionFieldSpec = SECTION_FIELD_MAP[sectionName];
      const patchFields = changedTenantConfigFields(config, updatedConfig, [...spec.fields, ...(spec.alsoWrites ?? [])]);

      // The PATCH response carries applied field names + masked diff, not the config: the
      // backend verified exactly these fields changed, so merge them locally.
      if (await patchTenantConfigFields(api.config.fields(tenantId), patchFields, `settings:${sectionName}`, getAccessToken)) {
        // The feature flags derive from these fields: the cached copy is stale from here on.
        invalidateCachedAuthFetch(CONFIG_PATH_PREFIX);
        // Functional merge: the Agent Settings page has two Save bars, and a save still in flight
        // must not lose its fields to the later one that started from the same loaded config.
        setConfig((prev) => ({ ...(prev ?? config), ...patchFields }) as TenantConfiguration);
      }

      // Sync the gate-relevant form state with what is now persisted (merge of loaded
      // config + this patch) — mirrors the old PUT-response sync.
      syncGateState({ ...config, ...patchFields } as TenantConfiguration);
      trackEvent("settings_saved", { section: sectionName, fieldCount: Object.keys(patchFields).length });
      setSuccessMessage("Configuration saved successfully!");
      setTimeout(() => setSuccessMessage(null), 3000);
      return null;
    } catch (err) {
      syncGateState(config);
      const msg = apiErrorText(err, "Failed to save configuration");
      trackEvent("settings_error", { action: "save", section: sectionName, error: msg });
      setError(msg);
      return msg;
    } finally {
      setSavingSection(null);
    }
  }, [tenantId, config, canEditConfig, getAccessToken, form]);

  // Nothing counts before the configuration has loaded or for a read-only viewer: their form still
  // holds the defaults, which differ from the stored row without anyone having edited it.
  const unsavedSections = useMemo(
    () => (config && canEditConfig && !loading ? sectionsWithUnsavedEdits(config, form) : NO_UNSAVED_SECTIONS),
    [config, canEditConfig, loading, form],
  );

  // Flows without a save bar (validation gates, Unrestricted Mode, consent) branch on the outcome.
  const saveConfiguration = useCallback(
    async (sectionName: SettingsSectionName, overrides?: Parameters<typeof runSectionSave>[1]) =>
      (await runSectionSave(sectionName, overrides)) === null,
    [runSectionSave],
  );

  // Save bars: a failure throws, so SaveBar shows its reason next to the button.
  const saveSection = useCallback(async (sectionName: SettingsSectionName) => {
    const failure = await runSectionSave(sectionName);
    if (failure !== null) throw new Error(failure);
  }, [runSectionSave]);

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
      payload = await fetchJson<AccessCheckPayload>(api.config.autopilotAccessCheck(tenantId), getAccessToken);
      ok = true;
    } catch (err) {
      if (err instanceof TokenExpiredError) throw err;
      // A backend refusal is an access-check outcome; only a failed round trip is transient.
      if (!(err instanceof ApiError)) return "transient";
      ok = false;
    }
    // Side signal, orthogonal to the access classification: the probe may have auto-flipped
    // the tenant's app-reg homing (self-service migration), deferred it, or named what blocks it.
    noteHomingProbe(payload, "access-check");
    return classifyAccessCheck(ok, payload);
  }, [tenantId, getAccessToken, noteHomingProbe]);

  // Direct gate persist for the toggle UI (disable + second-gate enable): same shared config
  // PATCH, explicit override values. saveConfiguration re-syncs the local gate state to the stored
  // row (the merge on success, the loaded row on failure), so the toggle shows the persisted truth.
  const saveValidationGate = useCallback(
    (changes: { validateAutopilotDevice?: boolean; validateCorporateIdentifier?: boolean; validateDeviceAssociation?: boolean }): Promise<boolean> =>
      saveConfiguration("autopilotValidation", changes),
    [saveConfiguration],
  );

  // What verified access does for a trigger. A validation trigger persists its gate bool via the
  // shared config PUT and returns true ONLY on a confirmed server persist — saveConfiguration
  // reports failure as false (and sets the error). Callers MUST NOT claim success without checking
  // this, or the admin sees "enabled" while the gate bool never landed. "restore" persists nothing:
  // the fresh token behind the probe already cleared the backend state, the page only catches up.
  // Message is caller-owned (reconcile vs normal-consent differ).
  const applyVerifiedAccess = useCallback(
    async (trigger: ConsentTrigger): Promise<boolean> => {
      const gate = gateChangeFor(trigger);
      if (gate) return saveConfiguration("autopilotValidation", gate);

      setGraphAccessRejected(false);
      setGraphAccessAppMissing(false);
      // The state just changed in the feature flags the dashboard banner reads.
      invalidateCachedAuthFetch(CONFIG_PATH_PREFIX);
      return true;
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
    async (trigger: ConsentTrigger): Promise<AccessCheckOutcome | "failed"> => {
      const outcome = await probeAccessCheck();
      if (outcome !== "reconciled") return outcome;

      const saved = await applyVerifiedAccess(trigger);
      if (!saved) return "failed";

      setSuccessMessage(trigger === "restore"
        ? ACCESS_RESTORED_MESSAGE
        : `Access is already approved by your organization — ${validationLabel(trigger)}${VALIDATION_ENABLED_SUFFIX}`);
      return "reconciled";
    },
    [probeAccessCheck, applyVerifiedAccess],
  );

  const beginDeviceValidationConsentFlow = useCallback(async (trigger: ConsentTrigger) => {
    if (!tenantId) return;
    try {
      setAutopilotConsentInProgress(true);
      setError(null);
      setSuccessMessage(null);

      const redirectUri = `${window.location.origin}/settings/tenant/autopilot`;
      const data = await fetchJson<AutopilotConsentUrlResponse>(
        api.config.autopilotConsentUrl(tenantId, redirectUri),
        getAccessToken,
      );
      const consentUrl = data.consentUrl;
      if (!consentUrl) {
        throw new Error("Backend did not return a consent URL.");
      }

      // The consent page reloads the portal: unsaved edits on other settings pages are asked about first.
      leaveOrAsk({
        via: "consent",
        proceed: () => {
          // Trace the funnel entry: this consent redirect targets the NEW app and will auto-flip
          // the tenant on verified return. AI's pagehide beacon carries it out past the redirect.
          trackEvent("consent_flow_started", { trigger, funnel: data.willAutoFlipHoming === true });

          sessionStorage.setItem("deviceValidationConsentPending", "true");
          sessionStorage.setItem("consentTrigger", trigger);
          window.location.href = consentUrl;
        },
        onStay: () => setAutopilotConsentInProgress(false),
      });
    } catch (err) {
      trackEvent("consent_flow_start_failed", {
        trigger,
        error: err instanceof Error ? err.message : String(err),
      });
      setError(apiErrorText(err, "Failed to start admin consent flow"));
      setAutopilotConsentInProgress(false);
    }
  }, [tenantId, getAccessToken]);

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

      const trigger = parseConsentTrigger(sessionStorage.getItem("consentTrigger"));
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
          await fetchOk(api.config.autopilotConsentFailure(tenantId), getAccessToken, {
            method: "POST",
            body: jsonBody<AutopilotConsentFailureRequest>({
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
          const outcome = await tryReconcilePreApprovedConsent(trigger);
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

        const statusData = await fetchJson<AutopilotConsentStatusResponse>(
          api.config.autopilotConsentStatus(tenantId),
          getAccessToken,
        );
        noteHomingProbe(statusData, "consent-status");
        if (!statusData.isConsented) {
          throw new Error(statusData.message || "Consent is not active yet for this tenant.");
        }

        // Best-effort ops-event: pairs with ConsentFlowStarted/Failed so admins can see
        // whether repeated failures eventually resolved. Don't block the UI if it fails.
        try {
          await fetchOk(api.config.autopilotConsentSuccess(tenantId), getAccessToken, {
            method: "POST",
            body: jsonBody<AutopilotConsentSuccessRequest>({ trigger }),
          });
        } catch {
          // swallow — observability only
        }

        // Verify the concrete role is present before opening the gate. consent-status only proves
        // a token is acquirable (SP provisioned) — NOT that DeviceManagementServiceConfig.Read.All
        // is in the roles claim. Persist ONLY on an authoritative "reconciled": a transient probe
        // (timeout / Graph 5xx / propagation lag) is inconclusive, so opening the gate on it would
        // re-introduce the exact risk this check closes (gate open while the role is unproven).

        // AAD consent-propagation awareness: right after a successful admin consent the app-only
        // token often mints with an EMPTY roles claim for 30-90s (observed live 2026-07-31,
        // one tenant: token 43s post-consent with GrantedRoleCount=0) — a single-shot probe
        // misclassified that window as "permission not granted" and showed a hard error while
        // everything was actually fine. The access-check invalidates + re-mints fresh on every
        // call, so a bounded poll converges as soon as Microsoft has propagated the role. The
        // consent-in-progress spinner stays up for the duration (finally-block clears it).
        let probe: AccessCheckOutcome = "absent";
        let reconciled = false;
        let attempts = 0;
        for (attempts = 1; attempts <= 5; attempts++) {
          if (attempts > 1) await new Promise((resolve) => setTimeout(resolve, 15000));
          probe = await probeAccessCheck();
          if (probe === "reconciled") reconciled = true;
          // Keep polling while the homing switch is still converging: the switch rides this very
          // probe, and the validation role resolves against the PREVIOUS app first — breaking on
          // that reconcile left funnel tenants consented but never switched (live 2026-09-02:
          // one probe 1 s after consent, role still propagating on the new app, no retry).
          if (reconciled && !homingPendingRef.current) break;
        }

        if (reconciled) {
          if (attempts > 1) trackEvent("consent_verify_propagated", { trigger, attempts: String(attempts) });
          const saved = await applyVerifiedAccess(trigger);
          if (saved) {
            setSuccessMessage(trigger === "restore" ? ACCESS_RESTORED_MESSAGE : `${validationLabel(trigger)}${VALIDATION_ENABLED_SUFFIX}`);
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
        // Covers consent-status non-ok and "not consented yet" (both throw above).
        trackEvent("consent_verify_failed", {
          trigger,
          stage: "status-check",
          error: err instanceof Error ? err.message : String(err),
        });
        setError(apiErrorText(err, "Failed to verify consent"));
      } finally {
        setAutopilotConsentInProgress(false);
      }
    };

    handleConsentCallback();
  }, [tenantId, config, router, getAccessToken, addNotification, tryReconcilePreApprovedConsent, probeAccessCheck, applyVerifiedAccess, noteHomingProbe]);

  // Manual "Detect existing access" affordance — for admins who never even attempt the consent
  // redirect because they know they lack consent rights. Probes and, on success, enables
  // validation silently; otherwise surfaces an actionable message.
  const detectExistingAccess = useCallback(async (trigger: ConsentTrigger) => {
    if (!tenantId) return;
    try {
      setAutopilotConsentInProgress(true);
      setError(null);
      setSuccessMessage(null);

      const outcome = await tryReconcilePreApprovedConsent(trigger);
      if (trigger === "restore" && (outcome === "transient" || outcome === "absent")) {
        // Under the probe's short budget a refusal also reads as transient: "timed out" would mislead here.
        trackEvent("detect_access_failed", { trigger, outcome });
        setError("Access could not be confirmed. Use Restore access to grant admin consent, or ask someone with consent rights (Global Administrator or Privileged Role Administrator) to approve the app first.");
      } else if (outcome === "transient") {
        trackEvent("detect_access_failed", { trigger, outcome });
        setError("Couldn't verify access right now (timed out). Please try again in a moment.");
      } else if (outcome === "absent") {
        trackEvent("detect_access_failed", { trigger, outcome });
        setError("No existing access detected for this tenant. Complete admin consent, or ask someone with consent rights (Global Administrator or Privileged Role Administrator) to approve the app first.");
      }
      // "reconciled" => success message already set by the helper.
      // "failed" => access present but persist failed; saveConfiguration already set the error.
    } catch (err) {
      setError(apiErrorText(err, "Failed to detect existing access"));
    } finally {
      setAutopilotConsentInProgress(false);
    }
  }, [tenantId, tryReconcilePreApprovedConsent]);

  // -----------------------------------------------------------------------
  // Test webhook channel
  // -----------------------------------------------------------------------
  const handleTestChannel = useCallback(async (channelId: string) => {
    if (!tenantId) return;
    setTestingChannelId(channelId);
    setTestChannelResult(null);
    try {
      const data = await fetchJson<TestWebhookNotificationResponse>(api.config.testNotification(tenantId), getAccessToken, {
        method: "POST",
        body: jsonBody<TestNotificationChannelRequest>({ channelId }),
      });
      setTestChannelResult({ channelId, success: data.success, message: data.message });
    } catch (err) {
      setTestChannelResult({ channelId, success: false, message: apiErrorText(err, "Failed to send test notification.") });
    } finally {
      setTestingChannelId(null);
    }
  }, [tenantId, getAccessToken]);

  // -----------------------------------------------------------------------
  // Per-section save handlers
  // -----------------------------------------------------------------------
  const handleSaveHardwareWhitelist = useCallback(() => saveSection("hardwareWhitelist"), [saveSection]);

  // Hardware-rejection notify: with channels configured, the hardware-section toggle is a
  // convenience view over the per-channel flags (checked = any enabled channel opted in;
  // toggling writes through to every channel). Without channels it edits the legacy tenant
  // flag, which the backend maps into the synthesized channel.
  const effectiveHwRejectionNotify = hardwareRejectionNotify(form);
  const setHwRejectionNotifyWriteThrough = useCallback((v: boolean) => {
    setForm((current) => ({
      ...current,
      webhookNotifyOnHardwareRejection: v,
      notificationChannels: current.notificationChannels.map((c) => ({ ...c, notifyOnHardwareRejection: v })),
    }));
  }, []);

  const handleSaveAgentParameters = useCallback(() => saveSection("agentParameters"), [saveSection]);

  const handleSaveAgentCollectors = useCallback(() => saveSection("agentCollectors"), [saveSection]);

  const handleSaveAgentAnalyzers = useCallback(() => saveSection("agentAnalyzers"), [saveSection]);

  const handleSaveNotifications = useCallback(() => saveSection("notifications"), [saveSection]);

  const handleSaveSlaTargets = useCallback(() => saveSection("slaTargets"), [saveSection]);

  const handleSaveContact = useCallback(() => saveSection("contact"), [saveSection]);

  const handleSaveDiagnostics = useCallback(() => saveSection("diagnostics"), [saveSection]);

  const handleSaveDataManagement = useCallback(() => saveSection("dataManagement"), [saveSection]);

  // Persist the toggle with the new value passed explicitly. The on/off value MUST be threaded
  // through as an override rather than read from `unrestrictedMode` state: the toggle calls this
  // synchronously after setUnrestrictedMode(...), so the closed-over state is still stale at the
  // time the PUT body is built (matches the handleToggleCloudPcValidation pattern).
  const handleSaveUnrestrictedMode = useCallback(
    (value: boolean) => saveConfiguration("unrestrictedMode", { unrestrictedMode: value }),
    [saveConfiguration],
  );

  /**
   * Toggle the Windows 365 Cloud PC validation fallback. No consent flow — the backing
   * CloudPC.Read.All permission is an Optional Graph capabilities add-on (grant script);
   * without the grant the backend simply keeps rejecting Cloud PCs with a pointer to the
   * add-on, so enabling early is safe. The toggle moves at once; a failed save moves it back
   * (saveConfiguration re-syncs the gates).
   */
  const handleToggleCloudPcValidation = useCallback(async (newValue: boolean) => {
    formSetters.setValidateCloudPcDevice(newValue);
    await saveConfiguration("autopilotValidation", { validateCloudPcDevice: newValue });
  }, [formSetters, saveConfiguration]);

  /**
   * Toggle Intune Enrollment Validation (field name validateIntuneDeviceBinding). No consent
   * flow — DeviceManagementManagedDevices.Read.All is an Optional Graph capabilities add-on
   * granted with the script; until it is granted no device is admitted through this option
   * (the backend answers 503 Retry-After, like a missing core consent). Moves at once and back on
   * a failed save, like the Cloud PC toggle.
   */
  const handleToggleIntuneDeviceBinding = useCallback(async (newValue: boolean) => {
    formSetters.setValidateIntuneDeviceBinding(newValue);
    await saveConfiguration("autopilotValidation", { validateIntuneDeviceBinding: newValue });
  }, [formSetters, saveConfiguration]);

  // -----------------------------------------------------------------------
  // Admin management handlers
  // -----------------------------------------------------------------------
  const handleAddAdmin = useCallback(async () => {
    const addingApplication = newMemberKind === "application";
    if (!tenantId || !(addingApplication ? looksLikeGuid(newAdminEmail) : newAdminEmail.trim())) return;
    try {
      setAddingAdmin(true);
      setError(null);
      setSuccessMessage(null);

      // A service principal is stored under app:<client-id> and is always read-only (Viewer).
      const body = addingApplication
        ? { applicationId: newAdminEmail.trim(), role: "Viewer", canManageBootstrapTokens: false }
        : { upn: newAdminEmail.trim(), role: newMemberRole, canManageBootstrapTokens: false };
      await fetchOk(api.tenants.admins(tenantId), getAccessToken, {
        method: "POST",
        body: jsonBody<AddTenantAdminRequest>(body),
      });

      trackEvent("admin_member_added", { role: body.role, kind: newMemberKind });
      setSuccessMessage(addingApplication
        ? `Service principal ${newAdminEmail.trim()} added as Viewer.`
        : `${newMemberRole} ${newAdminEmail} added successfully!`);
      setNewAdminEmail("");
      setNewMemberRole("Admin");
      await fetchAdmins();
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error("Error adding admin:", err);
      const msg = apiErrorText(err, "Failed to add admin");
      trackEvent("settings_error", { action: "add_admin", error: msg });
      setError(msg);
    } finally {
      setAddingAdmin(false);
    }
  }, [tenantId, newAdminEmail, newMemberRole, newMemberKind, getAccessToken, fetchAdmins]);

  const handleRemoveAdmin = useCallback(async (adminUpn: string) => {
    if (!tenantId) return;

    try {
      setRemovingAdmin(adminUpn);
      setError(null);
      setSuccessMessage(null);

      await fetchOk(api.tenants.admin(tenantId, adminUpn), getAccessToken, {
        method: "DELETE",
      });

      trackEvent("admin_member_removed");
      setSuccessMessage(`Admin ${adminUpn} removed successfully!`);
      await fetchAdmins();
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error("Error removing admin:", err);
      const msg = apiErrorText(err, "Failed to remove admin");
      trackEvent("settings_error", { action: "remove_admin", error: msg });
      setError(msg);
    } finally {
      setRemovingAdmin(null);
    }
  }, [tenantId, getAccessToken, fetchAdmins]);

  const handleToggleTenantAdmin = useCallback(async (adminUpn: string, isEnabled: boolean) => {
    if (!tenantId) return;
    const action = isEnabled ? "disable" : "enable";

    try {
      setTogglingAdmin(adminUpn);
      setError(null);
      setSuccessMessage(null);

      await fetchOk(api.tenants.adminAction(tenantId, adminUpn, action), getAccessToken, { method: "PATCH" });

      trackEvent("admin_member_toggled", { action });
      setSuccessMessage(`Admin ${adminUpn} ${action}d successfully!`);
      await fetchAdmins();
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error(`Error ${action}ing admin:`, err);
      const msg = apiErrorText(err, `Failed to ${action} admin`);
      trackEvent("settings_error", { action: `${action}_admin`, error: msg });
      setError(msg);
    } finally {
      setTogglingAdmin(null);
    }
  }, [tenantId, getAccessToken, fetchAdmins]);

  const handleUpdatePermissions = useCallback(async (adminUpn: string, role: string, canManageBootstrapTokens: boolean) => {
    if (!tenantId) return;
    try {
      setTogglingAdmin(adminUpn);
      setError(null);
      setSuccessMessage(null);

      await fetchOk(
        api.tenants.adminPermissions(tenantId, adminUpn),
        getAccessToken,
        {
          method: "PATCH",
          body: jsonBody<UpdateMemberPermissionsRequest>({ role, canManageBootstrapTokens }),
        },
      );

      trackEvent("admin_permissions_updated", { role });
      setSuccessMessage(`Permissions for ${adminUpn} updated successfully!`);
      await fetchAdmins();
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error("Error updating permissions:", err);
      const msg = apiErrorText(err, "Failed to update permissions");
      trackEvent("settings_error", { action: "update_permissions", error: msg });
      setError(msg);
    } finally {
      setTogglingAdmin(null);
    }
  }, [tenantId, getAccessToken, fetchAdmins]);

  // -----------------------------------------------------------------------
  // Bootstrap session handlers
  // -----------------------------------------------------------------------
  const createBootstrapSession = useCallback(async (validityHours: number, label: string): Promise<string | null> => {
    if (!tenantId) return null;
    try {
      const data = await fetchJson<{ bootstrapUrl?: string }>(api.bootstrap.sessions(), getAccessToken, {
        method: "POST",
        body: jsonBody<CreateBootstrapSessionRequest>({ tenantId, validityHours, label }),
      });
      trackEvent("bootstrap_session_created", { validityHours });
      await fetchBootstrapSessions();
      return data.bootstrapUrl || null;
    } catch (err) {
      const msg = apiErrorText(err, "Failed to create bootstrap session");
      trackEvent("settings_error", { action: "create_bootstrap", error: msg });
      setError(msg);
      return null;
    }
  }, [tenantId, getAccessToken, fetchBootstrapSessions]);

  const revokeBootstrapSession = useCallback(async (code: string) => {
    if (!tenantId) return;
    try {
      await fetchOk(
        api.bootstrap.session(code, tenantId),
        getAccessToken,
        { method: "DELETE" },
      );
      trackEvent("bootstrap_session_revoked");
      await fetchBootstrapSessions();
    } catch (err) {
      const msg = apiErrorText(err, "Failed to revoke bootstrap session");
      trackEvent("settings_error", { action: "revoke_bootstrap", error: msg });
      setError(msg);
    }
  }, [tenantId, getAccessToken, fetchBootstrapSessions]);

  // -----------------------------------------------------------------------
  // Offboard
  // -----------------------------------------------------------------------
  const handleOffboard = useCallback(async () => {
    if (!tenantId) return;
    try {
      setOffboarding(true);
      setOffboardError(null);

      const body = await fetchJson<OffboardResponse>(api.tenants.offboard(tenantId), getAccessToken, { method: 'DELETE' });
      invalidateCachedAuthFetch(CONFIG_PATH_PREFIX);

      // Backend returns 202 (or 200 for idempotent re-clicks) with the History row pointer
      // and EarliestProcessingAt (cache-drain barrier deadline). Switch the UI into the
      // drain-barrier banner state; the banner's countdown will auto-logout once the
      // barrier elapses (by then the worker has started Phase 2 and the auth pipeline
      // returns 403 via the existing Disabled-flag gate).
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
      setOffboardError(apiErrorText(err, 'Offboard failed'));
    } finally {
      setOffboarding(false);
    }
  }, [tenantId, getAccessToken]);

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

      await fetchOk(api.config.trial(tenantId), getAccessToken, {
        method: "POST",
      });
      invalidateCachedAuthFetch(CONFIG_PATH_PREFIX);

      // Refetch the authoritative edition surface (server-resolved).
      const flags = await fetchJson<TenantFeatureFlagsResponse>(api.config.featureFlags(tenantId), getAccessToken).catch(() => null);
      if (flags) setEditionInfo(parseEditionInfo(flags));
      setSuccessMessage("Pro trial started — all Pro features are now active for 30 days.");
      setTimeout(() => setSuccessMessage(null), 5000);
      trackEvent("ProTrialStarted", { tenantId });
      return true;
    } catch (err) {
      setError(apiErrorText(err, "Failed to start trial"));
      return false;
    } finally {
      setStartingTrial(false);
    }
  }, [tenantId, getAccessToken]);

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
      config, loading, canEditConfig, savingSection, unsavedSections,
      error, setError, successMessage, setSuccessMessage,

      // Every edited value and its setter; the hardware switch below replaces its pair.
      ...form,
      ...formSetters,

      // Edition / trial
      editionInfo, startingTrial, startTrial,
      appHomingFunnelActive, homingFlipped, homingMissingRoles,
      graphAccessRejected, graphAccessAppMissing,

      // Validation
      handleToggleCloudPcValidation,
      handleToggleIntuneDeviceBinding,
      saveValidationGate,
      autopilotConsentInProgress, beginDeviceValidationConsentFlow, detectExistingAccess,

      // Hardware whitelist
      webhookNotifyOnHardwareRejection: effectiveHwRejectionNotify,
      setWebhookNotifyOnHardwareRejection: setHwRejectionNotifyWriteThrough,
      handleSaveHardwareWhitelist,

      // Agent settings and contact
      handleSaveContact,
      handleSaveAgentParameters,
      handleSaveAgentCollectors,

      // Agent analyzers
      newAllowedAccount, setNewAllowedAccount,
      handleSaveAgentAnalyzers,

      // Unrestricted mode
      handleSaveUnrestrictedMode,

      // Notifications
      testingChannelId, testChannelResult,
      handleTestChannel, handleSaveNotifications,

      // SLA Targets
      handleSaveSlaTargets,

      // Diagnostics
      newDiagPath, setNewDiagPath,
      newDiagDesc, setNewDiagDesc,
      handleSaveDiagnostics,

      // Admin management
      admins, loadingAdmins,
      newAdminEmail, setNewAdminEmail,
      newMemberRole, setNewMemberRole,
      newMemberKind, setNewMemberKind,
      addingAdmin, removingAdmin, togglingAdmin,
      handleAddAdmin, handleRemoveAdmin, handleToggleTenantAdmin, handleUpdatePermissions,

      // Bootstrap sessions
      bootstrapSessions, bootstrapLoading,
      fetchBootstrapSessions, createBootstrapSession, revokeBootstrapSession,

      // Data management
      handleSaveDataManagement,

      // Offboarding
      showOffboardDialog, setShowOffboardDialog,
      offboardConfirmText, setOffboardConfirmText,
      offboarding, offboardError, setOffboardError,
      handleOffboard,
      offboardingInProgress, handleDrainBarrierElapsed,

      // Auth
      user, getAccessToken,
  }), [
    config, loading, canEditConfig, savingSection, unsavedSections, error, successMessage,
    form, formSetters,
    editionInfo, startingTrial, startTrial, appHomingFunnelActive, homingFlipped, homingMissingRoles,
    graphAccessRejected, graphAccessAppMissing,
    handleToggleCloudPcValidation, handleToggleIntuneDeviceBinding,
    saveValidationGate, autopilotConsentInProgress, beginDeviceValidationConsentFlow, detectExistingAccess,
    effectiveHwRejectionNotify, setHwRejectionNotifyWriteThrough,
    handleSaveHardwareWhitelist,
    handleSaveContact,
    handleSaveAgentParameters,
    handleSaveAgentCollectors,
    newAllowedAccount,
    handleSaveAgentAnalyzers,
    handleSaveUnrestrictedMode,
    testingChannelId, testChannelResult,
    handleTestChannel, handleSaveNotifications,
    handleSaveSlaTargets,
    newDiagPath, newDiagDesc,
    handleSaveDiagnostics,
    admins, loadingAdmins, newAdminEmail, newMemberRole, newMemberKind, addingAdmin, removingAdmin, togglingAdmin,
    handleAddAdmin, handleRemoveAdmin, handleToggleTenantAdmin, handleUpdatePermissions,
    bootstrapSessions, bootstrapLoading, fetchBootstrapSessions, createBootstrapSession, revokeBootstrapSession,
    handleSaveDataManagement,
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
