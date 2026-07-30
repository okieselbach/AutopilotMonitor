"use client";

import { useState, useEffect, useRef } from "react";
import { AnalyzeRule } from "../types";

interface TemplateConfigModalProps {
  rule: AnalyzeRule;
  saving: boolean;
  onSave: (variables: Record<string, string>) => void;
  onCancel: () => void;
}

export default function TemplateConfigModal({
  rule,
  saving,
  onSave,
  onCancel,
}: TemplateConfigModalProps) {
  const templateVars = rule.templateVariables || [];
  const [values, setValues] = useState<Record<string, string>>(() => {
    const initial: Record<string, string> = {};
    for (const tv of templateVars) {
      initial[tv.name] = "";
    }
    return initial;
  });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const firstInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    // Focus the first input on mount
    const timer = setTimeout(() => firstInputRef.current?.focus(), 100);
    return () => clearTimeout(timer);
  }, []);

  const validate = (): boolean => {
    const newErrors: Record<string, string> = {};
    for (const tv of templateVars) {
      const val = values[tv.name]?.trim();
      if (!val) {
        newErrors[tv.name] = "This field is required.";
      } else if (tv.validation) {
        try {
          const regex = new RegExp(tv.validation);
          if (!regex.test(val)) {
            newErrors[tv.name] = "Value does not match the expected format.";
          }
        } catch {
          // Invalid regex in rule definition, skip validation
        }
      }
    }
    setErrors(newErrors);
    return Object.keys(newErrors).length === 0;
  };

  const handleSubmit = () => {
    if (!validate()) return;
    const trimmed: Record<string, string> = {};
    for (const [k, v] of Object.entries(values)) {
      trimmed[k] = v.trim();
    }
    onSave(trimmed);
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      {/* Backdrop */}
      <div className="absolute inset-0 bg-black/50" onClick={onCancel} />

      {/* Modal */}
      <div className="relative bg-white rounded-xl shadow-2xl max-w-lg w-full mx-4 overflow-hidden">
        {/* Header */}
        <div className="px-6 py-4 border-b border-gray-200 bg-gradient-to-r from-amber-50 to-orange-50">
          <div className="flex items-center space-x-3">
            <div className="flex-shrink-0 w-10 h-10 bg-amber-100 rounded-lg flex items-center justify-center">
              <svg className="w-5 h-5 text-amber-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6V4m0 2a2 2 0 100 4m0-4a2 2 0 110 4m-6 8a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4m6 6v10m6-2a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4" />
              </svg>
            </div>
            <div className="flex-1 min-w-0">
              <h3 className="text-lg font-semibold text-gray-900 truncate">Configure: {rule.title}</h3>
              <div className="flex items-center space-x-2 mt-0.5">
                <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-amber-100 text-amber-800">
                  Template
                </span>
                <span className="text-xs text-gray-500">{rule.ruleId}</span>
              </div>
            </div>
          </div>
        </div>

        {/* Body */}
        <div className="px-6 py-5 space-y-5">
          {/* Info box */}
          <div className="bg-amber-50 border border-amber-200 rounded-lg p-3 flex items-start space-x-2.5">
            <svg className="w-5 h-5 text-amber-500 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <p className="text-sm text-amber-800">
              This rule needs your environment-specific values before it can be activated.
              A custom copy will be created for your tenant.
            </p>
          </div>

          {/* Variable fields */}
          {templateVars.map((tv, idx) => (
            <div key={tv.name}>
              <label className="block text-sm font-semibold text-gray-700 mb-1">
                {tv.label}
              </label>
              {tv.description && (
                <p className="text-xs text-gray-500 mb-2">{tv.description}</p>
              )}
              <div className="relative">
                <input
                  ref={idx === 0 ? firstInputRef : undefined}
                  type="text"
                  value={values[tv.name] || ""}
                  onChange={(e) => {
                    setValues({ ...values, [tv.name]: e.target.value });
                    if (errors[tv.name]) {
                      setErrors({ ...errors, [tv.name]: "" });
                    }
                  }}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") handleSubmit();
                  }}
                  placeholder={tv.placeholder}
                  className={`w-full px-3 py-2.5 border-2 rounded-lg text-sm transition-colors focus:outline-none focus:ring-2 focus:ring-offset-1 ${
                    errors[tv.name]
                      ? "border-red-300 focus:border-red-400 focus:ring-red-200"
                      : "border-amber-300 focus:border-amber-400 focus:ring-amber-200 animate-pulse-border"
                  }`}
                />
              </div>
              {errors[tv.name] && (
                <p className="mt-1 text-xs text-red-600">{errors[tv.name]}</p>
              )}
              <p className="mt-1 text-xs text-gray-400">
                Replaces: <code className="bg-gray-100 px-1 rounded">{tv.placeholder}</code>
              </p>
            </div>
          ))}
        </div>

        {/* Footer */}
        <div className="px-6 py-4 border-t border-gray-200 bg-gray-50 flex items-center justify-end space-x-3">
          <button
            onClick={onCancel}
            disabled={saving}
            className="px-5 py-2 border border-gray-300 rounded-lg text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm font-medium"
          >
            Cancel
          </button>
          <button
            onClick={handleSubmit}
            disabled={saving}
            className="px-5 py-2 bg-amber-600 text-white rounded-lg hover:bg-amber-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2 text-sm font-medium"
          >
            {saving ? (
              <>
                <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                <span>Creating...</span>
              </>
            ) : (
              <>
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                </svg>
                <span>Save &amp; Enable</span>
              </>
            )}
          </button>
        </div>
      </div>

      {/* Pulse animation for the amber border */}
      <style jsx>{`
        @keyframes pulse-border {
          0%, 100% { border-color: rgb(252 211 77); }
          50% { border-color: rgb(245 158 11); }
        }
        .animate-pulse-border {
          animation: pulse-border 2s ease-in-out 3;
        }
      `}</style>
    </div>
  );
}
