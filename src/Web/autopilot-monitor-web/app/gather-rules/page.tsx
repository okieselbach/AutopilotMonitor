"use client";

import { useEffect, useState, useCallback } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useAuth } from "../../contexts/AuthContext";
import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";
import { trackEvent } from "@/lib/appInsights";
import { downloadAsJson, stripInternalFields, bumpVersion } from "@/lib/rulePageHelpers";
import { StatCard } from "@/components/rules/StatCard";
import { RuleFilterBar } from "@/components/rules/RuleFilterBar";
import { EmptyState } from "@/components/rules/EmptyState";
import { FormJsonToggle, JsonModeToggleButtons } from "@/components/rules/FormJsonToggle";
import { useAuthenticatedFetch, useNotificationMessages, useGlobalAdminScope } from "@/hooks";
import { GlobalAdminBanner, globalAdminSubtitle } from "@/components/GlobalAdminBanner";
import { TenantScopeSelector } from "@/components/TenantScopeSelector";
import { GatherRule, NewRuleForm, EMPTY_FORM, CATEGORY_COLORS, withDerivedScopeMode } from "./types";
import { GatherRuleFormFields } from "./components/GatherRuleFormFields";
import { GatherRuleCard } from "./components/GatherRuleCard";

export default function GatherRulesPage() {
  const router = useRouter();

  const { user, getAccessToken } = useAuth();

  const { successMessage, error, showSuccess, showError } = useNotificationMessages();

  const { data: rules, loading, execute: fetchRulesExec, setData: setRules } = useAuthenticatedFetch<GatherRule[]>({
    onError: (err) => showError(err.message),
    onTokenExpired: (err) => showError(err.message),
  });

  const { execute: mutate } = useAuthenticatedFetch({
    onError: (err) => showError(err.message),
    onTokenExpired: (err) => showError(err.message),
  });

  // Filter state
  const [searchQuery, setSearchQuery] = useState("");
  const [categoryFilter, setCategoryFilter] = useState("all");
  const [typeFilter, setTypeFilter] = useState("all");

  // Expanded / editing state
  const [expandedRuleId, setExpandedRuleId] = useState<string | null>(null);
  const [editingRuleId, setEditingRuleId] = useState<string | null>(null);
  const [editForm, setEditForm] = useState<NewRuleForm>({ ...EMPTY_FORM });
  const [saving, setSaving] = useState(false);

  // Create form state
  const [showCreateForm, setShowCreateForm] = useState(false);
  const [newRule, setNewRule] = useState<NewRuleForm>({ ...EMPTY_FORM });
  const [creating, setCreating] = useState(false);

  // JSON mode (create + edit)
  const [jsonModeCreate, setJsonModeCreate] = useState(false);
  const [jsonModeEdit, setJsonModeEdit] = useState(false);
  const [jsonText, setJsonText] = useState("");
  const [jsonError, setJsonError] = useState<string | null>(null);

  // Toggling / deleting state
  const [togglingRule, setTogglingRule] = useState<string | null>(null);
  const [deletingRule, setDeletingRule] = useState<string | null>(null);

  // Unrestricted mode (fetched from tenant config for validation indicators)
  const [unrestrictedMode, setUnrestrictedMode] = useState(false);

  // Global admin tenant scope (tenant list, selector state, override/effective tenant)
  const scope = useGlobalAdminScope();
  const { isGlobalOverride, effectiveTenantId } = scope;
  // Editable only for a real Global Admin (any tenant), or an own-tenant admin viewing their OWN tenant.
  // A read-only Global Reader — and an own-tenant admin viewing a FOREIGN tenant (cross-tenant override) —
  // is read-only. Backend also enforces (rules write is TenantAdminOrGA, cross-tenant blocked for non-GA).
  const isReadOnly = !(user?.isGlobalAdmin || (user?.isTenantAdmin && !isGlobalOverride));

  const fetchRules = useCallback(async () => {
    if (!effectiveTenantId) return;
    const url = isGlobalOverride
      ? api.rules.globalGather(effectiveTenantId)
      : api.rules.gather(effectiveTenantId);
    await fetchRulesExec(
      url,
      undefined,
      { transform: (d) => (d as { rules?: GatherRule[] }).rules || [] }
    );
  }, [effectiveTenantId, isGlobalOverride, fetchRulesExec]);

  useEffect(() => {
    if (effectiveTenantId) {
      fetchRules();
    }
  }, [effectiveTenantId, fetchRules]);

  // Fetch unrestrictedMode from tenant config (for validation indicators).
  // The display flag lives in the member-readable feature-flags endpoint so that
  // Operators/Viewers can load this page without 403'ing on the admin-only full config.
  // GA-override path keeps using globalConfig.tenant (GA-only endpoint).
  useEffect(() => {
    if (!effectiveTenantId) return;
    const fetchConfig = async () => {
      try {
        const url = isGlobalOverride
          ? api.globalConfig.tenant(effectiveTenantId)
          : api.config.featureFlags(effectiveTenantId);
        const response = await authenticatedFetch(url, getAccessToken);
        if (response.ok) {
          const data = await response.json();
          setUnrestrictedMode(data.unrestrictedMode ?? false);
        }
      } catch {
        // Silently default to restricted mode
      }
    };
    fetchConfig();
  }, [effectiveTenantId, isGlobalOverride, getAccessToken]);

  const handleToggleRule = async (rule: GatherRule) => {
    setTogglingRule(rule.ruleId);
    const result = await mutate(
      api.rules.gatherRule(rule.ruleId, effectiveTenantId),
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ enabled: !rule.enabled, isBuiltIn: rule.isBuiltIn, isCommunity: rule.isCommunity }),
      }
    );
    if (result !== null) {
      setRules(prev =>
        (prev || []).map(r =>
          r.ruleId === rule.ruleId ? { ...r, enabled: !r.enabled } : r
        )
      );
      showSuccess(`Rule "${rule.title}" ${!rule.enabled ? "enabled" : "disabled"}`);
    }
    setTogglingRule(null);
  };

  const handleDeleteRule = async (rule: GatherRule) => {
    if (!confirm(`Delete rule "${rule.title}"? This cannot be undone.`)) return;

    setDeletingRule(rule.ruleId);
    const result = await mutate(
      api.rules.gatherRule(rule.ruleId, effectiveTenantId),
      { method: "DELETE" }
    );
    if (result !== null) {
      trackEvent("rule_deleted", { ruleType: "gather" });
      setRules(prev => (prev || []).filter(r => r.ruleId !== rule.ruleId));
      if (expandedRuleId === rule.ruleId) setExpandedRuleId(null);
      showSuccess(`Rule "${rule.title}" deleted`);
    }
    setDeletingRule(null);
  };

  const buildParameters = (form: NewRuleForm): Record<string, string> => {
    const params: Record<string, string> = {};
    if (form.collectorType === "registry") {
      if (form.valueName) params.valueName = form.valueName;
      if (form.listSubkeys) params.listSubkeys = "true";
    }
    if (form.collectorType === "eventlog") {
      if (form.eventId) params.eventId = form.eventId;
      if (form.messageFilter) params.messageFilter = form.messageFilter;
      if (form.maxEntries) params.maxEntries = form.maxEntries;
      if (form.source) params.source = form.source;
    }
    if (form.collectorType === "file") {
      params.readContent = form.readContent ? "true" : "false";
    }
    if (form.collectorType === "logparser") {
      if (form.logPattern) params.pattern = form.logPattern;
      if (form.logFormat && form.logFormat !== "cmtrace") params.format = form.logFormat;
      params.trackPosition = form.trackPosition ? "true" : "false";
      if (form.maxLines) params.maxLines = form.maxLines;
    }
    if (form.collectorType === "json") {
      if (form.jsonPath) params.jsonpath = form.jsonPath;
      if (form.maxResults) params.maxResults = form.maxResults;
    }
    if (form.collectorType === "xml") {
      if (form.xpath) params.xpath = form.xpath;
      if (form.xmlNamespaces) params.namespaces = form.xmlNamespaces;
      if (form.maxResults) params.maxResults = form.maxResults;
    }
    return params;
  };

  const handleCreateRule = async (overrideForm?: NewRuleForm) => {
    const form = overrideForm || newRule;
    if (!form.ruleId || !form.title || !form.target || !form.outputEventType) {
      showError("Rule ID, Title, Target, and Output Event Type are required");
      return;
    }

    if (rulesList.some(r => r.ruleId.toLowerCase() === form.ruleId.toLowerCase())) {
      showError(`A rule with ID "${form.ruleId}" already exists. Please use a unique Rule ID.`);
      return;
    }

    setCreating(true);
    const payload = {
      ruleId: form.ruleId,
      title: form.title,
      description: form.description,
      category: form.category,
      collectorType: form.collectorType,
      target: form.target,
      parameters: buildParameters(form),
      trigger: form.trigger,
      intervalSeconds: form.trigger === "interval" ? form.intervalSeconds : null,
      triggerPhase: form.trigger === "phase_change" ? form.triggerPhase : null,
      triggerEventType: form.trigger === "on_event" ? form.triggerEventType : null,
      activePhases: form.scopeMode === "during" && form.activePhases.length > 0 ? form.activePhases : null,
      activeFromPhase: form.scopeMode === "from" && form.activeFromPhase ? form.activeFromPhase : null,
      emitMode: form.emitMode || null,
      outputEventType: form.outputEventType,
      outputSeverity: form.outputSeverity,
    };

    const result = await mutate(
      api.rules.gather(effectiveTenantId),
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      }
    );
    if (result !== null) {
      trackEvent("rule_created", { ruleType: "gather" });
      showSuccess(`Rule "${form.title}" created`);
      setShowCreateForm(false);
      setNewRule({ ...EMPTY_FORM });
      setJsonModeCreate(false);
      setJsonError(null);
      fetchRules();
    }
    setCreating(false);
  };

  const startEditing = (rule: GatherRule) => {
    // Ensure the card stays expanded when entering edit mode
    setExpandedRuleId(rule.ruleId);
    setEditingRuleId(rule.ruleId);
    setEditForm({
      ruleId: rule.ruleId,
      title: rule.title,
      description: rule.description,
      category: rule.category,
      collectorType: rule.collectorType,
      target: rule.target,
      valueName: rule.parameters?.valueName || "",
      listSubkeys: rule.parameters?.listSubkeys === "true",
      eventId: rule.parameters?.eventId || "",
      messageFilter: rule.parameters?.messageFilter || "",
      maxEntries: rule.parameters?.maxEntries || "",
      source: rule.parameters?.source || "",
      readContent: rule.parameters?.readContent === "true",
      logPattern: rule.parameters?.pattern || "",
      logFormat: rule.parameters?.format || "cmtrace",
      trackPosition: rule.parameters?.trackPosition !== "false",
      maxLines: rule.parameters?.maxLines || "",
      jsonPath: rule.parameters?.jsonpath || "",
      xpath: rule.parameters?.xpath || "",
      xmlNamespaces: rule.parameters?.namespaces || "",
      maxResults: rule.parameters?.maxResults || "",
      trigger: rule.trigger,
      intervalSeconds: rule.intervalSeconds || 60,
      triggerPhase: rule.triggerPhase || "",
      triggerEventType: rule.triggerEventType || "",
      scopeMode: rule.activePhases?.length ? "during" : rule.activeFromPhase ? "from" : "always",
      activePhases: rule.activePhases || [],
      activeFromPhase: rule.activeFromPhase || "",
      // Existing rules without the field keep today's behavior ("always") — the on_change
      // default applies to NEW rules only.
      emitMode: rule.emitMode === "on_change" ? "on_change" : "always",
      outputEventType: rule.outputEventType,
      outputSeverity: rule.outputSeverity,
    });
  };

  const handleSaveEdit = async (rule: GatherRule, overrideForm?: NewRuleForm) => {
    const form = overrideForm || editForm;
    if (!form.title || !form.target || !form.outputEventType) {
      showError("Title, Target, and Output Event Type are required");
      return;
    }

    setSaving(true);
    const payload = {
      title: form.title,
      description: form.description,
      category: form.category,
      collectorType: form.collectorType,
      target: form.target,
      parameters: buildParameters(form),
      trigger: form.trigger,
      intervalSeconds: form.trigger === "interval" ? form.intervalSeconds : null,
      triggerPhase: form.trigger === "phase_change" ? form.triggerPhase : null,
      triggerEventType: form.trigger === "on_event" ? form.triggerEventType : null,
      activePhases: form.scopeMode === "during" && form.activePhases.length > 0 ? form.activePhases : null,
      activeFromPhase: form.scopeMode === "from" && form.activeFromPhase ? form.activeFromPhase : null,
      emitMode: form.emitMode || null,
      outputEventType: form.outputEventType,
      outputSeverity: form.outputSeverity,
      version: bumpVersion(rule.version),
      enabled: rule.enabled,
      author: rule.author,
      createdAt: rule.createdAt,
      // Same wipe class as the toggle bug: the full-replace PUT must carry tags or they vanish.
      tags: rule.tags ?? [],
      isBuiltIn: rule.isBuiltIn ?? false,
      isCommunity: rule.isCommunity ?? false,
    };

    const result = await mutate(
      api.rules.gatherRule(rule.ruleId, effectiveTenantId),
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      }
    );
    if (result !== null) {
      trackEvent("rule_modified", { ruleType: "gather" });
      showSuccess(`Rule "${form.title}" saved`);
      setEditingRuleId(null);
      setJsonModeEdit(false);
      setJsonError(null);
      fetchRules();
    }
    setSaving(false);
  };

  // Computed values
  const rulesList = rules || [];
  const filteredRules = rulesList.filter((rule) => {
    if (searchQuery) {
      const q = searchQuery.toLowerCase().trim();
      if (q.startsWith("#")) {
        const tag = q.slice(1);
        if (tag !== "" && !rule.tags?.some(t => t.toLowerCase().includes(tag))) {
          return false;
        }
      } else if (
        !rule.title.toLowerCase().includes(q) &&
        !rule.ruleId.toLowerCase().includes(q) &&
        !rule.tags?.some(t => t.toLowerCase().includes(q))
      ) {
        return false;
      }
    }
    if (categoryFilter !== "all" && rule.category !== categoryFilter) return false;
    if (typeFilter === "builtin" && !rule.isBuiltIn) return false;
    if (typeFilter === "community" && !rule.isCommunity) return false;
    if (typeFilter === "custom" && (rule.isBuiltIn || rule.isCommunity)) return false;
    return true;
  });

  const totalRules = rulesList.length;
  const activeRules = rulesList.filter((r) => r.enabled).length;
  const builtInCount = rulesList.filter((r) => r.isBuiltIn).length;
  const communityCount = rulesList.filter((r) => r.isCommunity).length;
  const customCount = rulesList.filter((r) => !r.isBuiltIn && !r.isCommunity).length;

  const handleExportSingle = (rule: GatherRule) => {
    downloadAsJson(stripInternalFields(rule), `gather-rule-${rule.ruleId}.json`);
    trackEvent("rules_exported", { ruleType: "gather", scope: "single" });
  };

  const handleExportAll = () => {
    downloadAsJson(filteredRules.map(stripInternalFields), "gather-rules-export.json");
    trackEvent("rules_exported", { ruleType: "gather", scope: "all" });
  };

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        <GlobalAdminBanner show={scope.isGlobalAdmin} delegated={scope.isDelegatedScope} subtitle={globalAdminSubtitle(scope)} />
        {/* Header */}
        <header className="bg-white shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <div className="flex items-center justify-between">
              <div>
                <div>
                  <h1 className="text-2xl font-normal text-gray-900">Gather Rules</h1>
                  <p className="text-sm text-gray-600 mt-1">Manage data collection rules for device enrollment</p>
                </div>
              </div>
              <TenantScopeSelector scope={scope} />
            </div>
          </div>
        </header>

        {/* Main Content */}
        <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          {loading ? (
            <div className="bg-white rounded-lg shadow p-8 text-center">
              <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-indigo-600 mx-auto"></div>
              <p className="mt-4 text-gray-600">Loading gather rules...</p>
            </div>
          ) : (
            <div className="space-y-6">
              {/* Success Message */}
              {successMessage && (
                <div className="bg-green-50 border border-green-200 rounded-lg p-4 flex items-center space-x-3">
                  <svg className="w-5 h-5 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                  <span className="text-green-800 font-medium">{successMessage}</span>
                </div>
              )}

              {/* Error Message */}
              {error && (
                <div className="bg-red-50 border border-red-200 rounded-lg p-4 flex items-center space-x-3">
                  <svg className="w-5 h-5 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                  <span className="text-red-800">{error}</span>
                </div>
              )}

              {/* Community Contribution Hint */}
              <div className="bg-blue-50 border border-blue-200 rounded-lg p-4 flex items-start space-x-3">
                <svg className="w-5 h-5 text-blue-500 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <p className="text-sm text-blue-800">
                  Missing a rule, found a bug, or have an improvement idea? Help make Autopilot Monitor better —{" "}
                  <a href="https://github.com/okieselbach/Autopilot-Monitor/issues" target="_blank" rel="noopener noreferrer" className="font-medium underline hover:text-blue-900">
                    open a GitHub issue
                  </a>.
                </p>
              </div>

              {/* Summary Stats */}
              <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
                <StatCard label="Total Rules" value={totalRules} />
                <StatCard label="Active Rules" value={activeRules} valueColor="text-emerald-600" />
                <StatCard label="Built-in / Community" value={builtInCount + communityCount} valueColor="text-blue-600" />
                <StatCard label="Custom" value={customCount} valueColor="text-purple-600" />
              </div>

              {/* Filter Bar + Create Button */}
              <RuleFilterBar
                searchQuery={searchQuery}
                onSearchChange={setSearchQuery}
                searchPlaceholder="Search by title, rule ID, or #tag..."
                filters={[
                  {
                    label: "Category",
                    value: categoryFilter,
                    onChange: setCategoryFilter,
                    options: [
                      { value: "all", label: "All Categories" },
                      { value: "network", label: "Network" },
                      { value: "identity", label: "Identity" },
                      { value: "apps", label: "Apps" },
                      { value: "device", label: "Device" },
                      { value: "esp", label: "ESP" },
                      { value: "enrollment", label: "Enrollment" },
                    ],
                  },
                  {
                    label: "Type",
                    value: typeFilter,
                    onChange: setTypeFilter,
                    options: [
                      { value: "all", label: "All Types" },
                      { value: "builtin", label: "Built-in" },
                      { value: "community", label: "Community" },
                      { value: "custom", label: "Custom" },
                    ],
                  },
                ]}
                onExportAll={isReadOnly ? undefined : handleExportAll}
                onCreateNew={isReadOnly || isGlobalOverride ? undefined : () => { setShowCreateForm(!showCreateForm); if (showCreateForm) setNewRule({ ...EMPTY_FORM }); }}
                createLabel="Create Custom Rule"
                showCreateForm={showCreateForm && !isGlobalOverride && !isReadOnly}
              />

              {/* Create Custom Rule Form */}
              {showCreateForm && (
                <div className="bg-white rounded-lg shadow">
                  <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-indigo-50 to-purple-50">
                    <div className="flex items-center justify-between">
                      <div className="flex items-center space-x-2">
                        <svg className="w-6 h-6 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
                        </svg>
                        <div>
                          <h2 className="text-xl font-semibold text-gray-900">Create Custom Rule</h2>
                          <p className="text-sm text-gray-500 mt-1">Define a new data collection rule for enrolled devices — <a href="https://docs.autopilotmonitor.com/rules/gather-rules" target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">see documentation</a></p>
                        </div>
                      </div>
                      <JsonModeToggleButtons
                        jsonMode={jsonModeCreate}
                        onToggleMode={(mode) => {
                          if (mode) { setJsonText(JSON.stringify(newRule, null, 2)); }
                          setJsonModeCreate(mode);
                          setJsonError(null);
                        }}
                      />
                    </div>
                  </div>
                  <div className="p-6">
                    <FormJsonToggle
                      jsonMode={jsonModeCreate}
                      onToggleMode={(mode) => {
                        if (mode) { setJsonText(JSON.stringify(newRule, null, 2)); }
                        setJsonModeCreate(mode);
                        setJsonError(null);
                      }}
                      jsonText={jsonText}
                      onJsonTextChange={(text) => { setJsonText(text); setJsonError(null); }}
                      jsonError={jsonError}
                      onApplyJson={() => {
                        try {
                          const parsed = JSON.parse(jsonText) as NewRuleForm;
                          if (!parsed.ruleId && !parsed.title) throw new Error("JSON must include at least ruleId and title");
                          // Rule-shaped JSON carries activePhases/activeFromPhase but no scopeMode
                          // — derive it so the scope fields survive into the create payload.
                          setNewRule(withDerivedScopeMode({ ...EMPTY_FORM, ...parsed }));
                          setJsonModeCreate(false);
                          setJsonError(null);
                        } catch (e) {
                          setJsonError(e instanceof Error ? e.message : "Invalid JSON");
                        }
                      }}
                      textareaRows={20}
                      description={<>Edit the rule as JSON. All fields are supported including <code className="bg-gray-100 px-1 rounded text-xs">parameters</code> for collector-specific options.</>}
                    >
                      <GatherRuleFormFields form={newRule} setForm={setNewRule} showRuleId={true} unrestrictedMode={unrestrictedMode} existingRuleIds={rulesList.map(r => r.ruleId)} />
                    </FormJsonToggle>

                    {/* Action Buttons */}
                    <div className="flex items-center justify-end space-x-3 pt-4 mt-5 border-t border-gray-200">
                      <button
                        onClick={() => {
                          setShowCreateForm(false);
                          setJsonModeCreate(false);
                          setJsonError(null);
                          setNewRule({ ...EMPTY_FORM });
                        }}
                        disabled={creating}
                        className="px-6 py-2 border border-gray-300 rounded-lg text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm font-medium"
                      >
                        Cancel
                      </button>
                      <button
                        onClick={() => {
                          if (jsonModeCreate) {
                            try {
                              const parsed = JSON.parse(jsonText) as NewRuleForm;
                              setJsonError(null);
                              handleCreateRule(withDerivedScopeMode({ ...EMPTY_FORM, ...parsed }));
                            } catch (e) {
                              setJsonError(e instanceof Error ? e.message : "Invalid JSON");
                            }
                          } else {
                            handleCreateRule();
                          }
                        }}
                        disabled={creating}
                        className="px-6 py-2 bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2 text-sm font-medium"
                      >
                        {creating ? (
                          <>
                            <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                            <span>Creating...</span>
                          </>
                        ) : (
                          <span>Save Rule</span>
                        )}
                      </button>
                    </div>
                  </div>
                </div>
              )}

              {/* Rules List */}
              <div className="space-y-3">
                <div className="flex items-center space-x-2 px-1">
                  <svg className="w-5 h-5 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2" />
                  </svg>
                  <span className="text-sm text-gray-500">
                    {filteredRules.length} rule{filteredRules.length !== 1 ? "s" : ""} found
                    {(searchQuery || categoryFilter !== "all" || typeFilter !== "all") && " (filtered)"}
                  </span>
                </div>

                {filteredRules.length === 0 ? (
                  <EmptyState
                    message="No rules match your filters."
                    onClearFilters={() => { setSearchQuery(""); setCategoryFilter("all"); setTypeFilter("all"); }}
                    showClearButton={!!(searchQuery || categoryFilter !== "all" || typeFilter !== "all")}
                  />
                ) : (
                  filteredRules.map((rule) => (
                    <GatherRuleCard
                      key={rule.ruleId}
                      rule={rule}
                      isExpanded={expandedRuleId === rule.ruleId}
                      isEditing={editingRuleId === rule.ruleId}
                      editForm={editForm}
                      setEditForm={setEditForm}
                      saving={saving}
                      togglingRule={togglingRule}
                      deletingRule={deletingRule}
                      jsonModeEdit={jsonModeEdit}
                      jsonText={jsonText}
                      jsonError={jsonError}
                      onToggle={() => handleToggleRule(rule)}
                      onExpand={() => {
                        if (editingRuleId === rule.ruleId) return;
                        setExpandedRuleId(expandedRuleId === rule.ruleId ? null : rule.ruleId);
                        if (expandedRuleId === rule.ruleId && editingRuleId === rule.ruleId) {
                          setEditingRuleId(null);
                        }
                      }}
                      onStartEditing={() => startEditing(rule)}
                      onCancelEditing={() => { setEditingRuleId(null); setJsonModeEdit(false); setJsonError(null); }}
                      onSaveEdit={handleSaveEdit}
                      onDelete={() => handleDeleteRule(rule)}
                      onExport={() => handleExportSingle(rule)}
                      onToggleJsonMode={(mode) => {
                        if (mode) { setJsonText(JSON.stringify(editForm, null, 2)); }
                        setJsonModeEdit(mode);
                        setJsonError(null);
                      }}
                      onJsonTextChange={(text) => { setJsonText(text); setJsonError(null); }}
                      onApplyJson={() => {
                        try {
                          const parsed = JSON.parse(jsonText) as NewRuleForm;
                          if (!parsed.title) throw new Error("JSON must include title");
                          setEditForm(withDerivedScopeMode({ ...editForm, ...parsed }));
                          setJsonModeEdit(false);
                          setJsonError(null);
                        } catch (e) {
                          setJsonError(e instanceof Error ? e.message : "Invalid JSON");
                        }
                      }}
                      readOnly={isReadOnly}
                      unrestrictedMode={unrestrictedMode}
                    />
                  ))
                )}
              </div>
            </div>
          )}
        </main>
      </div>
    </ProtectedRoute>
  );
}
