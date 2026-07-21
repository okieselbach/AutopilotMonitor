"use client";

import { useMemo, useState } from "react";
import { GatherRule, NewRuleForm, CATEGORY_COLORS, COLLECTOR_TYPE_LABELS, EMPTY_FORM, formatTrigger, formatGatherPhase, withDerivedScopeMode } from "../types";
import { GatherRuleFormFields } from "./GatherRuleFormFields";
import { FormJsonToggle, JsonModeToggleButtons, ReadOnlyJsonView } from "@/components/rules/FormJsonToggle";
import { validateGatherRuleTarget } from "@/utils/guardValidation";
import { ValidationIndicator } from "@/components/ValidationIndicator";
import { stripInternalFields } from "@/lib/rulePageHelpers";

interface GatherRuleCardProps {
  rule: GatherRule;
  isExpanded: boolean;
  isEditing: boolean;
  editForm: NewRuleForm;
  setEditForm: (f: NewRuleForm) => void;
  saving: boolean;
  togglingRule: string | null;
  deletingRule: string | null;
  jsonModeEdit: boolean;
  jsonText: string;
  jsonError: string | null;
  onToggle: () => void;
  onExpand: () => void;
  onStartEditing: () => void;
  onCancelEditing: () => void;
  onSaveEdit: (rule: GatherRule, overrideForm?: NewRuleForm) => void;
  onDelete: () => void;
  onExport: () => void;
  onToggleJsonMode: (mode: boolean) => void;
  onJsonTextChange: (text: string) => void;
  onApplyJson: () => void;
  readOnly?: boolean;
  unrestrictedMode?: boolean;
}

export function GatherRuleCard({
  rule,
  isExpanded,
  isEditing,
  editForm,
  setEditForm,
  saving,
  togglingRule,
  deletingRule,
  jsonModeEdit,
  jsonText,
  jsonError,
  onToggle,
  onExpand,
  onStartEditing,
  onCancelEditing,
  onSaveEdit,
  onDelete,
  onExport,
  onToggleJsonMode,
  onJsonTextChange,
  onApplyJson,
  readOnly = false,
  unrestrictedMode = false,
}: GatherRuleCardProps) {
  const [showJson, setShowJson] = useState(false);
  const catColor = CATEGORY_COLORS[rule.category] || { bg: "bg-gray-100", text: "text-gray-700" };
  const canEdit = !rule.isBuiltIn && !rule.isCommunity;

  const targetValidation = useMemo(
    () => rule.target ? validateGatherRuleTarget(rule.collectorType, rule.target, unrestrictedMode) : null,
    [rule.collectorType, rule.target, unrestrictedMode]
  );

  return (
    <div
      className={`bg-white rounded-lg shadow border transition-all ${
        isExpanded ? "border-indigo-300 ring-1 ring-indigo-200" : "border-gray-200 hover:border-gray-300"
      } ${!rule.enabled ? "opacity-60" : ""}`}
    >
      {/* Collapsed Header */}
      <div
        className="p-4 cursor-pointer select-none"
        onClick={() => {
          if (isEditing) return;
          onExpand();
        }}
      >
        <div className="flex items-center space-x-4">
          {/* Enable/Disable Toggle */}
          {readOnly ? (
            <span
              className={`inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full ${rule.enabled ? "bg-emerald-500" : "bg-gray-300"}`}
              title={rule.enabled ? "Enabled" : "Disabled"}
            >
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white ${rule.enabled ? "translate-x-6" : "translate-x-1"}`} />
            </span>
          ) : (
            <button
              onClick={(e) => {
                e.stopPropagation();
                onToggle();
              }}
              disabled={togglingRule === rule.ruleId}
              className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${
                togglingRule === rule.ruleId
                  ? "opacity-50 cursor-not-allowed"
                  : "cursor-pointer"
              } ${rule.enabled ? "bg-emerald-500" : "bg-gray-300"}`}
              title={rule.enabled ? "Disable rule" : "Enable rule"}
            >
              <span
                className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                  rule.enabled ? "translate-x-6" : "translate-x-1"
                }`}
              />
            </button>
          )}

          {/* Rule ID */}
          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-mono font-medium bg-gray-100 text-gray-600 border border-gray-200 flex-shrink-0 hidden sm:inline-flex">
            {rule.ruleId}
          </span>

          {/* Title */}
          <div className="flex-1 min-w-0">
            <h3 className="text-sm font-semibold text-gray-900 truncate">
              {rule.title}
            </h3>
          </div>

          {/* Category Badge */}
          <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${catColor.bg} ${catColor.text} flex-shrink-0`}>
            {rule.category.charAt(0).toUpperCase() + rule.category.slice(1)}
          </span>

          {/* Collector Type */}
          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-50 text-gray-600 flex-shrink-0 hidden md:inline-flex">
            {COLLECTOR_TYPE_LABELS[rule.collectorType] || rule.collectorType}
          </span>

          {/* Type Badge */}
          <span
            className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium flex-shrink-0 ${
              rule.isBuiltIn
                ? "bg-blue-50 text-blue-600 border border-blue-200"
                : rule.isCommunity
                ? "bg-amber-50 text-amber-600 border border-amber-200"
                : "bg-purple-50 text-purple-600 border border-purple-200"
            }`}
          >
            {rule.isBuiltIn ? "Built-in" : rule.isCommunity ? "Community" : "Custom"}
          </span>

          {/* Expand/Collapse Arrow */}
          <svg
            className={`w-5 h-5 text-gray-400 transition-transform flex-shrink-0 ${
              isExpanded ? "rotate-180" : ""
            }`}
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
          </svg>
        </div>
      </div>

      {/* Expanded Details */}
      {isExpanded && !isEditing && (
        <div className="border-t border-gray-200 p-6 space-y-6">
          {/* Meta Info Row + Details/JSON toggle */}
          <div className="flex items-start justify-between gap-4">
            <div className="flex flex-wrap items-center gap-4 text-sm text-gray-500">
              <span>
                <span className="font-medium text-gray-700">Version:</span> {rule.version}
              </span>
              <span>
                <span className="font-medium text-gray-700">Author:</span> {rule.author}
              </span>
              <span>
                <span className="font-medium text-gray-700">Created:</span>{" "}
                {new Date(rule.createdAt).toLocaleDateString()}
              </span>
              <span>
                <span className="font-medium text-gray-700">Updated:</span>{" "}
                {new Date(rule.updatedAt).toLocaleDateString()}
              </span>
              <span className="text-xs font-mono text-gray-400 sm:hidden">
                {rule.ruleId}
              </span>
            </div>
            {!canEdit && (
              <div className="flex items-center bg-gray-100 rounded-lg p-1 flex-shrink-0">
                <button
                  onClick={() => setShowJson(false)}
                  className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors ${
                    !showJson ? "bg-white text-gray-900 shadow-sm" : "text-gray-500 hover:text-gray-700"
                  }`}
                >
                  Details
                </button>
                <button
                  onClick={() => setShowJson(true)}
                  className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors ${
                    showJson ? "bg-white text-gray-900 shadow-sm" : "text-gray-500 hover:text-gray-700"
                  }`}
                >
                  JSON
                </button>
              </div>
            )}
          </div>

          {/* JSON view (replaces detail view for built-in / community rules) */}
          {showJson && !canEdit ? (
            <ReadOnlyJsonView
              jsonText={JSON.stringify(stripInternalFields(rule), null, 2)}
              textareaRows={20}
            />
          ) : (
          <>

          {/* Tags */}
          {rule.tags && rule.tags.length > 0 && (
            <div className="flex flex-wrap gap-2">
              {rule.tags.map((tag, idx) => (
                <span
                  key={idx}
                  className="inline-flex items-center px-2 py-0.5 rounded text-xs bg-gray-100 text-gray-600"
                >
                  #{tag}
                </span>
              ))}
            </div>
          )}

          {/* Description */}
          {rule.description && (
            <div>
              <h4 className="text-sm font-semibold text-gray-700 mb-1">Description</h4>
              <p className="text-sm text-gray-600 leading-relaxed">{rule.description}</p>
            </div>
          )}

          {/* Collection Details */}
          <div>
            <h4 className="text-sm font-semibold text-gray-700 mb-2">Collection Details</h4>
            <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 space-y-3">
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 text-sm">
                <div>
                  <span className="text-gray-500 font-medium">Collector Type:</span>{" "}
                  <span className="text-gray-900 font-semibold">{COLLECTOR_TYPE_LABELS[rule.collectorType] || rule.collectorType}</span>
                </div>
                <div>
                  <span className="text-gray-500 font-medium">Category:</span>{" "}
                  <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${catColor.bg} ${catColor.text}`}>
                    {rule.category}
                  </span>
                </div>
              </div>
              <div className="text-sm">
                <span className="text-gray-500 font-medium">Target:</span>
                <code className="ml-2 px-2 py-1 bg-gray-200 rounded text-xs font-mono text-gray-800 break-all">
                  {rule.target}
                </code>
                <ValidationIndicator result={targetValidation} className="ml-2" />
              </div>
              {rule.parameters && Object.keys(rule.parameters).length > 0 && (
                <div className="text-sm">
                  <span className="text-gray-500 font-medium">Parameters:</span>
                  <div className="mt-1 space-y-1">
                    {Object.entries(rule.parameters).map(([key, value]) => (
                      <div key={key} className="flex items-start gap-2">
                        <code className="px-1.5 py-0.5 bg-indigo-100 rounded text-xs font-mono text-indigo-700">{key}</code>
                        <span className="text-gray-400">=</span>
                        <code className="px-1.5 py-0.5 bg-gray-200 rounded text-xs font-mono text-gray-700 break-all">{value}</code>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </div>
          </div>

          {/* Trigger Details */}
          <div>
            <h4 className="text-sm font-semibold text-gray-700 mb-2">Trigger</h4>
            <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 text-sm">
              <div className="flex items-center gap-2">
                <svg className="w-4 h-4 text-amber-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
                </svg>
                <span className="font-semibold text-gray-900">{formatTrigger(rule.trigger)}</span>
                {rule.trigger === "interval" && rule.intervalSeconds && (
                  <span className="text-gray-500">every {rule.intervalSeconds} seconds</span>
                )}
                {rule.trigger === "phase_change" && rule.triggerPhase && (
                  <span className="text-gray-500">
                    entering <code className="px-1 bg-gray-200 rounded text-xs">{formatGatherPhase(rule.triggerPhase)}</code>
                  </span>
                )}
                {rule.trigger === "phase_exit" && (
                  <span className="text-gray-500">
                    {rule.triggerPhase ? (
                      <>leaving <code className="px-1 bg-gray-200 rounded text-xs">{formatGatherPhase(rule.triggerPhase)}</code></>
                    ) : (
                      "on every phase exit"
                    )}
                  </span>
                )}
                {rule.trigger === "on_event" && rule.triggerEventType && (
                  <span className="text-gray-500">
                    on event: <code className="px-1 bg-gray-200 rounded text-xs">{rule.triggerEventType}</code>
                  </span>
                )}
                {rule.activePhases && rule.activePhases.length > 0 && (
                  <span className="text-gray-500">
                    · during {rule.activePhases.map(formatGatherPhase).join(", ")}
                  </span>
                )}
                {!rule.activePhases?.length && rule.activeFromPhase && (
                  <span className="text-gray-500">
                    · from {formatGatherPhase(rule.activeFromPhase)}
                  </span>
                )}
                {rule.emitMode === "on_change" && (
                  <span className="text-gray-500">· emit on change</span>
                )}
              </div>
            </div>
          </div>

          {/* Output */}
          <div>
            <h4 className="text-sm font-semibold text-gray-700 mb-2">Output</h4>
            <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 text-sm">
              <div className="flex flex-wrap items-center gap-4">
                <div>
                  <span className="text-gray-500 font-medium">Event Type:</span>{" "}
                  <code className="px-1.5 py-0.5 bg-gray-200 rounded text-xs font-mono text-gray-800">{rule.outputEventType}</code>
                </div>
                <div>
                  <span className="text-gray-500 font-medium">Severity:</span>{" "}
                  <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
                    rule.outputSeverity.toLowerCase() === "error" || rule.outputSeverity.toLowerCase() === "critical"
                      ? "bg-red-100 text-red-700"
                      : rule.outputSeverity.toLowerCase() === "warning"
                      ? "bg-yellow-100 text-yellow-700"
                      : "bg-blue-100 text-blue-700"
                  }`}>
                    {rule.outputSeverity.charAt(0).toUpperCase() + rule.outputSeverity.slice(1)}
                  </span>
                </div>
              </div>
            </div>
          </div>

          </>
          )}

          {/* Actions */}
          {!readOnly && (
            <div className="flex items-center justify-end space-x-3 pt-4 border-t border-gray-200">
              <button
                onClick={(e) => { e.stopPropagation(); onExport(); }}
                className="px-4 py-2 text-sm bg-gray-100 text-gray-700 rounded-lg hover:bg-gray-200 transition-colors flex items-center space-x-2"
                title="Export rule as JSON"
              >
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                </svg>
                <span>Export</span>
              </button>
              {canEdit && (
                <button
                  onClick={(e) => { e.stopPropagation(); onStartEditing(); }}
                  className="px-4 py-2 text-sm bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 transition-colors flex items-center space-x-2"
                  title="Edit rule"
                >
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
                  </svg>
                  <span>Edit</span>
                </button>
              )}
              {!rule.isBuiltIn && !rule.isCommunity && (
                <button
                  onClick={(e) => { e.stopPropagation(); onDelete(); }}
                  disabled={deletingRule === rule.ruleId}
                  className="px-4 py-2 text-sm bg-red-600 text-white rounded-lg hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
                  title="Delete rule"
                >
                  {deletingRule === rule.ruleId ? (
                    <>
                      <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                      <span>Deleting...</span>
                    </>
                  ) : (
                    <>
                      <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                      </svg>
                      <span>Delete</span>
                    </>
                  )}
                </button>
              )}
            </div>
          )}
        </div>
      )}

      {/* Edit Form (replaces detail view when editing) */}
      {isExpanded && isEditing && !readOnly && (
        <div className="border-t border-gray-200 p-6">
          <div className="flex items-center justify-between mb-4">
            <div className="flex items-center space-x-2">
              <svg className="w-5 h-5 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
              </svg>
              <h4 className="text-sm font-semibold text-gray-900">
                Editing: {rule.ruleId}
              </h4>
            </div>
            <JsonModeToggleButtons
              jsonMode={jsonModeEdit}
              onToggleMode={onToggleJsonMode}
            />
          </div>

          <FormJsonToggle
            jsonMode={jsonModeEdit}
            onToggleMode={onToggleJsonMode}
            jsonText={jsonText}
            onJsonTextChange={onJsonTextChange}
            jsonError={jsonError}
            onApplyJson={onApplyJson}
            textareaRows={20}
            description={<>Edit the rule as JSON. All fields are supported including <code className="bg-gray-100 px-1 rounded text-xs">parameters</code> for collector-specific options.</>}
          >
            <GatherRuleFormFields form={editForm} setForm={setEditForm} showRuleId={false} unrestrictedMode={unrestrictedMode} />
          </FormJsonToggle>

          {/* Action Buttons */}
          <div className="flex items-center justify-end space-x-3 pt-4 mt-5 border-t border-gray-200">
            <button
              onClick={onCancelEditing}
              disabled={saving}
              className="px-6 py-2 border border-gray-300 rounded-lg text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm font-medium"
            >
              Cancel
            </button>
            <button
              onClick={() => {
                if (jsonModeEdit) {
                  try {
                    const parsed = JSON.parse(jsonText) as NewRuleForm;
                    onSaveEdit(rule, withDerivedScopeMode({ ...editForm, ...parsed }));
                  } catch (e) {
                    // jsonError is handled by parent
                  }
                } else {
                  onSaveEdit(rule);
                }
              }}
              disabled={saving}
              className="px-6 py-2 bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2 text-sm font-medium"
            >
              {saving ? (
                <>
                  <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                  <span>Saving...</span>
                </>
              ) : (
                <span>Save Changes</span>
              )}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
