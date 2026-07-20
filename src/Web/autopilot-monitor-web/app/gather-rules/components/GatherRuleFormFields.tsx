"use client";

import { useMemo } from "react";
import {
  NewRuleForm,
  CATEGORIES,
  COLLECTOR_TYPES,
  COLLECTOR_TYPE_LABELS,
  TRIGGERS,
  SEVERITIES,
  TARGET_PLACEHOLDERS,
  TARGET_HINTS,
  GATHER_PHASES,
  EMIT_MODES,
  formatTrigger,
} from "../types";
import { KNOWN_EVENT_TYPES, findEventType } from "../eventTypes";
import { validateGatherRuleTarget } from "@/utils/guardValidation";
import { ValidationIndicator } from "@/components/ValidationIndicator";

interface GatherRuleFormFieldsProps {
  form: NewRuleForm;
  setForm: (f: NewRuleForm) => void;
  showRuleId: boolean;
  unrestrictedMode?: boolean;
  existingRuleIds?: string[];
}

export function GatherRuleFormFields({ form, setForm, showRuleId, unrestrictedMode = false, existingRuleIds }: GatherRuleFormFieldsProps) {
  const targetValidation = useMemo(
    () => form.target.trim() ? validateGatherRuleTarget(form.collectorType, form.target, unrestrictedMode) : null,
    [form.collectorType, form.target, unrestrictedMode]
  );
  const ruleIdTrimmed = form.ruleId.trim().toLowerCase();
  const isDuplicate = showRuleId && existingRuleIds && ruleIdTrimmed.length > 0
    && existingRuleIds.some(id => id.toLowerCase() === ruleIdTrimmed);

  return (
    <div className="space-y-5">
      {/* Row 1: Rule ID (create only), Title */}
      <div className={`grid grid-cols-1 ${showRuleId ? "sm:grid-cols-2" : ""} gap-4`}>
        {showRuleId && (
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Rule ID <span className="text-red-500">*</span>
            </label>
            <input
              type="text"
              value={form.ruleId}
              onChange={(e) => setForm({ ...form, ruleId: e.target.value })}
              placeholder="e.g., custom-network-check"
              autoComplete="off"
              className={`w-full px-4 py-2 border rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 transition-colors ${isDuplicate ? "border-red-400 focus:ring-red-300 focus:border-red-400" : "border-gray-300 focus:ring-indigo-500 focus:border-indigo-500"}`}
            />
            {isDuplicate && (
              <p className="mt-1 text-xs text-red-600">A rule with this ID already exists. Please choose a unique Rule ID.</p>
            )}
          </div>
        )}
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">
            Title <span className="text-red-500">*</span>
          </label>
          <input
            type="text"
            value={form.title}
            onChange={(e) => setForm({ ...form, title: e.target.value })}
            placeholder="e.g., Custom Network Check"
            autoComplete="off"
            className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
          />
        </div>
      </div>

      {/* Description */}
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Description</label>
        <textarea
          value={form.description}
          onChange={(e) => setForm({ ...form, description: e.target.value })}
          placeholder="Describe what this rule collects and why..."
          rows={2}
          className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors resize-none"
        />
      </div>

      {/* Row 2: Category, Collector Type */}
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Category</label>
          <select
            value={form.category}
            onChange={(e) => setForm({ ...form, category: e.target.value })}
            className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
          >
            {CATEGORIES.map((cat) => (
              <option key={cat} value={cat}>
                {cat.charAt(0).toUpperCase() + cat.slice(1)}
              </option>
            ))}
          </select>
        </div>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Collector Type</label>
          <select
            value={form.collectorType}
            onChange={(e) => setForm({ ...form, collectorType: e.target.value })}
            className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
          >
            {COLLECTOR_TYPES.map((ct) => (
              <option key={ct} value={ct}>
                {COLLECTOR_TYPE_LABELS[ct] || (ct.charAt(0).toUpperCase() + ct.slice(1))}
              </option>
            ))}
          </select>
        </div>
      </div>

      {/* Target */}
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">
          Target <span className="text-red-500">*</span>
        </label>
        <input
          type="text"
          value={form.target}
          onChange={(e) => setForm({ ...form, target: e.target.value })}
          placeholder={TARGET_PLACEHOLDERS[form.collectorType] || "Target for data collection"}
          autoComplete="off"
          className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
        />
        <div className="flex items-center gap-2 mt-1">
          <p className="text-xs text-gray-400">{TARGET_HINTS[form.collectorType] || "Registry path, WMI class, event log name, file path, or command depending on collector type"}</p>
          <ValidationIndicator result={targetValidation} />
        </div>
      </div>

      {/* Registry: optional Value Name */}
      {form.collectorType === "registry" && (
        <div className="space-y-3">
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Value Name</label>
            <input
              type="text"
              value={form.valueName}
              onChange={(e) => setForm({ ...form, valueName: e.target.value, listSubkeys: false })}
              placeholder="e.g., IsRecoveryAllowed (leave empty to read all values)"
              autoComplete="off"
              disabled={form.listSubkeys}
              className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors disabled:opacity-50 disabled:bg-gray-50"
            />
            <p className="text-xs text-gray-400 mt-1">Specific registry value to read. Leave empty to read all values in the key.</p>
          </div>
          <label className="flex items-center space-x-2 text-sm text-gray-700 cursor-pointer">
            <input
              type="checkbox"
              checked={form.listSubkeys}
              onChange={(e) => setForm({ ...form, listSubkeys: e.target.checked, valueName: e.target.checked ? "" : form.valueName })}
              className="rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
            />
            <span>List subkeys instead of values</span>
          </label>
        </div>
      )}

      {/* EventLog: optional filters */}
      {form.collectorType === "eventlog" && (
        <div className="space-y-4">
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Event ID</label>
              <input
                type="text"
                value={form.eventId}
                onChange={(e) => setForm({ ...form, eventId: e.target.value })}
                placeholder="e.g., 62407 (leave empty for all events)"
                autoComplete="off"
                className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
              />
              <p className="text-xs text-gray-400 mt-1">Filter by specific Event ID. Leave empty to collect all events.</p>
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Max Entries</label>
              <input
                type="number"
                min={1}
                max={50}
                value={form.maxEntries}
                onChange={(e) => setForm({ ...form, maxEntries: e.target.value })}
                placeholder="10"
                className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
              />
              <p className="text-xs text-gray-400 mt-1">Maximum number of events to return (1-50, default: 10).</p>
            </div>
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Source / Provider</label>
              <input
                type="text"
                value={form.source}
                onChange={(e) => setForm({ ...form, source: e.target.value })}
                placeholder="e.g., Microsoft-Windows-Kernel-General"
                autoComplete="off"
                className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
              />
              <p className="text-xs text-gray-400 mt-1">Filter by event provider/source name. Leave empty for all sources.</p>
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Message Filter</label>
              <input
                type="text"
                value={form.messageFilter}
                onChange={(e) => setForm({ ...form, messageFilter: e.target.value })}
                placeholder="e.g., *ESPProgress* (leave empty for no filter)"
                autoComplete="off"
                className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
              />
              <p className="text-xs text-gray-400 mt-1">Filter by message text. Use * as wildcard prefix/suffix.</p>
            </div>
          </div>
        </div>
      )}

      {/* File: optional parameters */}
      {form.collectorType === "file" && (
        <div>
          <label className="flex items-center gap-2 cursor-pointer">
            <input
              type="checkbox"
              checked={form.readContent}
              onChange={(e) => setForm({ ...form, readContent: e.target.checked })}
              className="w-4 h-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
            />
            <span className="text-sm font-medium text-gray-700">Read file content</span>
          </label>
          <p className="text-xs text-gray-400 mt-1 ml-6">Read the last 4000 characters of the file (only files &lt;50 KB). Useful for log files and setup logs.</p>
        </div>
      )}

      {/* LogParser: format, pattern, max lines, track position */}
      {form.collectorType === "logparser" && (
        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Log Format</label>
            <select
              value={form.logFormat}
              onChange={(e) => setForm({ ...form, logFormat: e.target.value })}
              className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
            >
              <option value="cmtrace">CMTrace (default)</option>
              <option value="text">Plain Text</option>
            </select>
            <p className="text-xs text-gray-400 mt-1">
              {form.logFormat === "cmtrace"
                ? "Parses CMTrace-format logs. Regex is matched against the message field. Extracts timestamp, component, and log type."
                : "Parses plain text files line by line. Regex is matched against the raw line. Use for any text-based log format."}
            </p>
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Pattern <span className="text-red-500">*</span>
            </label>
            <input
              type="text"
              value={form.logPattern}
              onChange={(e) => setForm({ ...form, logPattern: e.target.value })}
              placeholder={form.logFormat === "text"
                ? `e.g., (?<timestamp>\\d{4}-\\d{2}-\\d{2}).*(?<level>ERROR|WARN|INFO).*(?<message>.*)`
                : `e.g., (?<action>Install|Uninstall).*(?<appName>[A-Za-z0-9_-]+)`}
              autoComplete="off"
              className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 font-mono focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
            />
            <p className="text-xs text-gray-400 mt-1">Regex with named capture groups. Each match emits a separate event. Named groups become event data fields.</p>
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Max Lines</label>
              <input
                type="number"
                min={1}
                max={10000}
                value={form.maxLines}
                onChange={(e) => setForm({ ...form, maxLines: e.target.value })}
                placeholder="1000"
                className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
              />
              <p className="text-xs text-gray-400 mt-1">Max lines to parse per file per execution (default: 1000).</p>
            </div>
            <div className="flex flex-col justify-center">
              <label className="flex items-center gap-2 cursor-pointer mt-4">
                <input
                  type="checkbox"
                  checked={form.trackPosition}
                  onChange={(e) => setForm({ ...form, trackPosition: e.target.checked })}
                  className="w-4 h-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
                />
                <span className="text-sm font-medium text-gray-700">Track position</span>
              </label>
              <p className="text-xs text-gray-400 mt-1 ml-6">Resume from last read position across executions (recommended).</p>
            </div>
          </div>
        </div>
      )}

      {/* JSON: JSONPath expression + optional max results */}
      {form.collectorType === "json" && (
        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              JSONPath Expression <span className="text-red-500">*</span>
            </label>
            <input
              type="text"
              value={form.jsonPath}
              onChange={(e) => setForm({ ...form, jsonPath: e.target.value })}
              placeholder="e.g., $.settings.tenantId or $..errorCode"
              autoComplete="off"
              className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 font-mono focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
            />
            <p className="text-xs text-gray-400 mt-1">
              JSONPath query to extract values. Examples: <code className="bg-gray-100 px-1 rounded">$.key</code> (root property),{" "}
              <code className="bg-gray-100 px-1 rounded">$..name</code> (recursive search),{" "}
              <code className="bg-gray-100 px-1 rounded">$.items[0]</code> (array index),{" "}
              <code className="bg-gray-100 px-1 rounded">$.items[?(@.active==true)]</code> (filter).
            </p>
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Max Results</label>
            <input
              type="number"
              min={1}
              max={100}
              value={form.maxResults}
              onChange={(e) => setForm({ ...form, maxResults: e.target.value })}
              placeholder="20"
              className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
            />
            <p className="text-xs text-gray-400 mt-1">Maximum number of matches to return (1-100, default: 20).</p>
          </div>
        </div>
      )}

      {/* XML: XPath expression + optional namespaces + max results */}
      {form.collectorType === "xml" && (
        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              XPath Expression <span className="text-red-500">*</span>
            </label>
            <input
              type="text"
              value={form.xpath}
              onChange={(e) => setForm({ ...form, xpath: e.target.value })}
              placeholder="e.g., /configuration/appSettings/add[@key='Setting1']/@value"
              autoComplete="off"
              className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 font-mono focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
            />
            <p className="text-xs text-gray-400 mt-1">
              XPath query to extract values. Examples: <code className="bg-gray-100 px-1 rounded">/root/element</code> (path),{" "}
              <code className="bg-gray-100 px-1 rounded">//element</code> (anywhere),{" "}
              <code className="bg-gray-100 px-1 rounded">/root/item[@attr=&apos;value&apos;]</code> (filter),{" "}
              <code className="bg-gray-100 px-1 rounded">/root/element/text()</code> (text content).
            </p>
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Namespaces</label>
              <input
                type="text"
                value={form.xmlNamespaces}
                onChange={(e) => setForm({ ...form, xmlNamespaces: e.target.value })}
                placeholder="e.g., ns=http://schemas.example.com/config"
                autoComplete="off"
                className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
              />
              <p className="text-xs text-gray-400 mt-1">
                Optional. Format: <code className="bg-gray-100 px-1 rounded">prefix=uri;prefix2=uri2</code>
              </p>
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Max Results</label>
              <input
                type="number"
                min={1}
                max={100}
                value={form.maxResults}
                onChange={(e) => setForm({ ...form, maxResults: e.target.value })}
                placeholder="20"
                className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
              />
              <p className="text-xs text-gray-400 mt-1">Max matches to return (1-100, default: 20).</p>
            </div>
          </div>
        </div>
      )}

      {/* Trigger */}
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Trigger</label>
        <select
          value={form.trigger}
          onChange={(e) => setForm({ ...form, trigger: e.target.value })}
          className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
        >
          {TRIGGERS.map((t) => (
            <option key={t} value={t}>{formatTrigger(t)}</option>
          ))}
        </select>
      </div>

      {/* Conditional Trigger Fields */}
      {form.trigger === "interval" && (
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Interval (seconds)</label>
          <input
            type="number"
            min={5}
            max={3600}
            value={form.intervalSeconds}
            onChange={(e) => setForm({ ...form, intervalSeconds: parseInt(e.target.value) || 60 })}
            className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
          />
          <p className="text-xs text-gray-400 mt-1">How often to run this rule (5 - 3600 seconds)</p>
        </div>
      )}

      {form.trigger === "phase_change" && (
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Trigger Phase</label>
          <select
            value={form.triggerPhase}
            onChange={(e) => setForm({ ...form, triggerPhase: e.target.value })}
            className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
          >
            <option value="">Any phase change</option>
            {form.triggerPhase && !GATHER_PHASES.some((p) => p.value === form.triggerPhase) && (
              <option value={form.triggerPhase}>{form.triggerPhase} (legacy value)</option>
            )}
            {GATHER_PHASES.map((p) => (
              <option key={p.value} value={p.value}>{p.label}</option>
            ))}
          </select>
          <p className="text-xs text-gray-400 mt-1">Run this rule when the enrollment reaches this phase. &quot;Any phase change&quot; fires once per phase transition.</p>
        </div>
      )}

      {form.trigger === "on_event" && (
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">
            Trigger Event Type
            <span className="ml-2 text-xs text-gray-500 font-normal">
              ({KNOWN_EVENT_TYPES.length} known event types — start typing to filter)
            </span>
          </label>
          <input
            type="text"
            list="gather-rule-event-types"
            value={form.triggerEventType}
            onChange={(e) => setForm({ ...form, triggerEventType: e.target.value })}
            placeholder="e.g., session_stalled, enrollment_failed, modern_deployment_error"
            autoComplete="off"
            className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
          />
          <datalist id="gather-rule-event-types">
            {KNOWN_EVENT_TYPES.map((et) => (
              <option key={et.value} value={et.value}>{et.description}</option>
            ))}
          </datalist>
          {form.triggerEventType && findEventType(form.triggerEventType) && (
            <p className="text-xs text-gray-500 mt-1">
              {findEventType(form.triggerEventType)!.description}
            </p>
          )}
          {form.triggerEventType && !findEventType(form.triggerEventType) && (
            <p className="text-xs text-amber-600 mt-1">
              Custom event type — make sure the agent actually emits this value.
            </p>
          )}
        </div>
      )}

      {/* Row: Active During (phase scope) + Emit Mode */}
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Active During</label>
          <select
            value={form.scopeMode}
            onChange={(e) => {
              const scopeMode = e.target.value as NewRuleForm["scopeMode"];
              // Keep the form state consistent: only the fields of the selected mode survive.
              setForm({
                ...form,
                scopeMode,
                activePhases: scopeMode === "during" ? form.activePhases : [],
                activeFromPhase: scopeMode === "from" ? form.activeFromPhase : "",
              });
            }}
            className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
          >
            <option value="always">All phases (always)</option>
            <option value="during">Only during specific phases</option>
            <option value="from">From a phase onwards</option>
          </select>
          <p className="text-xs text-gray-400 mt-1">Restrict when this rule runs. Outside its scope the rule is idle — interval rules stop polling, triggers are ignored.</p>
        </div>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Emit Mode</label>
          <select
            value={form.emitMode}
            onChange={(e) => setForm({ ...form, emitMode: e.target.value })}
            className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
          >
            {EMIT_MODES.map((m) => (
              <option key={m.value} value={m.value}>{m.label}</option>
            ))}
          </select>
          <p className="text-xs text-gray-400 mt-1">On change: polls on the trigger cadence but only emits an event when the collected result changes. The first collection always emits.</p>
        </div>
      </div>

      {form.scopeMode === "during" && (
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Active Phases</label>
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-2">
            {GATHER_PHASES.map((p) => (
              <label key={p.value} className="flex items-center space-x-2 text-sm text-gray-700 cursor-pointer">
                <input
                  type="checkbox"
                  checked={form.activePhases.includes(p.value)}
                  onChange={(e) =>
                    setForm({
                      ...form,
                      activePhases: e.target.checked
                        ? [...form.activePhases, p.value]
                        : form.activePhases.filter((x) => x !== p.value),
                    })
                  }
                  className="rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
                />
                <span>{p.label}</span>
              </label>
            ))}
          </div>
          <p className="text-xs text-gray-400 mt-1">The rule runs only while the enrollment is in one of the selected phases. No selection = all phases.</p>
        </div>
      )}

      {form.scopeMode === "from" && (
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Active From Phase</label>
          <select
            value={form.activeFromPhase}
            onChange={(e) => setForm({ ...form, activeFromPhase: e.target.value })}
            className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
          >
            <option value="">Select a phase…</option>
            {GATHER_PHASES.map((p) => (
              <option key={p.value} value={p.value}>{p.label}</option>
            ))}
          </select>
          <p className="text-xs text-gray-400 mt-1">Once the enrollment reaches this phase the rule activates and stays active for the rest of the session. No selection = all phases.</p>
        </div>
      )}

      {/* Row 3: Output Event Type, Output Severity */}
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">
            Output Event Type <span className="text-red-500">*</span>
          </label>
          <input
            type="text"
            value={form.outputEventType}
            onChange={(e) => setForm({ ...form, outputEventType: e.target.value })}
            placeholder="e.g., CustomNetworkStatus"
            autoComplete="off"
            className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
          />
        </div>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Output Severity</label>
          <select
            value={form.outputSeverity}
            onChange={(e) => setForm({ ...form, outputSeverity: e.target.value })}
            className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
          >
            {SEVERITIES.map((s) => (
              <option key={s} value={s}>
                {s.charAt(0).toUpperCase() + s.slice(1)}
              </option>
            ))}
          </select>
        </div>
      </div>
    </div>
  );
}
