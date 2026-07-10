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

import { isTemplateRule } from "@/lib/analyzeRuleTabs";
import { AnalyzeRule, RuleForm, EMPTY_FORM, EMPTY_CONDITION, ruleToForm } from "./types";
import AnalyzeRuleFormFields from "./components/AnalyzeRuleFormFields";
import AnalyzeRuleCard from "./components/AnalyzeRuleCard";
import TemplateConfigModal from "./components/TemplateConfigModal";

export default function AnalyzeRulesPage() {
  const router = useRouter();

  const { user, getAccessToken } = useAuth();

  const { successMessage, error, showSuccess, showError } = useNotificationMessages();

  const { data: rules, loading, execute: fetchRulesExec, setData: setRules } = useAuthenticatedFetch<AnalyzeRule[]>({
    onError: (err) => showError(err.message),
    onTokenExpired: (err) => showError(err.message),
  });

  const { execute: mutate } = useAuthenticatedFetch({
    onError: (err) => showError(err.message),
    onTokenExpired: (err) => showError(err.message),
  });

  // Filter state
  const [searchQuery, setSearchQuery] = useState("");
  const [severityFilter, setSeverityFilter] = useState("all");
  const [categoryFilter, setCategoryFilter] = useState("all");
  const [typeFilter, setTypeFilter] = useState("all");

  // Active tab: "rules" (all real rules) vs "templates" (copy blueprints)
  const [activeTab, setActiveTab] = useState<"rules" | "templates">("rules");

  // Expanded / editing state
  const [expandedRuleId, setExpandedRuleId] = useState<string | null>(null);
  const [editingRuleId, setEditingRuleId] = useState<string | null>(null);
  const [editForm, setEditForm] = useState<RuleForm>({ ...EMPTY_FORM });
  const [saving, setSaving] = useState(false);

  // Create form state
  const [showCreateForm, setShowCreateForm] = useState(false);
  const [newRule, setNewRule] = useState<RuleForm>({ ...EMPTY_FORM });
  const [creating, setCreating] = useState(false);

  // JSON mode (create + edit)
  const [jsonModeCreate, setJsonModeCreate] = useState(false);
  const [jsonModeEdit, setJsonModeEdit] = useState(false);
  const [jsonText, setJsonText] = useState("");
  const [jsonError, setJsonError] = useState<string | null>(null);

  // Toggling / deleting state
  const [togglingRuleId, setTogglingRuleId] = useState<string | null>(null);
  const [deletingRuleId, setDeletingRuleId] = useState<string | null>(null);

  // Template modal state
  const [configureTemplateRule, setConfigureTemplateRule] = useState<AnalyzeRule | null>(null);
  const [creatingFromTemplate, setCreatingFromTemplate] = useState(false);

  // Rule telemetry stats (hit rates)
  const [ruleStatsMap, setRuleStatsMap] = useState<Record<string, { hitRate: number; fireCount: number }>>({});

  // Tenant notification channels (for the per-rule notify target selector)
  const [tenantChannels, setTenantChannels] = useState<{ id: string; name: string }[]>([]);

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
      ? api.rules.globalAnalyze(effectiveTenantId)
      : api.rules.analyze();
    await fetchRulesExec(
      url,
      undefined,
      { transform: (d) => { const r = d as { success?: boolean; rules?: AnalyzeRule[] }; return r.success && Array.isArray(r.rules) ? r.rules : []; } }
    );
  }, [effectiveTenantId, isGlobalOverride, fetchRulesExec]);

  useEffect(() => {
    fetchRules();
  }, [fetchRules]);

  // Fetch rule telemetry stats (hit rates for last 30 days)
  useEffect(() => {
    if (!effectiveTenantId) return;
    const fetchStats = async () => {
      try {
        const statsUrl = scope.routeGlobal
          ? api.metrics.globalRuleStats(undefined, undefined, "analyze", effectiveTenantId)
          : api.metrics.ruleStats(undefined, undefined, "analyze");
        const response = await authenticatedFetch(statsUrl, getAccessToken);
        if (response.ok) {
          const data = await response.json();
          const map: Record<string, { hitRate: number; fireCount: number }> = {};
          if (data.rules && Array.isArray(data.rules)) {
            for (const r of data.rules) {
              map[r.ruleId] = { hitRate: r.hitRate ?? 0, fireCount: r.fireCount ?? 0 };
            }
          }
          setRuleStatsMap(map);
        }
      } catch {
        // Non-critical: don't break the page if stats fail to load
      }
    };
    fetchStats();
  }, [effectiveTenantId, scope.routeGlobal, getAccessToken]);

  // Fetch the tenant's notification channels (id + name) for the per-rule notify selector.
  // Mirrors the backend legacy synthesis: a non-migrated tenant with a single webhook shows
  // one "Default" channel under the stable "legacy" id. Redacted configs (read-only viewers)
  // still expose channel ids/names — only URLs/headers are masked.
  useEffect(() => {
    if (!effectiveTenantId) return;
    const fetchChannels = async () => {
      try {
        const response = await authenticatedFetch(api.config.tenant(effectiveTenantId), getAccessToken);
        if (!response.ok) return;
        const data = await response.json();
        const cfg = (data?.config ?? data) as {
          notificationChannelsJson?: string;
          webhookUrl?: string;
          webhookProviderType?: number;
          teamsWebhookUrl?: string;
        };
        if (cfg?.notificationChannelsJson) {
          try {
            const parsed = JSON.parse(cfg.notificationChannelsJson);
            if (Array.isArray(parsed)) {
              setTenantChannels(parsed
                .filter((c) => c && c.id && c.enabled !== false)
                .map((c) => ({ id: String(c.id), name: String(c.name || c.id) })));
              return;
            }
          } catch { /* malformed → fall through to legacy check */ }
        }
        if ((cfg?.webhookUrl && cfg?.webhookProviderType) || cfg?.teamsWebhookUrl) {
          setTenantChannels([{ id: "legacy", name: "Default" }]);
        } else {
          setTenantChannels([]);
        }
      } catch {
        // Non-critical: the notify selector simply shows "no channels configured".
      }
    };
    fetchChannels();
  }, [effectiveTenantId, getAccessToken]);

  // Toggle rule enabled/disabled
  const handleToggleRule = async (rule: AnalyzeRule) => {
    // Intercept: if this is a template rule being enabled, open the config modal instead
    if ((rule.templateVariables?.length ?? 0) > 0 && !rule.enabled) {
      setConfigureTemplateRule(rule);
      return;
    }

    setTogglingRuleId(rule.ruleId);
    const result = await mutate(
      api.rules.analyzeRule(rule.ruleId),
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...rule, enabled: !rule.enabled }),
      }
    );
    if (result !== null) {
      setRules((prev) =>
        (prev || []).map((r) => (r.ruleId === rule.ruleId ? { ...r, enabled: !r.enabled } : r))
      );
      showSuccess(`Rule "${rule.title}" ${!rule.enabled ? "enabled" : "disabled"} successfully!`);
    }
    setTogglingRuleId(null);
  };

  // Toggle the "mark session as failed" (KO-criterion) override for a rule.
  // Cycles between: default (unset) → opt-in (true) → opt-out (false) → default.
  // Built-in rules' definitions ship a `markSessionAsFailedDefault`; the tenant
  // state only stores an explicit override when it differs from the default.
  const handleToggleMarkAsFailed = async (rule: AnalyzeRule) => {
    const currentEffective = rule.markSessionAsFailed ?? rule.markSessionAsFailedDefault ?? false;
    const nextEffective = !currentEffective;
    // Write the override only when it diverges from the rule default; otherwise clear it
    // so future default changes propagate.
    const nextOverride: boolean | null =
      nextEffective === (rule.markSessionAsFailedDefault ?? false) ? null : nextEffective;

    setTogglingRuleId(rule.ruleId);
    const result = await mutate(
      api.rules.analyzeRule(rule.ruleId),
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...rule, markSessionAsFailed: nextOverride }),
      }
    );
    if (result !== null) {
      setRules((prev) =>
        (prev || []).map((r) => (r.ruleId === rule.ruleId ? { ...r, markSessionAsFailed: nextOverride } : r))
      );
      showSuccess(`Rule "${rule.title}" ${nextEffective ? "set as KO criterion (session will be marked failed)" : "will no longer mark session as failed"}`);
    }
    setTogglingRuleId(null);
  };

  // Update the rule-level channel-notification override + channel targets.
  // Same override semantics as the KO criterion: store an explicit override only when it
  // diverges from the rule default, so future default changes propagate.
  const handleUpdateNotify = async (rule: AnalyzeRule, notify: boolean, channelIds: string[]) => {
    const nextOverride: boolean | null = notify === (rule.notifyDefault ?? false) ? null : notify;

    setTogglingRuleId(rule.ruleId);
    const result = await mutate(
      api.rules.analyzeRule(rule.ruleId),
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...rule, notify: nextOverride, notifyChannelIds: channelIds }),
      }
    );
    if (result !== null) {
      setRules((prev) =>
        (prev || []).map((r) => (r.ruleId === rule.ruleId ? { ...r, notify: nextOverride, notifyChannelIds: channelIds } : r))
      );
      showSuccess(notify
        ? (channelIds.length > 0
          ? `Rule "${rule.title}" now notifies ${channelIds.length} channel(s) when it fires`
          : `Notification enabled for "${rule.title}" — select at least one channel`)
        : `Channel notification disabled for "${rule.title}"`);
    }
    setTogglingRuleId(null);
  };

  // Delete custom rule
  const handleDeleteRule = async (rule: AnalyzeRule) => {
    if (!confirm(`Are you sure you want to delete the rule "${rule.title}"? This action cannot be undone.`)) {
      return;
    }

    setDeletingRuleId(rule.ruleId);
    const result = await mutate(
      api.rules.analyzeRule(rule.ruleId),
      { method: "DELETE" }
    );
    if (result !== null) {
      trackEvent("rule_deleted", { ruleType: "analyze" });
      setRules((prev) => (prev || []).filter((r) => r.ruleId !== rule.ruleId));
      if (expandedRuleId === rule.ruleId) setExpandedRuleId(null);
      showSuccess(`Rule "${rule.title}" deleted successfully!`);
    }
    setDeletingRuleId(null);
  };

  // Create custom rule
  const handleCreateRule = async (formOverride?: RuleForm) => {
    const form = formOverride ?? newRule;
    if (!form.ruleId.trim() || !form.title.trim()) {
      showError("Rule ID and Title are required.");
      return;
    }

    if (rulesList.some(r => r.ruleId.toLowerCase() === form.ruleId.trim().toLowerCase())) {
      showError(`A rule with ID "${form.ruleId.trim()}" already exists. Please use a unique Rule ID.`);
      return;
    }

    setCreating(true);
    const payload = {
      ruleId: form.ruleId.trim(),
      title: form.title.trim(),
      description: form.description.trim(),
      severity: form.severity,
      category: form.category,
      trigger: form.trigger,
      explanation: form.explanation.trim(),
      baseConfidence: form.baseConfidence,
      confidenceThreshold: form.confidenceThreshold,
      preconditions: form.preconditions.filter(p => p.eventType.trim() && p.operator.trim()),
      conditions: form.conditions.filter(c => c.signal.trim()),
      confidenceFactors: form.confidenceFactors.filter(f => f.signal.trim()),
      remediation: form.remediation.filter(r => r.title.trim()),
      relatedDocs: form.relatedDocs.filter(d => d.title.trim() && d.url.trim()),
    };

    const result = await mutate(
      api.rules.analyze(),
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      }
    );
    if (result !== null) {
      trackEvent("rule_created", { ruleType: "analyze" });
      showSuccess(`Rule "${form.title}" created successfully!`);
      setNewRule({ ...EMPTY_FORM, conditions: [{ ...EMPTY_CONDITION }] });
      setShowCreateForm(false);
      await fetchRules();
    }
    setCreating(false);
  };

  // Start editing
  const startEditing = (rule: AnalyzeRule) => {
    setEditingRuleId(rule.ruleId);
    setEditForm(ruleToForm(rule));
  };

  // Save edited rule
  const handleSaveEdit = async (rule: AnalyzeRule, formOverride?: RuleForm) => {
    const form = formOverride ?? editForm;
    if (!form.title.trim()) {
      showError("Title is required.");
      return;
    }

    setSaving(true);
    const payload = {
      ...rule,
      title: form.title.trim(),
      description: form.description.trim(),
      severity: form.severity,
      category: form.category,
      trigger: form.trigger,
      explanation: form.explanation.trim(),
      baseConfidence: form.baseConfidence,
      confidenceThreshold: form.confidenceThreshold,
      preconditions: form.preconditions.filter(p => p.eventType.trim() && p.operator.trim()),
      conditions: form.conditions.filter(c => c.signal.trim()),
      confidenceFactors: form.confidenceFactors.filter(f => f.signal.trim()),
      remediation: form.remediation.filter(r => r.title.trim()),
      relatedDocs: form.relatedDocs.filter(d => d.title.trim() && d.url.trim()),
      author: user?.displayName || user?.upn || rule.author,
      version: bumpVersion(rule.version),
    };

    const result = await mutate(
      api.rules.analyzeRule(rule.ruleId),
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      }
    );
    if (result !== null) {
      trackEvent("rule_modified", { ruleType: "analyze" });
      setEditingRuleId(null);
      showSuccess(`Rule "${editForm.title}" updated successfully!`);
      await fetchRules();
    }
    setSaving(false);
  };

  // Create custom rule from template
  const handleCreateFromTemplate = async (variables: Record<string, string>) => {
    if (!configureTemplateRule) return;
    setCreatingFromTemplate(true);
    const result = await mutate(
      api.rules.analyzeRuleFromTemplate(configureTemplateRule.ruleId),
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(variables),
      }
    );
    if (result !== null) {
      const newRuleId = `${configureTemplateRule.ruleId}-CUSTOM`;
      trackEvent("rule_created_from_template", { ruleType: "analyze", templateRuleId: configureTemplateRule.ruleId });
      showSuccess(`Custom rule created from "${configureTemplateRule.title}" and enabled — see the Rules tab.`);
      setConfigureTemplateRule(null);
      await fetchRules();
      // Surface the new custom rule where it lives: switch to the Rules tab and expand it.
      setActiveTab("rules");
      setExpandedRuleId(newRuleId);
    }
    setCreatingFromTemplate(false);
  };

  const rulesList = rules || [];

  // Map: templateRuleId -> custom copy ruleId (for template rules that already have a tenant copy)
  const templateCopyMap = new Map<string, string>();
  for (const r of rulesList) {
    if (r.derivedFromTemplateRuleId) {
      templateCopyMap.set(r.derivedFromTemplateRuleId, r.ruleId);
    }
  }

  // Filter rules
  const filteredRules = rulesList.filter((rule) => {
    const matchesSearch =
      searchQuery === "" ||
      (() => {
        const q = searchQuery.toLowerCase().trim();
        if (q.startsWith("#")) {
          const tag = q.slice(1);
          return tag === "" || rule.tags?.some(t => t.toLowerCase().includes(tag));
        }
        return (
          rule.title.toLowerCase().includes(q) ||
          rule.ruleId.toLowerCase().includes(q) ||
          rule.tags?.some(t => t.toLowerCase().includes(q))
        );
      })();

    const matchesSeverity =
      severityFilter === "all" || rule.severity.toLowerCase() === severityFilter.toLowerCase();

    const matchesCategory =
      categoryFilter === "all" || rule.category.toLowerCase() === categoryFilter.toLowerCase();

    // Tab membership: templates live in their own tab; everything else (incl. custom
    // copies derived from a template) is a "real" rule.
    const ruleIsTemplate = isTemplateRule(rule);
    const matchesTab = activeTab === "templates" ? ruleIsTemplate : !ruleIsTemplate;

    // Type filter only applies in the Rules tab (it's hidden in the Templates tab).
    const matchesType =
      activeTab === "templates" ||
      typeFilter === "all" ||
      (typeFilter === "builtin" && rule.isBuiltIn && !rule.isCommunity) ||
      (typeFilter === "community" && rule.isCommunity) ||
      (typeFilter === "custom" && !rule.isBuiltIn && !rule.isCommunity);

    return matchesTab && matchesSearch && matchesSeverity && matchesCategory && matchesType;
  });

  // Total template count (unfiltered) drives the tab badge + whether the tab bar shows at all.
  const templateCount = rulesList.filter(isTemplateRule).length;
  const ruleTabCount = rulesList.length - templateCount;
  const showTabs = templateCount > 0;

  // Summary stats
  const totalRules = rulesList.length;
  const activeRules = rulesList.filter((r) => r.enabled).length;
  const criticalCount = rulesList.filter((r) => r.severity.toLowerCase() === "critical").length;
  const highCount = rulesList.filter((r) => r.severity.toLowerCase() === "high").length;
  const warningCount = rulesList.filter((r) => r.severity.toLowerCase() === "warning").length;
  const infoCount = rulesList.filter((r) => r.severity.toLowerCase() === "info").length;

  const uniqueCategories = Array.from(new Set(rulesList.map((r) => r.category.toLowerCase())));

  const handleExportSingle = (rule: AnalyzeRule) => {
    const cleaned = stripInternalFields(rule);
    downloadAsJson({ "$schema": "../schema/analyze-rule.schema.json", ...cleaned }, `${rule.ruleId}.json`);
    trackEvent("rules_exported", { ruleType: "analyze", scope: "single" });
  };

  const handleExportAll = () => {
    const cleaned = filteredRules.map(stripInternalFields);
    downloadAsJson(cleaned, "analyze-rules-export.json");
    trackEvent("rules_exported", { ruleType: "analyze", scope: "all" });
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
                  <h1 className="text-2xl font-normal text-gray-900">Analyze Rules</h1>
                  <p className="text-sm text-gray-600 mt-1">Manage event analysis rules for issue detection</p>
                </div>
              </div>
              <TenantScopeSelector scope={scope} />
            </div>
          </div>
        </header>

        {/* Main Content */}
        <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          {/* Success Message */}
          {successMessage && (
            <div className="mb-6 bg-green-50 border border-green-200 rounded-lg p-4 flex items-center space-x-3">
              <svg className="w-5 h-5 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" /></svg>
              <span className="text-green-800 font-medium">{successMessage}</span>
            </div>
          )}

          {/* Error Message */}
          {error && (
            <div className="mb-6 bg-red-50 border border-red-200 rounded-lg p-4 flex items-center space-x-3">
              <svg className="w-5 h-5 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" /></svg>
              <span className="text-red-800">{error}</span>
            </div>
          )}

          {loading ? (
            <div className="bg-white rounded-lg shadow p-8 text-center">
              <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-indigo-600 mx-auto"></div>
              <p className="mt-4 text-gray-600">Loading analyze rules...</p>
            </div>
          ) : (
            <div className="space-y-6">
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
              <div className="grid grid-cols-2 md:grid-cols-6 gap-4">
                <StatCard label="Total" value={totalRules} />
                <StatCard label="Active" value={activeRules} valueColor="text-green-600" />
                <StatCard label="Critical" value={criticalCount} borderColor="border-red-400" valueColor="text-red-600" />
                <StatCard label="High" value={highCount} borderColor="border-orange-400" valueColor="text-orange-600" />
                <StatCard label="Warning" value={warningCount} borderColor="border-yellow-400" valueColor="text-yellow-600" />
                <StatCard label="Info" value={infoCount} borderColor="border-blue-400" valueColor="text-blue-600" />
              </div>

              {/* Rule Telemetry (only shown when data is available) */}
              {Object.keys(ruleStatsMap).length > 0 && (() => {
                const statsEntries = Object.entries(ruleStatsMap)
                  .filter(([, s]) => s.fireCount > 0)
                  .sort((a, b) => b[1].fireCount - a[1].fireCount);
                const totalFires = statsEntries.reduce((sum, [, s]) => sum + s.fireCount, 0);
                const topRules = statsEntries.slice(0, 5);
                const rulesList = rules || [];

                return (
                  <div className="bg-white rounded-lg shadow border border-indigo-200 p-4">
                    <div className="flex items-center justify-between mb-3">
                      <h3 className="text-sm font-semibold text-gray-700">Rule Telemetry (last 30 days)</h3>
                      <span className="text-xs text-gray-500">{totalFires} total fires across {statsEntries.length} rules</span>
                    </div>
                    {topRules.length === 0 ? (
                      <p className="text-sm text-gray-400">No rules have fired in the last 30 days.</p>
                    ) : (
                      <div className="space-y-2">
                        {topRules.map(([ruleId, stat]) => {
                          const rule = rulesList.find(r => r.ruleId === ruleId);
                          const barWidth = totalFires > 0 ? Math.max(4, Math.round((stat.fireCount / totalFires) * 100)) : 0;
                          return (
                            <button
                              key={ruleId}
                              onClick={() => {
                                setExpandedRuleId(ruleId);
                                setTimeout(() => {
                                  document.getElementById(`rule-card-${ruleId}`)?.scrollIntoView({ behavior: "smooth", block: "center" });
                                }, 100);
                              }}
                              className="w-full text-left group"
                            >
                              <div className="flex items-center gap-3">
                                <div className="flex-1 min-w-0">
                                  <div className="flex items-center gap-2 mb-0.5">
                                    <span className="text-xs font-mono text-gray-400">{ruleId}</span>
                                    <span className="text-sm text-gray-800 truncate">{rule?.title ?? ruleId}</span>
                                  </div>
                                  <div className="w-full h-1.5 bg-gray-100 rounded-full overflow-hidden">
                                    <div
                                      className={`h-full rounded-full ${
                                        stat.hitRate >= 20 ? "bg-red-500" :
                                        stat.hitRate >= 5 ? "bg-amber-500" : "bg-indigo-400"
                                      }`}
                                      style={{ width: `${barWidth}%` }}
                                    />
                                  </div>
                                </div>
                                <div className="flex items-center gap-3 flex-shrink-0 text-right">
                                  <span className="text-sm font-semibold text-gray-700">{stat.fireCount}x</span>
                                  <span className={`text-xs font-medium px-1.5 py-0.5 rounded ${
                                    stat.hitRate >= 20 ? "bg-red-50 text-red-700" :
                                    stat.hitRate >= 5 ? "bg-amber-50 text-amber-700" :
                                    "bg-gray-50 text-gray-600"
                                  }`}>
                                    {stat.hitRate}% hit rate
                                  </span>
                                </div>
                              </div>
                            </button>
                          );
                        })}
                        {statsEntries.length > 5 && (
                          <p className="text-xs text-gray-400 pt-1">+ {statsEntries.length - 5} more rules with hits</p>
                        )}
                      </div>
                    )}
                  </div>
                );
              })()}

              {/* Tab bar — only shown when at least one template exists. Keeps the page
                  unchanged for tenants without templates. */}
              {showTabs && (
                <div className="border-b border-gray-200">
                  <nav className="-mb-px flex space-x-6" aria-label="Rule tabs">
                    {([
                      { key: "rules" as const, label: "Rules", count: ruleTabCount },
                      { key: "templates" as const, label: "Templates", count: templateCount },
                    ]).map((tab) => (
                      <button
                        key={tab.key}
                        onClick={() => { setActiveTab(tab.key); if (tab.key === "templates") setShowCreateForm(false); }}
                        className={`whitespace-nowrap py-3 px-1 border-b-2 text-sm font-medium transition-colors ${
                          activeTab === tab.key
                            ? "border-indigo-500 text-indigo-600"
                            : "border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300"
                        }`}
                      >
                        {tab.label}
                        <span className={`ml-2 inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${
                          activeTab === tab.key ? "bg-indigo-100 text-indigo-700" : "bg-gray-100 text-gray-600"
                        }`}>
                          {tab.count}
                        </span>
                      </button>
                    ))}
                  </nav>
                </div>
              )}

              {/* Templates tab intro */}
              {activeTab === "templates" && (
                <div className="bg-amber-50 border border-amber-200 rounded-lg p-4 flex items-start space-x-3">
                  <svg className="w-5 h-5 text-amber-500 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                  <p className="text-sm text-amber-800">
                    These are <span className="font-semibold">copy templates</span>, not directly-active rules. Enabling one prompts for your
                    environment-specific values and creates an editable custom rule for your tenant. The created rule then appears in the <span className="font-semibold">Rules</span> tab.
                  </p>
                </div>
              )}

              {/* Filter Bar + Create Button */}
              <RuleFilterBar
                searchQuery={searchQuery}
                onSearchChange={setSearchQuery}
                searchPlaceholder="Search by title, rule ID, or #tag..."
                filters={[
                  {
                    label: "Severity",
                    value: severityFilter,
                    onChange: setSeverityFilter,
                    options: [
                      { value: "all", label: "All Severities" },
                      { value: "critical", label: "Critical" },
                      { value: "high", label: "High" },
                      { value: "warning", label: "Warning" },
                      { value: "info", label: "Info" },
                    ],
                  },
                  {
                    label: "Category",
                    value: categoryFilter,
                    onChange: setCategoryFilter,
                    options: [
                      { value: "all", label: "All Categories" },
                      ...uniqueCategories.map((cat) => ({ value: cat, label: cat.charAt(0).toUpperCase() + cat.slice(1) })),
                    ],
                  },
                  // Type filter is meaningless in the Templates tab → hide it there.
                  ...(activeTab === "templates" ? [] : [{
                    label: "Type",
                    value: typeFilter,
                    onChange: setTypeFilter,
                    options: [
                      { value: "all", label: "All Types" },
                      { value: "builtin", label: "Built-in" },
                      { value: "community", label: "Community" },
                      { value: "custom", label: "Custom" },
                    ],
                  }]),
                ]}
                onExportAll={isReadOnly ? undefined : handleExportAll}
                onCreateNew={isReadOnly || isGlobalOverride || activeTab === "templates" ? undefined : () => { setShowCreateForm(!showCreateForm); if (showCreateForm) setNewRule({ ...EMPTY_FORM, conditions: [{ ...EMPTY_CONDITION }] }); }}
                createLabel="Create Custom Rule"
                showCreateForm={showCreateForm && !isGlobalOverride && !isReadOnly}
              />

              {/* Create Custom Rule Form */}
              {showCreateForm && (
                <div className="bg-white rounded-lg shadow">
                  <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-indigo-50 to-purple-50">
                    <div className="flex items-center justify-between">
                      <div className="flex items-center space-x-2">
                        <svg className="w-6 h-6 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6v6m0 0v6m0-6h6m-6 0H6" /></svg>
                        <div>
                          <h2 className="text-xl font-semibold text-gray-900">Create Custom Analyze Rule</h2>
                          <p className="text-sm text-gray-500 mt-1">Define conditions and confidence scoring for issue detection — <a href="https://docs.autopilotmonitor.com/rules/analyze-rules" target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">see documentation</a></p>
                        </div>
                      </div>
                      {/* JSON Mode Toggle */}
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
                          const parsed = JSON.parse(jsonText) as RuleForm;
                          if (!parsed.ruleId && !parsed.title) throw new Error("JSON must include at least ruleId and title");
                          setNewRule({ ...EMPTY_FORM, ...parsed });
                          setJsonModeCreate(false);
                          setJsonError(null);
                        } catch (e) {
                          setJsonError(e instanceof Error ? e.message : "Invalid JSON");
                        }
                      }}
                      textareaRows={30}
                      description={<>Edit the rule as JSON. All fields are supported including <code className="bg-gray-100 px-1 rounded text-xs">event_correlation</code> condition properties.</>}
                    >
                      <AnalyzeRuleFormFields form={newRule} setForm={setNewRule} showRuleId={true} existingRuleIds={rulesList.map(r => r.ruleId)} />
                    </FormJsonToggle>
                    <div className="flex items-center justify-end space-x-3 pt-4 mt-5 border-t border-gray-200">
                      <button onClick={() => { setShowCreateForm(false); setJsonModeCreate(false); setJsonError(null); setNewRule({ ...EMPTY_FORM, conditions: [{ ...EMPTY_CONDITION }] }); }} disabled={creating} className="px-6 py-2 border border-gray-300 rounded-lg text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm font-medium">Cancel</button>
                      <button onClick={() => {
                        if (jsonModeCreate) {
                          try {
                            const parsed = JSON.parse(jsonText) as RuleForm;
                            setJsonError(null);
                            handleCreateRule({ ...EMPTY_FORM, ...parsed });
                          } catch (e) {
                            setJsonError(e instanceof Error ? e.message : "Invalid JSON");
                          }
                        } else {
                          handleCreateRule();
                        }
                      }} disabled={creating} className="px-6 py-2 bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2 text-sm font-medium">
                        {creating ? (<><div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div><span>Creating...</span></>) : (<span>Save Rule</span>)}
                      </button>
                    </div>
                  </div>
                </div>
              )}

              {/* Rules List */}
              {filteredRules.length === 0 ? (
                <EmptyState
                  message={
                    rulesList.length === 0
                      ? "No analyze rules found."
                      : activeTab === "templates"
                        ? "No templates match your current filters."
                        : "No rules match your current filters."
                  }
                  onClearFilters={() => { setSearchQuery(""); setSeverityFilter("all"); setCategoryFilter("all"); setTypeFilter("all"); }}
                  showClearButton={!!(searchQuery || severityFilter !== "all" || categoryFilter !== "all" || typeFilter !== "all")}
                />
              ) : (
                <div className="space-y-3">
                  {filteredRules.map((rule) => (
                    <div key={rule.ruleId} id={`rule-card-${rule.ruleId}`}>
                    <AnalyzeRuleCard
                      rule={rule}
                      isExpanded={expandedRuleId === rule.ruleId}
                      isEditing={editingRuleId === rule.ruleId}
                      editForm={editForm}
                      setEditForm={setEditForm}
                      saving={saving}
                      togglingRuleId={togglingRuleId}
                      deletingRuleId={deletingRuleId}
                      jsonModeEdit={jsonModeEdit}
                      jsonText={jsonText}
                      jsonError={jsonError}
                      onToggle={() => {
                        if (editingRuleId === rule.ruleId) return;
                        setExpandedRuleId(expandedRuleId === rule.ruleId ? null : rule.ruleId);
                        if (expandedRuleId === rule.ruleId && editingRuleId === rule.ruleId) setEditingRuleId(null);
                      }}
                      onToggleEnabled={handleToggleRule}
                      onToggleMarkAsFailed={handleToggleMarkAsFailed}
                      onUpdateNotify={handleUpdateNotify}
                      tenantChannels={tenantChannels}
                      onStartEditing={startEditing}
                      onSaveEdit={handleSaveEdit}
                      onCancelEdit={() => { setEditingRuleId(null); setJsonModeEdit(false); setJsonError(null); }}
                      onDelete={handleDeleteRule}
                      onExport={handleExportSingle}
                      onSetJsonModeEdit={setJsonModeEdit}
                      onSetJsonText={setJsonText}
                      onSetJsonError={setJsonError}
                      readOnly={isReadOnly}
                      variant={activeTab === "templates" ? "template" : "default"}
                      onConfigureTemplate={(r) => setConfigureTemplateRule(r)}
                      templateCopyExists={templateCopyMap.has(rule.ruleId)}
                      templateCopyRuleId={templateCopyMap.get(rule.ruleId)}
                      onScrollToCopy={(copyId) => setExpandedRuleId(copyId)}
                      hitRate={ruleStatsMap[rule.ruleId]?.hitRate ?? null}
                      fireCount={ruleStatsMap[rule.ruleId]?.fireCount ?? null}
                    />
                    </div>
                  ))}
                </div>
              )}
            </div>
          )}
        </main>

        {/* Template Configuration Modal */}
        {configureTemplateRule && (
          <TemplateConfigModal
            rule={configureTemplateRule}
            saving={creatingFromTemplate}
            onSave={handleCreateFromTemplate}
            onCancel={() => setConfigureTemplateRule(null)}
          />
        )}
      </div>
    </ProtectedRoute>
  );
}
