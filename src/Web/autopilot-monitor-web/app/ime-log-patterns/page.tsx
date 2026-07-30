"use client";

import { useEffect, useState, useCallback } from "react";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { api } from "@/lib/api";
import { useAuthenticatedFetch, useNotificationMessages } from "@/hooks";
import { useAdminMode } from "@/hooks/useAdminMode";
import { downloadAsJson, stripInternalFields } from "@/lib/rulePageHelpers";
import { StatCard } from "@/components/rules/StatCard";
import { RuleFilterBar } from "@/components/rules/RuleFilterBar";
import { EmptyState } from "@/components/rules/EmptyState";

import { ImeLogPattern, PatternForm, EMPTY_FORM, ACTION_LABELS } from "./types";
import ImePatternCard from "./components/ImePatternCard";
import { trackEvent } from "@/lib/appInsights";

export default function ImeLogPatternsPage() {
  const { tenantId } = useTenant();
  const { user } = useAuth();
  const isGlobalAdmin = user?.isGlobalAdmin ?? false;
  const { globalAdminMode } = useAdminMode();

  const { successMessage, error, showSuccess, showError } = useNotificationMessages();

  const { data: patterns, loading, execute: fetchPatternsExec, setData: setPatterns } = useAuthenticatedFetch<ImeLogPattern[]>({
    onError: (err) => showError(err.message),
    onTokenExpired: (err) => showError(err.message),
  });

  const { execute: mutate } = useAuthenticatedFetch({
    onError: (err) => showError(err.message),
    onTokenExpired: (err) => showError(err.message),
  });

  // Filters
  const [searchQuery, setSearchQuery] = useState("");
  const [categoryFilter, setCategoryFilter] = useState("all");
  const [actionFilter, setActionFilter] = useState("all");

  // Expand/Edit
  const [expandedPatternId, setExpandedPatternId] = useState<string | null>(null);
  const [editingPatternId, setEditingPatternId] = useState<string | null>(null);
  const [editForm, setEditForm] = useState<PatternForm>({ ...EMPTY_FORM });
  const [saving, setSaving] = useState(false);

  // JSON mode
  const [jsonModeEdit, setJsonModeEdit] = useState(false);
  const [jsonText, setJsonText] = useState("");
  const [jsonError, setJsonError] = useState<string | null>(null);

  // Toggle
  const [togglingPattern, setTogglingPattern] = useState<string | null>(null);

  // Parameter editing
  const [newParamKey, setNewParamKey] = useState("");
  const [newParamValue, setNewParamValue] = useState("");


  // Fetch patterns
  const fetchPatterns = useCallback(async () => {
    if (!tenantId) return;
    await fetchPatternsExec(
      api.rules.imeLogPatterns(),
      undefined,
      { transform: (d) => { const r = d as { success?: boolean; patterns?: ImeLogPattern[] }; return r.success && Array.isArray(r.patterns) ? r.patterns : []; } }
    );
  }, [tenantId, fetchPatternsExec]);

  useEffect(() => {
    fetchPatterns();
  }, [fetchPatterns]);

  // Toggle enable/disable (Global Admin only)
  const handleTogglePattern = async (pattern: ImeLogPattern) => {
    if (!isGlobalAdmin || !globalAdminMode) return;

    setTogglingPattern(pattern.patternId);
    const result = await mutate(
      api.rules.imeLogPattern(pattern.patternId),
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...pattern, enabled: !pattern.enabled }),
      }
    );
    if (result !== null) {
      setPatterns((prev) =>
        (prev || []).map((p) => (p.patternId === pattern.patternId ? { ...p, enabled: !p.enabled } : p))
      );
      if (pattern.category === "always" && pattern.enabled) {
        showSuccess(`Pattern "${pattern.patternId}" disabled. Warning: This pattern is in the "always" category and may be critical for enrollment tracking.`);
      } else {
        showSuccess(`Pattern "${pattern.patternId}" ${pattern.enabled ? "disabled" : "enabled"} successfully!`);
      }
    }
    setTogglingPattern(null);
  };

  // Start editing (Global Admin only)
  const startEditing = (pattern: ImeLogPattern) => {
    setEditingPatternId(pattern.patternId);
    setEditForm({
      category: pattern.category,
      pattern: pattern.pattern,
      action: pattern.action,
      parameters: pattern.parameters ? { ...pattern.parameters } : {},
      description: pattern.description || "",
      enabled: pattern.enabled,
    });
    setJsonModeEdit(false);
    setJsonError(null);
    setNewParamKey("");
    setNewParamValue("");
  };

  // Save edit (Global Admin only)
  const handleSaveEdit = async (patternId: string) => {
    setSaving(true);
    const payload: Partial<ImeLogPattern> = {
      patternId,
      category: editForm.category,
      pattern: editForm.pattern,
      action: editForm.action,
      parameters: Object.keys(editForm.parameters).length > 0 ? editForm.parameters : undefined,
      description: editForm.description || undefined,
      enabled: editForm.enabled,
    };

    const result = await mutate(
      api.rules.imeLogPattern(patternId),
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      }
    );
    if (result !== null) {
      setPatterns((prev) =>
        (prev || []).map((p) =>
          p.patternId === patternId
            ? { ...p, ...payload, parameters: payload.parameters || {} }
            : p
        )
      );
      trackEvent("ime_pattern_modified");
      setEditingPatternId(null);
      showSuccess(`Pattern "${patternId}" updated successfully!`);
    }
    setSaving(false);
  };

  // Export helpers
  const handleExportSingle = (pattern: ImeLogPattern) => {
    const cleaned = stripInternalFields(pattern);
    downloadAsJson({ "$schema": "../schema/ime-log-pattern.schema.json", ...cleaned }, `${pattern.patternId}.json`);
    trackEvent("rules_exported", { ruleType: "ime_pattern", scope: "single" });
  };

  const handleExportAll = () => {
    const cleaned = filteredPatterns.map(stripInternalFields);
    downloadAsJson(cleaned, "ime-log-patterns-export.json");
    trackEvent("rules_exported", { ruleType: "ime_pattern", scope: "all" });
  };

  const patternsList = patterns || [];

  // Filtering
  const filteredPatterns = patternsList
    .filter((p) => {
      if (categoryFilter !== "all" && p.category !== categoryFilter) return false;
      if (actionFilter !== "all" && p.action !== actionFilter) return false;
      if (searchQuery) {
        const q = searchQuery.toLowerCase();
        return (
          p.patternId.toLowerCase().includes(q) ||
          p.pattern.toLowerCase().includes(q) ||
          p.action.toLowerCase().includes(q) ||
          (p.description || "").toLowerCase().includes(q)
        );
      }
      return true;
    })
    .sort((a, b) => {
      const catOrder = ["always", "currentPhase", "otherPhases"];
      const catDiff = catOrder.indexOf(a.category) - catOrder.indexOf(b.category);
      if (catDiff !== 0) return catDiff;
      return a.patternId.localeCompare(b.patternId);
    });

  // Stats
  const totalPatterns = patternsList.length;
  const activePatterns = patternsList.filter((p) => p.enabled).length;
  const alwaysCount = patternsList.filter((p) => p.category === "always").length;
  const currentPhaseCount = patternsList.filter((p) => p.category === "currentPhase").length;
  const otherPhasesCount = patternsList.filter((p) => p.category === "otherPhases").length;

  // Unique actions in data for filter dropdown
  const uniqueActions = [...new Set(patternsList.map((p) => p.action))].sort();

  const canEdit = isGlobalAdmin && globalAdminMode;
  const isReadOnly = !user?.isTenantAdmin && !user?.isGlobalAdmin;

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        {/* Header */}
        <header className="bg-white shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <div>
              <h1 className="text-2xl font-normal text-gray-900">IME Log Patterns</h1>
              <p className="text-sm text-gray-600 mt-1">
                Regex patterns used by the agent to parse Intune Management Extension logs during enrollment tracking.
              </p>
            </div>
          </div>
        </header>

        {/* Main Content */}
        <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          {loading ? (
            <div className="bg-white rounded-lg shadow p-8 text-center">
              <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-indigo-600 mx-auto"></div>
              <p className="mt-4 text-gray-600">Loading IME log patterns...</p>
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
                  Missing a pattern, found a bug, or have an improvement idea? Help make Autopilot Monitor better —{" "}
                  <a href="https://github.com/okieselbach/AutopilotMonitor/issues" target="_blank" rel="noopener noreferrer" className="font-medium underline hover:text-blue-900">
                    open a GitHub issue
                  </a>.
                </p>
              </div>

              {/* Summary Stats */}
              <div className="grid grid-cols-2 sm:grid-cols-5 gap-4">
                <StatCard label="Total Patterns" value={totalPatterns} />
                <StatCard label="Active" value={activePatterns} valueColor="text-emerald-600" />
                <StatCard label="Always" value={alwaysCount} valueColor="text-emerald-600" />
                <StatCard label="Current Phase" value={currentPhaseCount} valueColor="text-blue-600" />
                <StatCard label="Other Phases" value={otherPhasesCount} valueColor="text-purple-600" />
              </div>

              {/* Filter Bar + Export */}
              <RuleFilterBar
                searchQuery={searchQuery}
                onSearchChange={setSearchQuery}
                searchPlaceholder="Search by pattern ID, regex, action..."
                filters={[
                  {
                    label: "Category",
                    value: categoryFilter,
                    onChange: setCategoryFilter,
                    options: [
                      { value: "all", label: "All Categories" },
                      { value: "always", label: "Always" },
                      { value: "currentPhase", label: "Current Phase" },
                      { value: "otherPhases", label: "Other Phases" },
                    ],
                  },
                  {
                    label: "Action",
                    value: actionFilter,
                    onChange: setActionFilter,
                    options: [
                      { value: "all", label: "All Actions" },
                      ...uniqueActions.map((a) => ({ value: a, label: ACTION_LABELS[a] || a })),
                    ],
                  },
                ]}
                onExportAll={isReadOnly ? undefined : handleExportAll}
              />

              {/* Patterns List */}
              {filteredPatterns.length === 0 ? (
                <EmptyState
                  message="Try adjusting your search or filter criteria."
                  onClearFilters={() => { setSearchQuery(""); setCategoryFilter("all"); setActionFilter("all"); }}
                  showClearButton={!!(searchQuery || categoryFilter !== "all" || actionFilter !== "all")}
                />
              ) : (
                filteredPatterns.map((pattern) => (
                  <ImePatternCard
                    key={pattern.patternId}
                    pattern={pattern}
                    isExpanded={expandedPatternId === pattern.patternId}
                    isEditing={editingPatternId === pattern.patternId}
                    editForm={editForm}
                    setEditForm={setEditForm}
                    saving={saving}
                    togglingPattern={togglingPattern}
                    canEdit={canEdit}
                    jsonModeEdit={jsonModeEdit}
                    jsonText={jsonText}
                    jsonError={jsonError}
                    newParamKey={newParamKey}
                    newParamValue={newParamValue}
                    onSetNewParamKey={setNewParamKey}
                    onSetNewParamValue={setNewParamValue}
                    onToggle={() => {
                      if (editingPatternId === pattern.patternId) return;
                      setExpandedPatternId(expandedPatternId === pattern.patternId ? null : pattern.patternId);
                      if (expandedPatternId === pattern.patternId && editingPatternId === pattern.patternId) {
                        setEditingPatternId(null);
                      }
                    }}
                    onToggleEnabled={handleTogglePattern}
                    onStartEditing={startEditing}
                    onSaveEdit={handleSaveEdit}
                    onCancelEdit={() => { setEditingPatternId(null); setJsonModeEdit(false); setJsonError(null); }}
                    onExport={handleExportSingle}
                    onSetJsonModeEdit={setJsonModeEdit}
                    onSetJsonText={setJsonText}
                    onSetJsonError={setJsonError}
                    readOnly={isReadOnly}
                  />
                ))
              )}
            </div>
          )}
        </main>
      </div>
    </ProtectedRoute>
  );
}
