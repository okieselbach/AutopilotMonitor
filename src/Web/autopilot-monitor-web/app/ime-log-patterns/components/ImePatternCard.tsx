"use client";

import { useState } from "react";
import { ImeLogPattern, PatternForm, CATEGORIES, ACTIONS, CATEGORY_COLORS, CATEGORY_LABELS, ACTION_LABELS } from "../types";
import { FormJsonToggle, JsonModeToggleButtons, ReadOnlyJsonView } from "@/components/rules/FormJsonToggle";
import { stripInternalFields } from "@/lib/rulePageHelpers";

interface ImePatternCardProps {
  pattern: ImeLogPattern;
  isExpanded: boolean;
  isEditing: boolean;
  editForm: PatternForm;
  setEditForm: (f: PatternForm) => void;
  saving: boolean;
  togglingPattern: string | null;
  canEdit: boolean;
  jsonModeEdit: boolean;
  jsonText: string;
  jsonError: string | null;
  newParamKey: string;
  newParamValue: string;
  onSetNewParamKey: (key: string) => void;
  onSetNewParamValue: (value: string) => void;
  onToggle: () => void;
  onToggleEnabled: (pattern: ImeLogPattern) => void;
  onStartEditing: (pattern: ImeLogPattern) => void;
  onSaveEdit: (patternId: string) => void;
  onCancelEdit: () => void;
  onExport: (pattern: ImeLogPattern) => void;
  onSetJsonModeEdit: (mode: boolean) => void;
  onSetJsonText: (text: string) => void;
  onSetJsonError: (error: string | null) => void;
  readOnly?: boolean;
}

export default function ImePatternCard({
  pattern, isExpanded, isEditing, editForm, setEditForm, saving,
  togglingPattern, canEdit,
  jsonModeEdit, jsonText, jsonError,
  newParamKey, newParamValue, onSetNewParamKey, onSetNewParamValue,
  onToggle, onToggleEnabled, onStartEditing, onSaveEdit, onCancelEdit,
  onExport,
  onSetJsonModeEdit, onSetJsonText, onSetJsonError,
  readOnly = false,
}: ImePatternCardProps) {
  const [showJson, setShowJson] = useState(false);
  const catColor = CATEGORY_COLORS[pattern.category] || { bg: "bg-gray-100", text: "text-gray-700" };

  return (
    <div
      className={`bg-white rounded-lg shadow border transition-all ${
        isExpanded ? "border-indigo-300 ring-1 ring-indigo-200" : "border-gray-200 hover:border-gray-300"
      } ${!pattern.enabled ? "opacity-60" : ""}`}
    >
      {/* Collapsed Header */}
      <div
        className="p-4 cursor-pointer select-none"
        onClick={() => {
          if (isEditing) return;
          onToggle();
        }}
      >
        {/* Mobile: badges wrap and the title drops onto its own full-width row (order-last);
            ≥sm: single line with the title in the middle, exactly as before. */}
        <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
          {/* Enable/Disable Toggle */}
          {canEdit ? (
            <button
              onClick={(e) => {
                e.stopPropagation();
                onToggleEnabled(pattern);
              }}
              disabled={togglingPattern === pattern.patternId}
              className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${
                togglingPattern === pattern.patternId
                  ? "opacity-50 cursor-not-allowed"
                  : "cursor-pointer"
              } ${pattern.enabled ? "bg-emerald-500" : "bg-gray-300"}`}
              title={pattern.enabled ? "Disable pattern" : "Enable pattern"}
            >
              <span
                className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                  pattern.enabled ? "translate-x-6" : "translate-x-1"
                }`}
              />
            </button>
          ) : (
            <span
              className={`inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full ${
                pattern.enabled ? "bg-emerald-500" : "bg-gray-300"
              }`}
              title={pattern.enabled ? "Enabled" : "Disabled"}
            >
              <span
                className={`inline-block h-4 w-4 transform rounded-full bg-white ${
                  pattern.enabled ? "translate-x-6" : "translate-x-1"
                }`}
              />
            </span>
          )}

          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-mono font-medium bg-gray-100 text-gray-600 border border-gray-200 flex-shrink-0 hidden sm:inline-flex">
            {pattern.patternId}
          </span>

          <div className="order-last w-full min-w-0 sm:order-none sm:w-auto sm:flex-1">
            <h3 className="text-sm font-semibold text-gray-900 sm:truncate">
              {pattern.description || pattern.patternId}
            </h3>
          </div>

          <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${catColor.bg} ${catColor.text} flex-shrink-0`}>
            {CATEGORY_LABELS[pattern.category] || pattern.category}
          </span>

          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-50 text-gray-600 flex-shrink-0 hidden md:inline-flex">
            {ACTION_LABELS[pattern.action] || pattern.action}
          </span>

          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-blue-50 text-blue-600 border border-blue-200 flex-shrink-0">
            Built-in
          </span>

          <svg
            className={`w-5 h-5 text-gray-400 transition-transform flex-shrink-0 ml-auto sm:ml-0 ${
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

      {/* Expanded Details (Read-Only) */}
      {isExpanded && !isEditing && (
        <div className="border-t border-gray-200 p-6 space-y-6">
          {/* Description + Details/JSON toggle */}
          <div className="flex items-start justify-between gap-4">
            {pattern.description && (
              <div className="text-sm text-gray-700">
                {pattern.description}
              </div>
            )}
            <div className="flex items-center bg-gray-100 rounded-lg p-1 flex-shrink-0 ml-auto">
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
          </div>

          {/* JSON view (replaces detail view) */}
          {showJson ? (
            <ReadOnlyJsonView
              jsonText={JSON.stringify(stripInternalFields(pattern), null, 2)}
              textareaRows={15}
            />
          ) : (
          <>

          <div>
            <h4 className="text-sm font-semibold text-gray-700 mb-2">Regex Pattern</h4>
            <div className="bg-gray-50 border border-gray-200 rounded-lg p-4">
              <code className="text-sm font-mono text-gray-800 break-all whitespace-pre-wrap">
                {pattern.pattern}
              </code>
            </div>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <h4 className="text-sm font-semibold text-gray-700 mb-2">Action</h4>
              <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 text-sm">
                <div className="flex items-center gap-2">
                  <svg className="w-4 h-4 text-indigo-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
                  </svg>
                  <span className="font-semibold text-gray-900">{ACTION_LABELS[pattern.action] || pattern.action}</span>
                  <code className="px-1.5 py-0.5 bg-gray-200 rounded text-xs font-mono text-gray-600">{pattern.action}</code>
                </div>
              </div>
            </div>
            <div>
              <h4 className="text-sm font-semibold text-gray-700 mb-2">Category</h4>
              <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 text-sm">
                <div className="flex items-center gap-2">
                  <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${catColor.bg} ${catColor.text}`}>
                    {CATEGORY_LABELS[pattern.category] || pattern.category}
                  </span>
                  {pattern.category === "always" && (
                    <span className="text-xs text-gray-500">Evaluated on every log line</span>
                  )}
                  {pattern.category === "currentPhase" && (
                    <span className="text-xs text-gray-500">Only during the active ESP phase</span>
                  )}
                  {pattern.category === "otherPhases" && (
                    <span className="text-xs text-gray-500">During non-active phases</span>
                  )}
                </div>
              </div>
            </div>
          </div>

          {pattern.parameters && Object.keys(pattern.parameters).length > 0 && (
            <div>
              <h4 className="text-sm font-semibold text-gray-700 mb-2">Parameters</h4>
              <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 text-sm">
                <div className="space-y-1">
                  {Object.entries(pattern.parameters).map(([key, value]) => (
                    <div key={key} className="flex items-start gap-2">
                      <code className="px-1.5 py-0.5 bg-indigo-100 rounded text-xs font-mono text-indigo-700">{key}</code>
                      <span className="text-gray-400">=</span>
                      <code className="px-1.5 py-0.5 bg-gray-200 rounded text-xs font-mono text-gray-700 break-all">{value}</code>
                    </div>
                  ))}
                </div>
              </div>
            </div>
          )}

          </>
          )}

          {!readOnly && (
            <div className="flex items-center justify-end space-x-3 pt-4 border-t border-gray-200">
              <button
                onClick={() => onExport(pattern)}
                className="px-4 py-2 text-sm bg-gray-100 text-gray-700 rounded-lg hover:bg-gray-200 transition-colors flex items-center space-x-2"
                title="Export pattern as JSON"
              >
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                </svg>
                <span>Export</span>
              </button>
              {canEdit && (
                <button
                  onClick={() => onStartEditing(pattern)}
                  className="px-4 py-2 text-sm bg-green-600 text-white rounded-lg hover:bg-green-700 transition-colors flex items-center space-x-2"
                  title="Edit pattern (Global Admin)"
                >
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
                  </svg>
                  <span>Edit (Global)</span>
                </button>
              )}
            </div>
          )}
        </div>
      )}

      {/* Edit Form */}
      {isExpanded && isEditing && canEdit && (
        <div className="border-t border-gray-200 p-6">
          <div className="flex items-center justify-between mb-4">
            <div className="flex items-center space-x-2">
              <svg className="w-5 h-5 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
              </svg>
              <h4 className="text-sm font-semibold text-gray-900">
                Editing: {pattern.patternId}
                <span className="ml-2 inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-amber-100 text-amber-700 border border-amber-200">
                  Global Edit
                </span>
              </h4>
            </div>
            <JsonModeToggleButtons
              jsonMode={jsonModeEdit}
              onToggleMode={(mode) => {
                if (mode) { onSetJsonText(JSON.stringify(editForm, null, 2)); }
                onSetJsonModeEdit(mode);
                onSetJsonError(null);
              }}
            />
          </div>

          <div className="mb-4 bg-amber-50 border border-amber-200 rounded-lg p-3 text-sm text-amber-800">
            <strong>Global Admin:</strong> Changes will apply globally to all tenants.
          </div>

          {editForm.category === "always" && (
            <div className="mb-4 bg-red-50 border border-red-200 rounded-lg p-3 text-sm text-red-800">
              <strong>Warning:</strong> This pattern is in the &quot;always&quot; category and is critical for enrollment tracking. Changes may affect all enrollment sessions.
            </div>
          )}

          <FormJsonToggle
            jsonMode={jsonModeEdit}
            onToggleMode={(mode) => {
              if (mode) { onSetJsonText(JSON.stringify(editForm, null, 2)); }
              onSetJsonModeEdit(mode);
              onSetJsonError(null);
            }}
            jsonText={jsonText}
            onJsonTextChange={(text) => onSetJsonText(text)}
            jsonError={jsonError}
            onApplyJson={() => {
              try {
                const parsed = JSON.parse(jsonText) as PatternForm;
                if (!parsed.pattern) throw new Error("JSON must include pattern");
                setEditForm({ ...editForm, ...parsed });
                onSetJsonModeEdit(false);
                onSetJsonError(null);
              } catch (e) {
                onSetJsonError(e instanceof Error ? e.message : "Invalid JSON");
              }
            }}
            textareaRows={15}
            description="Edit the pattern as JSON."
          >
            <div className="space-y-4">
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Category</label>
                <select
                  value={editForm.category}
                  onChange={(e) => setEditForm({ ...editForm, category: e.target.value })}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:ring-2 focus:ring-green-500 focus:border-green-500"
                >
                  {CATEGORIES.map((c) => (
                    <option key={c} value={c}>{CATEGORY_LABELS[c]}</option>
                  ))}
                </select>
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Regex Pattern</label>
                <textarea
                  value={editForm.pattern}
                  onChange={(e) => setEditForm({ ...editForm, pattern: e.target.value })}
                  rows={3}
                  className="w-full font-mono text-sm px-3 py-2 border border-gray-300 rounded-lg text-gray-900 focus:ring-2 focus:ring-green-500 focus:border-green-500"
                  spellCheck={false}
                  placeholder="e.g., EMS Agent Started"
                />
                <p className="mt-1 text-xs text-gray-500">C# regex syntax. Capture groups are used by some actions to extract values.</p>
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Action</label>
                <select
                  value={editForm.action}
                  onChange={(e) => setEditForm({ ...editForm, action: e.target.value })}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:ring-2 focus:ring-green-500 focus:border-green-500"
                >
                  {ACTIONS.map((a) => (
                    <option key={a} value={a}>{ACTION_LABELS[a]} ({a})</option>
                  ))}
                </select>
                <p className="mt-1 text-xs text-gray-500">Actions are hardcoded in the agent. Only use actions the agent supports.</p>
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Description</label>
                <textarea
                  value={editForm.description}
                  onChange={(e) => setEditForm({ ...editForm, description: e.target.value })}
                  rows={2}
                  className="w-full text-sm px-3 py-2 border border-gray-300 rounded-lg text-gray-900 focus:ring-2 focus:ring-green-500 focus:border-green-500"
                  placeholder="What this pattern detects and why it matters..."
                />
              </div>

              <div className="flex items-center gap-2">
                <input
                  type="checkbox"
                  id="edit-enabled"
                  checked={editForm.enabled}
                  onChange={(e) => setEditForm({ ...editForm, enabled: e.target.checked })}
                  className="h-4 w-4 text-indigo-600 border-gray-300 rounded focus:ring-green-500"
                />
                <label htmlFor="edit-enabled" className="text-sm font-medium text-gray-700">Enabled</label>
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Parameters</label>
                {Object.keys(editForm.parameters).length > 0 && (
                  <div className="space-y-2 mb-2">
                    {Object.entries(editForm.parameters).map(([key, value]) => (
                      <div key={key} className="flex items-center gap-2">
                        <code className="px-2 py-1 bg-indigo-50 rounded text-xs font-mono text-indigo-700 min-w-[80px]">{key}</code>
                        <span className="text-gray-400">=</span>
                        <input
                          type="text"
                          value={value}
                          onChange={(e) => {
                            const updated = { ...editForm.parameters };
                            updated[key] = e.target.value;
                            setEditForm({ ...editForm, parameters: updated });
                          }}
                          className="flex-1 text-sm px-2 py-1 border border-gray-300 rounded text-gray-900 focus:ring-1 focus:ring-green-500"
                        />
                        <button
                          onClick={() => {
                            const updated = { ...editForm.parameters };
                            delete updated[key];
                            setEditForm({ ...editForm, parameters: updated });
                          }}
                          className="text-red-400 hover:text-red-600 p-1"
                          title="Remove parameter"
                        >
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                          </svg>
                        </button>
                      </div>
                    ))}
                  </div>
                )}
                <div className="flex items-center gap-2">
                  <input
                    type="text"
                    value={newParamKey}
                    onChange={(e) => onSetNewParamKey(e.target.value)}
                    placeholder="Key"
                    className="w-32 text-sm px-2 py-1 border border-gray-300 rounded text-gray-900 focus:ring-1 focus:ring-green-500"
                  />
                  <input
                    type="text"
                    value={newParamValue}
                    onChange={(e) => onSetNewParamValue(e.target.value)}
                    placeholder="Value"
                    className="flex-1 text-sm px-2 py-1 border border-gray-300 rounded text-gray-900 focus:ring-1 focus:ring-green-500"
                  />
                  <button
                    onClick={() => {
                      if (newParamKey.trim()) {
                        setEditForm({
                          ...editForm,
                          parameters: { ...editForm.parameters, [newParamKey.trim()]: newParamValue },
                        });
                        onSetNewParamKey("");
                        onSetNewParamValue("");
                      }
                    }}
                    disabled={!newParamKey.trim()}
                    className="px-3 py-1 text-xs bg-green-600 text-white rounded hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    Add
                  </button>
                </div>
              </div>
            </div>
          </FormJsonToggle>

          <div className="flex items-center justify-end space-x-3 mt-6 pt-4 border-t border-gray-200">
            <button
              onClick={onCancelEdit}
              className="px-4 py-2 text-sm bg-gray-100 text-gray-700 rounded-lg hover:bg-gray-200 transition-colors"
            >
              Cancel
            </button>
            <button
              onClick={() => onSaveEdit(pattern.patternId)}
              disabled={saving}
              className="px-4 py-2 text-sm bg-green-600 text-white rounded-lg hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
            >
              {saving ? (
                <>
                  <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                  <span>Saving...</span>
                </>
              ) : (
                <>
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                  </svg>
                  <span>Save Changes</span>
                </>
              )}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
