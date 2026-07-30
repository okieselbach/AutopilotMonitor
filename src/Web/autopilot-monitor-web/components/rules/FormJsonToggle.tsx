"use client";

import React, { useState } from "react";

interface FormJsonToggleProps {
  jsonMode: boolean;
  onToggleMode: (jsonMode: boolean) => void;
  jsonText: string;
  onJsonTextChange: (text: string) => void;
  jsonError: string | null;
  onApplyJson: () => void;
  children: React.ReactNode;
  textareaRows?: number;
  description?: React.ReactNode;
}

export function FormJsonToggle({
  jsonMode,
  onToggleMode,
  jsonText,
  onJsonTextChange,
  jsonError,
  onApplyJson,
  children,
  textareaRows = 20,
  description,
}: FormJsonToggleProps) {
  return (
    <>
      {jsonMode ? (
        <div className="space-y-3">
          <div className="flex items-center justify-between">
            {description && (
              <p className="text-sm text-gray-600">{description}</p>
            )}
            <button
              type="button"
              onClick={onApplyJson}
              className="text-xs text-green-700 hover:text-green-800 font-medium whitespace-nowrap ml-4"
            >
              Apply JSON &rarr;
            </button>
          </div>
          {jsonError && (
            <p className="text-xs text-red-600 bg-red-50 border border-red-200 rounded px-3 py-2">{jsonError}</p>
          )}
          <textarea
            value={jsonText}
            onChange={(e) => onJsonTextChange(e.target.value)}
            rows={textareaRows}
            spellCheck={false}
            className="w-full px-4 py-3 border border-gray-300 rounded-lg text-sm font-mono text-gray-900 bg-gray-50 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500 resize-y"
          />
        </div>
      ) : (
        children
      )}
    </>
  );
}

/** Read-only JSON view for built-in / community rules (view & copy, no editing). */
export function ReadOnlyJsonView({
  jsonText,
  textareaRows = 20,
}: {
  jsonText: string;
  textareaRows?: number;
}) {
  const [copied, setCopied] = useState(false);

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(jsonText);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // fallback: select the textarea content
    }
  };

  return (
    <div className="space-y-3 mt-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-gray-600">Rule definition as JSON &mdash; copy to use as a template for a new custom rule.</p>
        <button
          type="button"
          onClick={handleCopy}
          className="flex items-center space-x-1.5 px-3 py-1.5 text-xs font-medium text-green-700 hover:text-green-800 bg-indigo-50 hover:bg-indigo-100 rounded-md transition-colors whitespace-nowrap ml-4"
        >
          {copied ? (
            <>
              <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" /></svg>
              <span>Copied!</span>
            </>
          ) : (
            <>
              <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" /></svg>
              <span>Copy JSON</span>
            </>
          )}
        </button>
      </div>
      <textarea
        value={jsonText}
        readOnly
        rows={textareaRows}
        spellCheck={false}
        className="w-full px-4 py-3 border border-gray-300 rounded-lg text-sm font-mono text-gray-900 bg-gray-50 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500 resize-y cursor-default"
      />
    </div>
  );
}

/** The toggle buttons for switching between Form and JSON mode. */
export function JsonModeToggleButtons({
  jsonMode,
  onToggleMode,
}: {
  jsonMode: boolean;
  onToggleMode: (jsonMode: boolean) => void;
}) {
  return (
    <div className="flex items-center bg-gray-100 rounded-lg p-1">
      <button
        onClick={() => onToggleMode(false)}
        className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors ${
          !jsonMode ? "bg-white text-gray-900 shadow-sm" : "text-gray-500 hover:text-gray-700"
        }`}
      >
        Form
      </button>
      <button
        onClick={() => onToggleMode(true)}
        className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors ${
          jsonMode ? "bg-white text-gray-900 shadow-sm" : "text-gray-500 hover:text-gray-700"
        }`}
      >
        JSON
      </button>
    </div>
  );
}
