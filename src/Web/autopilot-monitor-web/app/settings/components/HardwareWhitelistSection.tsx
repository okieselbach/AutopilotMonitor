"use client";

import { useState } from "react";
import SaveResetBar from "./SaveResetBar";
import ReadOnlyFieldset from "./ReadOnlyFieldset";

interface HardwareWhitelistSectionProps {
  manufacturerWhitelist: string;
  setManufacturerWhitelist: (value: string) => void;
  modelWhitelist: string;
  setModelWhitelist: (value: string) => void;
  onSave: () => Promise<void> | void;
  onReset: () => void;
  saving: boolean;
  /** Read-only viewer (Operator): inputs disabled, no Save/Reset bar. */
  readOnly?: boolean;
}

function parseList(csv: string): string[] {
  return csv.split(",").map((s) => s.trim()).filter(Boolean);
}

function joinList(items: string[]): string {
  return items.join(",");
}

function WhitelistEditor({
  label,
  description,
  placeholder,
  items,
  setItems,
}: {
  label: string;
  description: React.ReactNode;
  placeholder: string;
  items: string[];
  setItems: (items: string[]) => void;
}) {
  const [newItem, setNewItem] = useState("");
  const trimmed = newItem.trim();
  const isDuplicate =
    trimmed !== "" &&
    items.some((a) => a.toLowerCase() === trimmed.toLowerCase());

  const addItem = () => {
    if (!trimmed || isDuplicate) return;
    setItems([...items, trimmed]);
    setNewItem("");
  };

  const removeItem = (idx: number) => {
    setItems(items.filter((_, i) => i !== idx));
  };

  return (
    <div className="p-4 rounded-lg border border-gray-200">
      <p className="font-medium text-gray-900 mb-1">{label}</p>
      <p className="text-sm text-gray-500 mb-3">{description}</p>

      {/* Current items */}
      {items.length > 0 && (
        <div className="mb-3">
          <div className="space-y-1.5">
            {items.map((item, idx) => (
              <div
                key={idx}
                className="flex items-center justify-between bg-blue-50 border border-blue-200 rounded-lg px-3 py-2"
              >
                <p className="text-sm text-blue-900 font-mono">{item}</p>
                <button
                  onClick={() => removeItem(idx)}
                  className="ml-2 flex-shrink-0 text-blue-400 hover:text-red-600 transition-colors"
                  title="Remove"
                >
                  <svg
                    className="w-3.5 h-3.5"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth={2}
                      d="M6 18L18 6M6 6l12 12"
                    />
                  </svg>
                </button>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Add new item */}
      <div className="flex gap-2 mt-2">
        <input
          type="text"
          placeholder={placeholder}
          value={newItem}
          onChange={(e) => setNewItem(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              addItem();
            }
          }}
          className="flex-1 px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
        />
        <button
          onClick={addItem}
          disabled={!trimmed || isDuplicate}
          className="px-4 py-1.5 bg-blue-600 text-white rounded-lg text-sm font-medium hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors whitespace-nowrap"
        >
          Add
        </button>
      </div>
      {isDuplicate && (
        <p className="text-xs text-red-500 mt-1">
          This entry is already in the list.
        </p>
      )}
    </div>
  );
}

export default function HardwareWhitelistSection({
  manufacturerWhitelist,
  setManufacturerWhitelist,
  modelWhitelist,
  setModelWhitelist,
  onSave,
  onReset,
  saving,
  readOnly = false,
}: HardwareWhitelistSectionProps) {
  const manufacturers = parseList(manufacturerWhitelist);
  const models = parseList(modelWhitelist);

  return (
    <div className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-indigo-50 to-blue-50">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 3v2m6-2v2M9 19v2m6-2v2M5 9H3m2 6H3m18-6h-2m2 6h-2M7 19h10a2 2 0 002-2V7a2 2 0 00-2-2H7a2 2 0 00-2 2v10a2 2 0 002 2zM9 9h6v6H9V9z" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-gray-900">
              Hardware Whitelist
            </h2>
            <p className="text-sm text-gray-500 mt-1">
              Control which device manufacturers and models the backend accepts
              monitoring data from
            </p>
          </div>
        </div>
      </div>
      <div className="p-6 space-y-5">
        <ReadOnlyFieldset readOnly={readOnly}>
        <div className="space-y-5">
        {/* Info */}
        <div className="bg-blue-50 border border-blue-200 rounded-lg p-3 space-y-2">
          <p className="text-sm text-blue-900">
            Supports wildcards:{" "}
            <code className="bg-blue-100 px-1 rounded">*</code> = allow all,{" "}
            <code className="bg-blue-100 px-1 rounded">Dell*</code> = starts
            with &quot;Dell&quot;. Each entry is matched individually against
            the device hardware info.
          </p>
          <p className="text-sm text-blue-900">
            Devices that do not match this list are rejected at the API — the
            backend stores no session or telemetry data for them. Autopilot
            enrollment itself is <span className="font-medium">not</span>{" "}
            affected. The agent on a rejected device stops monitoring after the
            first rejected request and automatically removes itself from the
            device after 48 hours at the latest.
          </p>
        </div>

        {/* Manufacturer Whitelist */}
        <WhitelistEditor
          label="Allowed Manufacturers"
          description="Monitoring data is only accepted from manufacturers listed here. Use wildcards for partial matches."
          placeholder="e.g. Dell*, HP*, Microsoft Corporation"
          items={manufacturers}
          setItems={(items) => setManufacturerWhitelist(joinList(items))}
        />

        {/* Model Whitelist */}
        <WhitelistEditor
          label="Allowed Models"
          description="Monitoring data is only accepted from device models listed here. Use wildcards for partial matches."
          placeholder="e.g. Latitude*, EliteBook*, Surface*"
          items={models}
          setItems={(items) => setModelWhitelist(joinList(items))}
        />

        </div>
        </ReadOnlyFieldset>

        {!readOnly && <SaveResetBar onSave={onSave} onReset={onReset} saving={saving} />}
      </div>
    </div>
  );
}
