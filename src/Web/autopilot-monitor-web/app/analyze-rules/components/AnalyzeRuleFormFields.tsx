"use client";

import {
  RuleForm, RuleCondition, RulePrecondition,
  CATEGORIES, SEVERITIES, TRIGGERS, OPERATORS, SOURCES, PRECONDITION_OPERATORS,
  EMPTY_CONDITION, EMPTY_FACTOR, EMPTY_PRECONDITION,
} from "../types";

interface AnalyzeRuleFormFieldsProps {
  form: RuleForm;
  setForm: (f: RuleForm) => void;
  showRuleId: boolean;
  existingRuleIds?: string[];
}

export default function AnalyzeRuleFormFields({ form, setForm, showRuleId, existingRuleIds }: AnalyzeRuleFormFieldsProps) {
  const ruleIdTrimmed = form.ruleId.trim().toLowerCase();
  const isDuplicate = showRuleId && existingRuleIds && ruleIdTrimmed.length > 0
    && existingRuleIds.some(id => id.toLowerCase() === ruleIdTrimmed);

  return (
    <div className="space-y-5">
      {/* Basic Fields */}
      <div className={`grid grid-cols-1 ${showRuleId ? "sm:grid-cols-2" : ""} gap-4`}>
        {showRuleId && (
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Rule ID <span className="text-red-500">*</span></label>
            <input type="text" value={form.ruleId} onChange={(e) => setForm({ ...form, ruleId: e.target.value })} placeholder="e.g., ANALYZE-CUSTOM-001" autoComplete="off" className={`w-full px-4 py-2 border rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 ${isDuplicate ? "border-red-400 focus:ring-red-300 focus:border-red-400" : "border-gray-300 focus:ring-indigo-500 focus:border-indigo-500"}`} />
            {isDuplicate && (
              <p className="mt-1 text-xs text-red-600">A rule with this ID already exists. Please choose a unique Rule ID.</p>
            )}
          </div>
        )}
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Title <span className="text-red-500">*</span></label>
          <input type="text" value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} placeholder="e.g., Proxy Authentication Failure" autoComplete="off" className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500" />
        </div>
      </div>

      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Description</label>
        <textarea value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} placeholder="Describe what this rule detects..." rows={2} className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 resize-none" />
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Severity</label>
          <select value={form.severity} onChange={(e) => setForm({ ...form, severity: e.target.value })} className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500">
            {SEVERITIES.map((s) => (<option key={s} value={s}>{s.charAt(0).toUpperCase() + s.slice(1)}</option>))}
          </select>
        </div>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Category</label>
          <select value={form.category} onChange={(e) => setForm({ ...form, category: e.target.value })} className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500">
            {CATEGORIES.map((cat) => (<option key={cat} value={cat}>{cat.charAt(0).toUpperCase() + cat.slice(1)}</option>))}
          </select>
        </div>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Trigger Type</label>
          <select value={form.trigger} onChange={(e) => setForm({ ...form, trigger: e.target.value })} className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500">
            {TRIGGERS.map((t) => (<option key={t} value={t}>{t.charAt(0).toUpperCase() + t.slice(1)}</option>))}
          </select>
        </div>
      </div>

      {/* Preconditions (device-fact gates evaluated BEFORE conditions) */}
      <div>
        <div className="flex items-center justify-between mb-2">
          <div>
            <label className="block text-sm font-semibold text-gray-700">Preconditions</label>
            <p className="text-xs text-gray-500">Optional. Rule is silently skipped (no result) when ANY precondition fails. Useful e.g. to skip on virtual machines.</p>
          </div>
          <button type="button" onClick={() => setForm({ ...form, preconditions: [...form.preconditions, { ...EMPTY_PRECONDITION }] })} className="text-xs text-indigo-600 hover:text-indigo-800 font-medium">+ Add Precondition</button>
        </div>
        {form.preconditions.length === 0 ? (
          <p className="text-xs text-gray-400 italic">No preconditions. Rule applies to every device.</p>
        ) : (
          <div className="space-y-2">
            {form.preconditions.map((pre, idx) => {
              const updatePre = (patch: Partial<RulePrecondition>) => {
                const p = [...form.preconditions];
                p[idx] = { ...p[idx], ...patch };
                setForm({ ...form, preconditions: p });
              };
              return (
                <div key={idx} className="border border-amber-200 bg-amber-50 rounded-lg p-3 space-y-2">
                  <div className="flex items-center justify-between">
                    <span className="text-xs font-medium text-amber-700">Precondition {idx + 1}</span>
                    <button type="button" onClick={() => setForm({ ...form, preconditions: form.preconditions.filter((_, i) => i !== idx) })} className="text-xs text-red-500 hover:text-red-700">Remove</button>
                  </div>
                  <div className="grid grid-cols-1 sm:grid-cols-4 gap-2">
                    <input type="text" value={pre.eventType} onChange={(e) => updatePre({ eventType: e.target.value })} placeholder="Event type (e.g. hardware_spec)" autoComplete="off" className="px-3 py-1.5 border border-amber-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-amber-500" />
                    <input type="text" value={pre.dataField} onChange={(e) => updatePre({ dataField: e.target.value })} placeholder="Data field (e.g. isVirtualMachine)" autoComplete="off" className="px-3 py-1.5 border border-amber-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-amber-500" />
                    <select value={pre.operator} onChange={(e) => updatePre({ operator: e.target.value })} className="px-3 py-1.5 border border-amber-300 rounded text-sm text-gray-900 bg-white focus:outline-none focus:ring-1 focus:ring-amber-500">
                      {PRECONDITION_OPERATORS.map((o) => (<option key={o} value={o}>{o}</option>))}
                    </select>
                    <input type="text" value={pre.value} onChange={(e) => updatePre({ value: e.target.value })} placeholder="Value (e.g. false)" autoComplete="off" className="px-3 py-1.5 border border-amber-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-amber-500" />
                  </div>
                  <input type="text" value={pre.description ?? ""} onChange={(e) => updatePre({ description: e.target.value })} placeholder="Description (optional, e.g. 'skip on VMs')" autoComplete="off" className="w-full px-3 py-1.5 border border-amber-200 rounded text-sm text-gray-700 placeholder-gray-400 bg-white focus:outline-none focus:ring-1 focus:ring-amber-500" />
                </div>
              );
            })}
          </div>
        )}
      </div>

      {/* Conditions */}
      <div>
        <div className="flex items-center justify-between mb-2">
          <label className="block text-sm font-semibold text-gray-700">Conditions</label>
          <button type="button" onClick={() => setForm({ ...form, conditions: [...form.conditions, { ...EMPTY_CONDITION }] })} className="text-xs text-indigo-600 hover:text-indigo-800 font-medium">+ Add Condition</button>
        </div>
        <div className="space-y-3">
          {form.conditions.map((cond, idx) => {
            const isCorrelation = cond.source === "event_correlation";
            const isArray = cond.source === "event_data_array";
            const updateCond = (patch: Partial<RuleCondition>) => {
              const c = [...form.conditions];
              c[idx] = { ...c[idx], ...patch };
              setForm({ ...form, conditions: c });
            };
            return (
              <div key={idx} className={`border rounded-lg p-3 space-y-2 ${isCorrelation ? "bg-indigo-50 border-indigo-200" : "bg-gray-50 border-gray-200"}`}>
                <div className="flex items-center justify-between">
                  <span className="text-xs font-medium text-gray-500">
                    Condition {idx + 1}
                    {isCorrelation && <span className="ml-2 px-1.5 py-0.5 bg-indigo-100 text-indigo-700 rounded text-xs">event_correlation</span>}
                    {isArray && <span className="ml-2 px-1.5 py-0.5 bg-teal-100 text-teal-700 rounded text-xs">event_data_array</span>}
                  </span>
                  {form.conditions.length > 1 && (
                    <button type="button" onClick={() => setForm({ ...form, conditions: form.conditions.filter((_, i) => i !== idx) })} className="text-xs text-red-500 hover:text-red-700">Remove</button>
                  )}
                </div>

                {/* Row 1: Signal, Source, Event Type A */}
                <div className="grid grid-cols-1 sm:grid-cols-3 gap-2">
                  <input type="text" value={cond.signal} onChange={(e) => updateCond({ signal: e.target.value })} placeholder="Signal name" autoComplete="off" className="px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                  <select value={cond.source} onChange={(e) => updateCond({ source: e.target.value })} className="px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 bg-white focus:outline-none focus:ring-1 focus:ring-indigo-500">
                    {SOURCES.map((s) => (<option key={s} value={s}>{s}</option>))}
                  </select>
                  <input type="text" value={cond.eventType} onChange={(e) => updateCond({ eventType: e.target.value })} placeholder={isCorrelation ? "Event A type (e.g. app_install_completed)" : "Event type"} autoComplete="off" className="px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                </div>

                {/* Row 2: Data field / Operator / Value / Required */}
                <div className="grid grid-cols-1 sm:grid-cols-4 gap-2">
                  <input type="text" value={cond.dataField} onChange={(e) => updateCond({ dataField: e.target.value })} placeholder={isCorrelation ? "Filter field on Event B" : isArray ? "Array field (e.g. artifacts)" : "Data field"} autoComplete="off" className="px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                  <select value={cond.operator} onChange={(e) => updateCond({ operator: e.target.value })} className="px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 bg-white focus:outline-none focus:ring-1 focus:ring-indigo-500">
                    {OPERATORS.map((o) => (<option key={o} value={o}>{o}</option>))}
                  </select>
                  <input type="text" value={cond.value} onChange={(e) => updateCond({ value: e.target.value })} placeholder={isCorrelation ? "Filter value on Event B" : isArray ? "Value / allow-list regex" : "Value"} autoComplete="off" className="px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                  <label className="flex items-center space-x-2 text-sm text-gray-700">
                    <input type="checkbox" checked={cond.required} onChange={(e) => updateCond({ required: e.target.checked })} className="rounded border-gray-300 text-indigo-600 focus:ring-indigo-500" />
                    <span>Required</span>
                  </label>
                </div>

                {/* event_correlation extra fields */}
                {isCorrelation && (
                  <div className="pt-2 border-t border-indigo-200 space-y-2">
                    <p className="text-xs font-medium text-indigo-600">Correlation settings</p>

                    {/* Correlate Event Type + Join Field + Time Window */}
                    <div className="grid grid-cols-1 sm:grid-cols-3 gap-2">
                      <div>
                        <label className="block text-xs text-gray-500 mb-1">Event B type <span className="text-red-500">*</span></label>
                        <input type="text" value={cond.correlateEventType ?? ""} onChange={(e) => updateCond({ correlateEventType: e.target.value })} placeholder="e.g. app_install_failed" autoComplete="off" className="w-full px-3 py-1.5 border border-indigo-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                      </div>
                      <div>
                        <label className="block text-xs text-gray-500 mb-1">Join field <span className="text-red-500">*</span></label>
                        <input type="text" value={cond.joinField ?? ""} onChange={(e) => updateCond({ joinField: e.target.value })} placeholder="e.g. appId" autoComplete="off" className="w-full px-3 py-1.5 border border-indigo-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                      </div>
                      <div>
                        <label className="block text-xs text-gray-500 mb-1">Time window (seconds, optional)</label>
                        <input type="number" min={0} value={cond.timeWindowSeconds ?? ""} onChange={(e) => updateCond({ timeWindowSeconds: e.target.value === "" ? null : parseInt(e.target.value) || 0 })} placeholder="e.g. 300" className="w-full px-3 py-1.5 border border-indigo-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                      </div>
                    </div>

                    {/* Event A Filter */}
                    <div>
                      <label className="block text-xs text-gray-500 mb-1">Event A filter (optional)</label>
                      <div className="grid grid-cols-1 sm:grid-cols-3 gap-2">
                        <input type="text" value={cond.eventAFilterField ?? ""} onChange={(e) => updateCond({ eventAFilterField: e.target.value })} placeholder="Filter field on Event A" autoComplete="off" className="px-3 py-1.5 border border-indigo-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                        <select value={cond.eventAFilterOperator ?? "equals"} onChange={(e) => updateCond({ eventAFilterOperator: e.target.value })} className="px-3 py-1.5 border border-indigo-300 rounded text-sm text-gray-900 bg-white focus:outline-none focus:ring-1 focus:ring-indigo-500">
                          {OPERATORS.map((o) => (<option key={o} value={o}>{o}</option>))}
                        </select>
                        <input type="text" value={cond.eventAFilterValue ?? ""} onChange={(e) => updateCond({ eventAFilterValue: e.target.value })} placeholder="Filter value on Event A" autoComplete="off" className="px-3 py-1.5 border border-indigo-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                      </div>
                    </div>
                  </div>
                )}

                {/* event_data_array extra field */}
                {isArray && (
                  <div className="pt-2 border-t border-teal-200 space-y-2">
                    <p className="text-xs font-medium text-teal-700">Array settings</p>
                    <div>
                      <label className="block text-xs text-gray-500 mb-1">Item field <span className="text-gray-400">(sub-field tested on each array element)</span></label>
                      <input type="text" value={cond.itemField ?? ""} onChange={(e) => updateCond({ itemField: e.target.value })} placeholder="e.g. identity" autoComplete="off" className="w-full px-3 py-1.5 border border-teal-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-teal-500" />
                    </div>
                    <p className="text-xs text-gray-500">
                      <strong>Data field</strong> is the array to iterate (e.g. <code className="bg-gray-100 px-1 rounded">artifacts</code>); the operator/value test runs against <strong>Item field</strong> on each element. The condition matches when <strong>any</strong> element matches — e.g. <code className="bg-gray-100 px-1 rounded">not_regex</code> against an allow-list fires for any element not on the list.
                    </p>
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </div>

      {/* Confidence Scoring */}
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Base Confidence (%)</label>
          <input type="number" min={0} max={100} value={form.baseConfidence} onChange={(e) => setForm({ ...form, baseConfidence: parseInt(e.target.value) || 0 })} className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500" />
        </div>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Confidence Threshold (%)</label>
          <input type="number" min={0} max={100} value={form.confidenceThreshold} onChange={(e) => setForm({ ...form, confidenceThreshold: parseInt(e.target.value) || 0 })} className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500" />
        </div>
      </div>

      {/* Confidence Factors */}
      <div>
        <div className="flex items-center justify-between mb-2">
          <label className="block text-sm font-semibold text-gray-700">Confidence Factors</label>
          <button type="button" onClick={() => setForm({ ...form, confidenceFactors: [...form.confidenceFactors, { ...EMPTY_FACTOR }] })} className="text-xs text-indigo-600 hover:text-indigo-800 font-medium">+ Add Factor</button>
        </div>
        {form.confidenceFactors.length === 0 ? (
          <p className="text-xs text-gray-400 italic">No confidence factors. Click &quot;+ Add Factor&quot; to add one.</p>
        ) : (
          <div className="space-y-2">
            {form.confidenceFactors.map((factor, idx) => (
              <div key={idx} className="flex items-center gap-2">
                <input type="text" value={factor.signal} onChange={(e) => { const f = [...form.confidenceFactors]; f[idx] = { ...f[idx], signal: e.target.value }; setForm({ ...form, confidenceFactors: f }); }} placeholder="Signal" autoComplete="off" className="flex-1 px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                <input type="text" value={factor.condition} onChange={(e) => { const f = [...form.confidenceFactors]; f[idx] = { ...f[idx], condition: e.target.value }; setForm({ ...form, confidenceFactors: f }); }} placeholder="Condition" autoComplete="off" className="flex-1 px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                <input type="number" value={factor.weight} onChange={(e) => { const f = [...form.confidenceFactors]; f[idx] = { ...f[idx], weight: parseInt(e.target.value) || 0 }; setForm({ ...form, confidenceFactors: f }); }} className="w-20 px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                <span className="text-xs text-gray-500">%</span>
                <button type="button" onClick={() => setForm({ ...form, confidenceFactors: form.confidenceFactors.filter((_, i) => i !== idx) })} className="text-red-400 hover:text-red-600">
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" /></svg>
                </button>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Explanation */}
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Explanation</label>
        <textarea value={form.explanation} onChange={(e) => setForm({ ...form, explanation: e.target.value })} placeholder="Detailed explanation shown when this rule fires..." rows={3} className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 resize-none" />
      </div>

      {/* Remediation Steps */}
      <div>
        <div className="flex items-center justify-between mb-2">
          <label className="block text-sm font-semibold text-gray-700">Remediation Steps</label>
          <button type="button" onClick={() => setForm({ ...form, remediation: [...form.remediation, { title: "", steps: [""] }] })} className="text-xs text-indigo-600 hover:text-indigo-800 font-medium">+ Add Section</button>
        </div>
        {form.remediation.length === 0 ? (
          <p className="text-xs text-gray-400 italic">No remediation steps. Click &quot;+ Add Section&quot; to add one.</p>
        ) : (
          <div className="space-y-3">
            {form.remediation.map((rem, rIdx) => (
              <div key={rIdx} className="bg-green-50 border border-green-200 rounded-lg p-3 space-y-2">
                <div className="flex items-center justify-between">
                  <input type="text" value={rem.title} onChange={(e) => { const r = [...form.remediation]; r[rIdx] = { ...r[rIdx], title: e.target.value }; setForm({ ...form, remediation: r }); }} placeholder="Section title" autoComplete="off" className="flex-1 px-3 py-1.5 border border-green-300 rounded text-sm text-gray-900 placeholder-gray-400 bg-white focus:outline-none focus:ring-1 focus:ring-green-500" />
                  <button type="button" onClick={() => setForm({ ...form, remediation: form.remediation.filter((_, i) => i !== rIdx) })} className="ml-2 text-red-400 hover:text-red-600 text-xs">Remove</button>
                </div>
                {rem.steps.map((step, sIdx) => (
                  <div key={sIdx} className="flex items-center gap-2">
                    <span className="text-xs text-gray-500 w-5 text-right">{sIdx + 1}.</span>
                    <input type="text" value={step} onChange={(e) => { const r = [...form.remediation]; const steps = [...r[rIdx].steps]; steps[sIdx] = e.target.value; r[rIdx] = { ...r[rIdx], steps }; setForm({ ...form, remediation: r }); }} placeholder="Step description" autoComplete="off" className="flex-1 px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-green-500" />
                    {rem.steps.length > 1 && (
                      <button type="button" onClick={() => { const r = [...form.remediation]; r[rIdx] = { ...r[rIdx], steps: r[rIdx].steps.filter((_, i) => i !== sIdx) }; setForm({ ...form, remediation: r }); }} className="text-red-400 hover:text-red-600">
                        <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" /></svg>
                      </button>
                    )}
                  </div>
                ))}
                <button type="button" onClick={() => { const r = [...form.remediation]; r[rIdx] = { ...r[rIdx], steps: [...r[rIdx].steps, ""] }; setForm({ ...form, remediation: r }); }} className="text-xs text-green-600 hover:text-green-800 font-medium">+ Add Step</button>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Related Docs */}
      <div>
        <div className="flex items-center justify-between mb-2">
          <label className="block text-sm font-semibold text-gray-700">Related Documentation</label>
          <button type="button" onClick={() => setForm({ ...form, relatedDocs: [...form.relatedDocs, { title: "", url: "" }] })} className="text-xs text-indigo-600 hover:text-indigo-800 font-medium">+ Add Link</button>
        </div>
        {form.relatedDocs.length === 0 ? (
          <p className="text-xs text-gray-400 italic">No related docs. Click &quot;+ Add Link&quot; to add one.</p>
        ) : (
          <div className="space-y-2">
            {form.relatedDocs.map((doc, idx) => (
              <div key={idx} className="flex items-center gap-2">
                <input type="text" value={doc.title} onChange={(e) => { const d = [...form.relatedDocs]; d[idx] = { ...d[idx], title: e.target.value }; setForm({ ...form, relatedDocs: d }); }} placeholder="Link title" autoComplete="off" className="flex-1 px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                <input type="text" value={doc.url} onChange={(e) => { const d = [...form.relatedDocs]; d[idx] = { ...d[idx], url: e.target.value }; setForm({ ...form, relatedDocs: d }); }} placeholder="https://..." autoComplete="off" className="flex-1 px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                <button type="button" onClick={() => setForm({ ...form, relatedDocs: form.relatedDocs.filter((_, i) => i !== idx) })} className="text-red-400 hover:text-red-600">
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" /></svg>
                </button>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
