"use client";

import { useState } from "react";
import { AnalyzeRule, RuleForm, getSeverityColor, getCategoryColor } from "../types";
import { FormJsonToggle, JsonModeToggleButtons, ReadOnlyJsonView } from "@/components/rules/FormJsonToggle";
import AnalyzeRuleFormFields from "./AnalyzeRuleFormFields";
import { stripInternalFields } from "@/lib/rulePageHelpers";
import { formatInlineMarkdown } from "@/lib/formatInlineMarkdown";

interface AnalyzeRuleCardProps {
  rule: AnalyzeRule;
  isExpanded: boolean;
  isEditing: boolean;
  editForm: RuleForm;
  setEditForm: (f: RuleForm) => void;
  saving: boolean;
  togglingRuleId: string | null;
  deletingRuleId: string | null;
  jsonModeEdit: boolean;
  jsonText: string;
  jsonError: string | null;
  onToggle: () => void;
  onToggleEnabled: (rule: AnalyzeRule) => void;
  onToggleMarkAsFailed?: (rule: AnalyzeRule) => void;
  onStartEditing: (rule: AnalyzeRule) => void;
  onSaveEdit: (rule: AnalyzeRule, formOverride?: RuleForm) => void;
  onCancelEdit: () => void;
  onDelete: (rule: AnalyzeRule) => void;
  onExport: (rule: AnalyzeRule) => void;
  onSetJsonModeEdit: (mode: boolean) => void;
  onSetJsonText: (text: string) => void;
  onSetJsonError: (error: string | null) => void;
  readOnly?: boolean;
  /** "template" renders the card as a copy blueprint (used inside the Templates tab):
      a static template glyph instead of a toggle, plus a create/already-created banner. */
  variant?: "default" | "template";
  onConfigureTemplate?: (rule: AnalyzeRule) => void;
  templateCopyExists?: boolean;
  templateCopyRuleId?: string;
  onScrollToCopy?: (ruleId: string) => void;
  hitRate?: number | null;
  fireCount?: number | null;
}

export default function AnalyzeRuleCard({
  rule, isExpanded, isEditing, editForm, setEditForm, saving,
  togglingRuleId, deletingRuleId,
  jsonModeEdit, jsonText, jsonError,
  onToggle, onToggleEnabled, onToggleMarkAsFailed, onStartEditing, onSaveEdit, onCancelEdit,
  onDelete, onExport,
  onSetJsonModeEdit, onSetJsonText, onSetJsonError,
  readOnly = false,
  variant = "default",
  onConfigureTemplate, templateCopyExists, templateCopyRuleId, onScrollToCopy,
  hitRate, fireCount,
}: AnalyzeRuleCardProps) {
  const isTemplateVariant = variant === "template";
  const [showJson, setShowJson] = useState(false);
  const sevColor = getSeverityColor(rule.severity);
  const catColor = getCategoryColor(rule.category);
  const canEdit = !rule.isBuiltIn && !rule.isCommunity;
  const isTemplate = (rule.templateVariables?.length ?? 0) > 0;
  const isDerived = !!rule.derivedFromTemplateRuleId;
  const effectiveMarkAsFailed = (rule.markSessionAsFailed ?? rule.markSessionAsFailedDefault) ?? false;
  const koOverrideIsExplicit = rule.markSessionAsFailed !== null && rule.markSessionAsFailed !== undefined;

  return (
    <div
      className={`bg-white rounded-lg shadow border transition-all ${
        isExpanded ? "border-indigo-300 ring-1 ring-indigo-200" : "border-gray-200 hover:border-gray-300"
      }`}
    >
      {/* Collapsed Header */}
      <div className="p-4 cursor-pointer select-none" onClick={() => { if (isEditing) return; onToggle(); }}>
        <div className="flex items-center space-x-4">
          {isTemplateVariant ? (
            <span className="inline-flex h-6 w-6 flex-shrink-0 items-center justify-center rounded bg-amber-100 text-amber-600" title="Template — enabling creates a custom rule copy">
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 7H5a2 2 0 00-2 2v9a2 2 0 002 2h9a2 2 0 002-2v-3m-9-4h6m-6 4h6m2-11l4 4m0 0l-4 4m4-4H9" /></svg>
            </span>
          ) : readOnly ? (
            <span className={`inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full ${rule.enabled ? "bg-green-500" : "bg-gray-300"}`} title={rule.enabled ? "Enabled" : "Disabled"}>
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white ${rule.enabled ? "translate-x-6" : "translate-x-1"}`} />
            </span>
          ) : isTemplate && templateCopyExists ? (
            <button onClick={(e) => { e.stopPropagation(); if (templateCopyRuleId && onScrollToCopy) onScrollToCopy(templateCopyRuleId); }} className="relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full bg-gray-300 cursor-pointer" title="Custom copy already exists - click to view">
              <span className="inline-block h-4 w-4 transform rounded-full bg-white translate-x-1" />
            </button>
          ) : isTemplate && !rule.enabled ? (
            <button onClick={(e) => { e.stopPropagation(); onConfigureTemplate?.(rule); }} className="relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full bg-amber-400 hover:bg-amber-500 cursor-pointer transition-colors" title="Configure and enable this template rule">
              <span className="inline-block h-4 w-4 transform rounded-full bg-white translate-x-1" />
            </button>
          ) : (
            <button onClick={(e) => { e.stopPropagation(); onToggleEnabled(rule); }} disabled={togglingRuleId === rule.ruleId} className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${togglingRuleId === rule.ruleId ? "opacity-50 cursor-not-allowed" : "cursor-pointer"} ${rule.enabled ? "bg-green-500" : "bg-gray-300"}`} title={rule.enabled ? "Disable rule" : "Enable rule"}>
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${rule.enabled ? "translate-x-6" : "translate-x-1"}`} />
            </button>
          )}
          {/* Read-only KO indicator. Visible only when the effective value is ON so the header
              stays quiet for the common case. The actual toggle lives inside the expanded details
              (bottom-left) to prevent accidental clicks. */}
          {rule.enabled && effectiveMarkAsFailed && (
            <span
              className="inline-flex items-center px-2 py-0.5 rounded text-xs font-bold bg-red-100 text-red-800 border border-red-300 flex-shrink-0"
              title={`KO criterion: firing this rule marks the session as failed in the portal${koOverrideIsExplicit ? " (tenant override)" : " (rule default)"}.`}
            >
              KO
            </span>
          )}
          <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-semibold ${sevColor.bg} ${sevColor.text} flex-shrink-0`}>
            <span className={`w-1.5 h-1.5 rounded-full ${sevColor.dot} mr-1.5`}></span>
            {rule.severity.charAt(0).toUpperCase() + rule.severity.slice(1)}
          </span>
          <span className="text-xs font-mono text-gray-400 flex-shrink-0 hidden sm:inline">{rule.ruleId}</span>
          <div className="flex-1 min-w-0">
            <h3 className="text-sm font-semibold text-gray-900 truncate">{rule.title}</h3>
          </div>
          <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${catColor.bg} ${catColor.text} flex-shrink-0`}>
            {rule.category.charAt(0).toUpperCase() + rule.category.slice(1)}
          </span>
          <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium flex-shrink-0 ${rule.isBuiltIn ? "bg-blue-50 text-blue-600 border border-blue-200" : rule.isCommunity ? "bg-green-100 text-green-700" : "bg-purple-50 text-purple-600 border border-purple-200"}`}>
            {rule.isBuiltIn ? "Built-in" : rule.isCommunity ? "Community" : "Custom"}
          </span>
          {isTemplate && (
            <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium flex-shrink-0 bg-amber-100 text-amber-800 border border-amber-200">
              Requires Setup
            </span>
          )}
          {isDerived && (
            <span className="text-xs text-gray-400 flex-shrink-0 hidden lg:inline" title={`Based on template ${rule.derivedFromTemplateRuleId}`}>
              Based on {rule.derivedFromTemplateRuleId}
            </span>
          )}
          <span className="text-xs text-gray-500 flex-shrink-0 hidden md:inline" title="Confidence Threshold">Threshold: {rule.confidenceThreshold}%</span>
          {hitRate != null && hitRate > 0 && (
            <span
              className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium flex-shrink-0 ${
                hitRate >= 20 ? "bg-red-50 text-red-700 border border-red-200" :
                hitRate >= 5 ? "bg-amber-50 text-amber-700 border border-amber-200" :
                "bg-gray-50 text-gray-600 border border-gray-200"
              }`}
              title={`Fires on ${hitRate}% of evaluated sessions (${fireCount ?? 0} total fires in last 30 days)`}
            >
              {hitRate}% hit rate
            </span>
          )}
          <svg className={`w-5 h-5 text-gray-400 transition-transform flex-shrink-0 ${isExpanded ? "rotate-180" : ""}`} fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" /></svg>
        </div>
      </div>

      {/* Template-tab call-to-action / already-created banner.
          Always visible (not gated on expand) so the copy state is obvious at a glance. */}
      {isTemplateVariant && !readOnly && (
        templateCopyExists ? (
          <div className="mx-4 mb-4 -mt-1 rounded-lg border border-green-200 bg-green-50 p-3 flex items-center justify-between gap-3">
            <div className="flex items-center gap-2 text-sm text-green-800 min-w-0">
              <svg className="w-4 h-4 flex-shrink-0 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" /></svg>
              <span className="truncate">
                Custom rule already created{templateCopyRuleId ? <>: <code className="bg-green-100 px-1 rounded text-xs">{templateCopyRuleId}</code></> : ""}.
              </span>
            </div>
            <button
              onClick={(e) => { e.stopPropagation(); if (templateCopyRuleId && onScrollToCopy) onScrollToCopy(templateCopyRuleId); }}
              className="px-3 py-1.5 text-xs font-medium rounded-lg border border-green-300 text-green-800 bg-white hover:bg-green-100 transition-colors flex-shrink-0"
            >
              View / edit
            </button>
          </div>
        ) : (
          <div className="mx-4 mb-4 -mt-1 rounded-lg border border-amber-200 bg-amber-50 p-3 flex items-center justify-between gap-3">
            <p className="text-sm text-amber-800 min-w-0">
              This is a copy template. Enabling it creates an editable custom rule for your tenant — the template itself stays unchanged.
            </p>
            <button
              onClick={(e) => { e.stopPropagation(); onConfigureTemplate?.(rule); }}
              className="px-3 py-1.5 text-xs font-medium rounded-lg bg-amber-600 text-white hover:bg-amber-700 transition-colors flex-shrink-0 whitespace-nowrap"
            >
              Create custom rule
            </button>
          </div>
        )
      )}

      {/* Expanded Details (read-only) */}
      {isExpanded && !isEditing && (
        <div className="border-t border-gray-200 p-6 space-y-6">
          {/* Meta Info Row + Details/JSON toggle */}
          <div className="flex items-start justify-between gap-4">
            <div className="flex flex-wrap items-center gap-4 text-sm text-gray-500">
              <span><span className="font-medium text-gray-700">Version:</span> {rule.version}</span>
              <span><span className="font-medium text-gray-700">Author:</span> {rule.author}</span>
              <span><span className="font-medium text-gray-700">Trigger:</span> {(rule.trigger || "single").charAt(0).toUpperCase() + (rule.trigger || "single").slice(1)}</span>
              <span><span className="font-medium text-gray-700">Created:</span> {new Date(rule.createdAt).toLocaleDateString()}</span>
              <span><span className="font-medium text-gray-700">Updated:</span> {new Date(rule.updatedAt).toLocaleDateString()}</span>
              <span className="text-xs font-mono text-gray-400 sm:hidden">{rule.ruleId}</span>
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
              textareaRows={24}
            />
          ) : (
          <>

          {rule.tags && rule.tags.length > 0 && (
            <div className="flex flex-wrap gap-2">
              {rule.tags.map((tag, idx) => (<span key={idx} className="inline-flex items-center px-2 py-0.5 rounded text-xs bg-gray-100 text-gray-600">#{tag}</span>))}
            </div>
          )}

          <div>
            <h4 className="text-sm font-semibold text-gray-700 mb-1">Description</h4>
            <p className="text-sm text-gray-600 leading-relaxed">{rule.description}</p>
          </div>

          {rule.preconditions && rule.preconditions.length > 0 && (
            <div>
              <h4 className="text-sm font-semibold text-gray-700 mb-2">Preconditions ({rule.preconditions.length})</h4>
              <p className="text-xs text-gray-500 mb-2">Rule is silently skipped if any of these fails.</p>
              <div className="space-y-2">
                {rule.preconditions.map((pre, idx) => (
                  <div key={idx} className="bg-amber-50 border border-amber-200 rounded-lg p-3 text-sm">
                    <p className="text-gray-700">
                      <span className="text-gray-600 font-medium">Event:</span> {pre.eventType}
                      {pre.dataField && (<span> | <span className="text-gray-600 font-medium">Field:</span> <code className="bg-amber-100 px-1 rounded text-xs">{pre.dataField}</code></span>)}
                      {" "}
                      <code className="bg-amber-100 px-1 rounded text-xs">{pre.operator}</code>
                      {pre.value !== "" && (<> <code className="bg-amber-100 px-1 rounded text-xs">{pre.value}</code></>)}
                    </p>
                    {pre.description && (<p className="text-xs text-gray-500 italic mt-1">{pre.description}</p>)}
                  </div>
                ))}
              </div>
            </div>
          )}

          {rule.conditions.length > 0 && (
            <div>
              <h4 className="text-sm font-semibold text-gray-700 mb-2">Conditions ({rule.conditions.length})</h4>
              <div className="space-y-2">
                {rule.conditions.map((condition, idx) => (
                  <div key={idx} className="bg-gray-50 border border-gray-200 rounded-lg p-3 text-sm">
                    <div className="flex flex-wrap items-center gap-2 mb-1">
                      <span className="font-medium text-gray-800">{condition.signal}</span>
                      {condition.required && (<span className="text-xs px-1.5 py-0.5 rounded bg-red-100 text-red-700 font-medium">Required</span>)}
                    </div>
                    <div className="text-gray-500 space-y-0.5">
                      <p>
                        <span className="text-gray-600 font-medium">Source:</span> {condition.source}
                        {condition.eventType && (<span> | <span className="text-gray-600 font-medium">Event Type:</span> {condition.eventType}</span>)}
                        {condition.dataField && (<span> | <span className="text-gray-600 font-medium">Field:</span> <code className="bg-gray-200 px-1 rounded text-xs">{condition.dataField}</code></span>)}
                      </p>
                      <p>
                        <span className="text-gray-600 font-medium">Operator:</span> <code className="bg-gray-200 px-1 rounded text-xs">{condition.operator}</code>{" "}
                        <span className="text-gray-600 font-medium">Value:</span> <code className="bg-gray-200 px-1 rounded text-xs">{condition.value}</code>
                      </p>
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}

          <div>
            <h4 className="text-sm font-semibold text-gray-700 mb-2">Confidence Scoring</h4>
            <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 space-y-3">
              <div className="flex items-center space-x-6 text-sm">
                <span><span className="text-gray-600 font-medium">Base Confidence:</span> <span className="font-semibold text-gray-900">{rule.baseConfidence}%</span></span>
                <span><span className="text-gray-600 font-medium">Threshold:</span> <span className="font-semibold text-gray-900">{rule.confidenceThreshold}%</span></span>
              </div>
              {rule.confidenceFactors.length > 0 && (
                <div>
                  <p className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-2">Confidence Factors</p>
                  <div className="space-y-1">
                    {rule.confidenceFactors.map((factor, idx) => (
                      <div key={idx} className="flex items-center justify-between text-sm bg-white border border-gray-100 rounded px-3 py-1.5">
                        <div><span className="font-medium text-gray-700">{factor.signal}</span><span className="text-gray-400 mx-2">-</span><span className="text-gray-500">{factor.condition}</span></div>
                        <span className={`font-semibold ${factor.weight > 0 ? "text-green-600" : "text-red-600"}`}>{factor.weight > 0 ? "+" : ""}{factor.weight}%</span>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </div>
          </div>

          {rule.explanation && (
            <div>
              <h4 className="text-sm font-semibold text-gray-700 mb-1">Explanation</h4>
              <p className="text-sm text-gray-600 leading-relaxed bg-blue-50 border border-blue-200 rounded-lg p-3">{formatInlineMarkdown(rule.explanation)}</p>
            </div>
          )}

          {rule.remediation.length > 0 && (
            <div>
              <h4 className="text-sm font-semibold text-gray-700 mb-2">Remediation Steps</h4>
              <div className="space-y-3">
                {rule.remediation.map((rem, idx) => (
                  <div key={idx} className="bg-green-50 border border-green-200 rounded-lg p-4">
                    <h5 className="text-sm font-semibold text-green-800 mb-2">{rem.title}</h5>
                    <ol className="list-decimal list-inside space-y-1">
                      {rem.steps.map((step, sIdx) => (<li key={sIdx} className="text-sm text-green-700">{step}</li>))}
                    </ol>
                  </div>
                ))}
              </div>
            </div>
          )}

          {rule.relatedDocs.length > 0 && (
            <div>
              <h4 className="text-sm font-semibold text-gray-700 mb-2">Related Documentation</h4>
              <div className="flex flex-wrap gap-2">
                {rule.relatedDocs.map((doc, idx) => (
                  <a key={idx} href={doc.url} target="_blank" rel="noopener noreferrer" className="inline-flex items-center space-x-1.5 px-3 py-1.5 bg-indigo-50 border border-indigo-200 rounded-lg text-sm text-indigo-700 hover:bg-indigo-100 transition-colors">
                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14" /></svg>
                    <span>{doc.title}</span>
                  </a>
                ))}
              </div>
            </div>
          )}

          </>
          )}

          {/* KO criterion toggle — deliberately placed in the expanded detail view (not the header)
              to prevent accidental clicks. Visible to tenant admins for any enabled rule. */}
          {!readOnly && rule.enabled && onToggleMarkAsFailed && (
            <div className="pt-4 border-t border-gray-200">
              <div className="flex items-start justify-between gap-4 bg-gray-50 border border-gray-200 rounded-lg p-4">
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2 mb-1">
                    <h4 className="text-sm font-semibold text-gray-800">KO criterion</h4>
                    {effectiveMarkAsFailed && (
                      <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-bold bg-red-100 text-red-800 border border-red-300">
                        ACTIVE
                      </span>
                    )}
                    {koOverrideIsExplicit && (
                      <span className="text-[10px] text-gray-500 italic" title="Tenant override — differs from the rule's shipped default">
                        tenant override
                      </span>
                    )}
                  </div>
                  <p className="text-xs text-gray-600 leading-relaxed">
                    When enabled, a firing of this rule marks the whole enrollment session as <span className="font-semibold text-red-700">failed</span> in the portal —
                    even if the agent itself reported the session as successful. Use this for rules that represent a hard pass/fail criterion for your organization
                    (e.g. certificate provisioning must succeed).
                  </p>
                </div>
                <button
                  onClick={() => onToggleMarkAsFailed(rule)}
                  disabled={togglingRuleId === rule.ruleId}
                  className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${togglingRuleId === rule.ruleId ? "opacity-50 cursor-not-allowed" : "cursor-pointer"} ${effectiveMarkAsFailed ? "bg-red-500" : "bg-gray-300"}`}
                  title={effectiveMarkAsFailed ? "Disable KO criterion" : "Enable KO criterion"}
                >
                  <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${effectiveMarkAsFailed ? "translate-x-6" : "translate-x-1"}`} />
                </button>
              </div>
            </div>
          )}

          {/* Actions */}
          {!readOnly && (
            <div className="flex items-center justify-end space-x-3 pt-4 border-t border-gray-200">
              <button onClick={() => onExport(rule)} className="px-4 py-2 text-sm bg-gray-100 text-gray-700 rounded-lg hover:bg-gray-200 transition-colors flex items-center space-x-2" title="Export rule as JSON">
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" /></svg>
                <span>Export</span>
              </button>
              {canEdit && (
                <button onClick={() => onStartEditing(rule)} className="px-4 py-2 text-sm bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 transition-colors flex items-center space-x-2" title="Edit rule">
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" /></svg>
                  <span>Edit</span>
                </button>
              )}
              {!rule.isBuiltIn && !rule.isCommunity && (
                <button onClick={() => onDelete(rule)} disabled={deletingRuleId === rule.ruleId} className="px-4 py-2 text-sm bg-red-600 text-white rounded-lg hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2" title="Delete rule">
                  {deletingRuleId === rule.ruleId ? (<><div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div><span>Deleting...</span></>) : (<><svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" /></svg><span>Delete</span></>)}
                </button>
              )}
            </div>
          )}
        </div>
      )}

      {/* Edit Form */}
      {isExpanded && isEditing && !readOnly && (
        <div className="border-t border-gray-200 p-6">
          <div className="flex items-center justify-between mb-4">
            <div className="flex items-center space-x-2">
              <svg className="w-5 h-5 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" /></svg>
              <h4 className="text-sm font-semibold text-gray-900">
                Editing: {rule.ruleId}
              </h4>
            </div>
            {/* JSON Mode Toggle */}
            <JsonModeToggleButtons
              jsonMode={jsonModeEdit}
              onToggleMode={(mode) => {
                if (mode) { onSetJsonText(JSON.stringify(editForm, null, 2)); }
                onSetJsonModeEdit(mode);
                onSetJsonError(null);
              }}
            />
          </div>

          <FormJsonToggle
            jsonMode={jsonModeEdit}
            onToggleMode={(mode) => {
              if (mode) { onSetJsonText(JSON.stringify(editForm, null, 2)); }
              onSetJsonModeEdit(mode);
              onSetJsonError(null);
            }}
            jsonText={jsonText}
            onJsonTextChange={(text) => { onSetJsonText(text); onSetJsonError(null); }}
            jsonError={jsonError}
            onApplyJson={() => {
              try {
                const parsed = JSON.parse(jsonText) as RuleForm;
                if (!parsed.title) throw new Error("JSON must include title");
                setEditForm({ ...editForm, ...parsed });
                onSetJsonModeEdit(false);
                onSetJsonError(null);
              } catch (e) {
                onSetJsonError(e instanceof Error ? e.message : "Invalid JSON");
              }
            }}
            textareaRows={30}
            description={<>Edit the rule as JSON. All fields are supported including <code className="bg-gray-100 px-1 rounded text-xs">event_correlation</code> condition properties.</>}
          >
            <AnalyzeRuleFormFields form={editForm} setForm={setEditForm} showRuleId={false} />
          </FormJsonToggle>

          <div className="flex items-center justify-end space-x-3 pt-4 mt-5 border-t border-gray-200">
            <button onClick={onCancelEdit} disabled={saving} className="px-6 py-2 border border-gray-300 rounded-lg text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm font-medium">Cancel</button>
            <button onClick={() => {
              if (jsonModeEdit) {
                try {
                  const parsed = JSON.parse(jsonText) as RuleForm;
                  onSetJsonError(null);
                  onSaveEdit(rule, { ...editForm, ...parsed });
                } catch (e) {
                  onSetJsonError(e instanceof Error ? e.message : "Invalid JSON");
                }
              } else {
                onSaveEdit(rule);
              }
            }} disabled={saving} className="px-6 py-2 bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2 text-sm font-medium">
              {saving ? (<><div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div><span>Saving...</span></>) : (<span>Save Changes</span>)}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
