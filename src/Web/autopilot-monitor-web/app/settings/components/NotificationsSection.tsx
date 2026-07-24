"use client";

import { useEffect, useRef, useState } from "react";
import SaveResetBar from "./SaveResetBar";
import ReadOnlyFieldset from "./ReadOnlyFieldset";
import { MAX_NOTIFICATION_CHANNELS, NotificationChannel } from "../types";

interface NotificationsSectionProps {
  channels: NotificationChannel[];
  setChannels: (v: NotificationChannel[]) => void;
  onTestChannel: (channelId: string) => Promise<void>;
  testingChannelId: string | null;
  testChannelResult: { channelId: string; success: boolean; message: string } | null;
  onSave: () => Promise<void> | void;
  onReset: () => void;
  saving: boolean;
  /** Read-only viewer (Operator): channels visible but inert, no Save/Reset bar. */
  readOnly?: boolean;
}

const GENERIC_PROVIDER = 20;

const PROVIDERS = [
  { value: 1, label: "Microsoft Teams (Legacy Connector)", placeholder: "https://your-org.webhook.office.com/webhookb2/...", badge: "Deprecated", badgeColor: "bg-amber-100 text-amber-800" },
  { value: 2, label: "Microsoft Teams (Workflow Webhook)", placeholder: "https://prod-xx.westeurope.logic.azure.com:443/workflows/...", badge: "Recommended", badgeColor: "bg-green-100 text-green-800" },
  { value: 10, label: "Slack", placeholder: "https://hooks.slack.com/services/T.../B.../..." },
  { value: GENERIC_PROVIDER, label: "Generic JSON (ticketing / automation)", placeholder: "https://your-system.example.com/api/webhooks/autopilot" },
];

const EVENT_TOGGLES: { key: keyof NotificationChannel; label: string; hint: string }[] = [
  { key: "notifyOnStart", label: "Start", hint: "Enrollment started (one signal per fresh session and per Pre-Provisioning resume)" },
  { key: "notifyOnSuccess", label: "Success", hint: "Enrollment completed successfully" },
  { key: "notifyOnFailure", label: "Failure", hint: "Enrollment failed" },
  { key: "notifyOnHardwareRejection", label: "Hardware rejection", hint: "Device rejected by the hardware whitelist" },
  { key: "notifyOnSlaEvents", label: "SLA", hint: "SLA breach / resolved / consecutive failures (which breaches are evaluated is configured under SLA Targets)" },
];

type HeaderRow = { key: string; value: string };

function parseHeaderRows(json: string): HeaderRow[] {
  if (!json || !json.trim()) return [];
  try {
    const obj = JSON.parse(json);
    if (obj && typeof obj === "object" && !Array.isArray(obj)) {
      return Object.entries(obj).map(([key, value]) => ({ key, value: String(value) }));
    }
  } catch {
    // Malformed JSON (e.g. legacy/hand-edited value) — start from an empty editor.
  }
  return [];
}

function serializeHeaderRows(rows: HeaderRow[]): string {
  const obj: Record<string, string> = {};
  for (const { key, value } of rows) {
    const k = key.trim();
    if (k) obj[k] = value;
  }
  return Object.keys(obj).length > 0 ? JSON.stringify(obj) : "";
}

/**
 * Key→value editor for generic-webhook custom headers. Owns row state locally and serializes
 * to the JSON string the backend persists; re-syncs only on external changes (config load/reset).
 */
function WebhookHeaderEditor({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const [rows, setRows] = useState<HeaderRow[]>(() => parseHeaderRows(value));
  const lastSerialized = useRef(value);

  useEffect(() => {
    if (value !== lastSerialized.current) {
      setRows(parseHeaderRows(value));
      lastSerialized.current = value;
    }
  }, [value]);

  const commit = (next: HeaderRow[]) => {
    setRows(next);
    const json = serializeHeaderRows(next);
    lastSerialized.current = json;
    onChange(json);
  };

  return (
    <div>
      <span className="text-gray-700 font-medium text-sm">Custom Headers</span>
      <p className="text-xs text-gray-500 mb-2">
        Optional HTTP headers sent with every request — e.g. <code className="font-mono">Authorization</code> with an API key
        for your ticketing system or SMTP gateway. Framing headers (Host, Content-Type, …) are ignored.
      </p>
      <div className="space-y-2">
        {rows.map((row, i) => (
          <div key={i} className="flex items-center gap-2">
            <input
              type="text"
              value={row.key}
              onChange={(e) => commit(rows.map((r, j) => (j === i ? { ...r, key: e.target.value } : r)))}
              placeholder="Header name"
              className="block w-1/3 px-3 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-sky-500 focus:border-sky-500 transition-colors font-mono text-sm"
            />
            <input
              type="text"
              value={row.value}
              onChange={(e) => commit(rows.map((r, j) => (j === i ? { ...r, value: e.target.value } : r)))}
              placeholder="Value"
              className="block flex-1 px-3 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-sky-500 focus:border-sky-500 transition-colors font-mono text-sm"
            />
            <button
              type="button"
              onClick={() => commit(rows.filter((_, j) => j !== i))}
              className="flex-shrink-0 p-2 text-gray-400 hover:text-red-600 transition-colors"
              aria-label="Remove header"
            >
              <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </div>
        ))}
      </div>
      <button
        type="button"
        onClick={() => commit([...rows, { key: "", value: "" }])}
        className="mt-2 inline-flex items-center text-sm font-medium text-sky-600 hover:text-sky-700 transition-colors"
      >
        <svg className="w-4 h-4 mr-1" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
        </svg>
        Add header
      </button>
    </div>
  );
}

/** Single channel card: name, provider, URL, headers (generic only), event subscriptions, test. */
function ChannelEditor({
  channel,
  onChange,
  onRemove,
  onTest,
  testing,
  testResult,
}: {
  channel: NotificationChannel;
  onChange: (next: NotificationChannel) => void;
  onRemove: () => void;
  onTest: () => void;
  testing: boolean;
  testResult: { success: boolean; message: string } | null;
}) {
  const selectedProvider = PROVIDERS.find((p) => p.value === channel.providerType) ?? PROVIDERS[1];
  const isActive = channel.enabled && (channel.url ?? "").length > 0;

  return (
    <div className={`rounded-lg border ${channel.enabled ? "border-gray-200" : "border-gray-100 bg-gray-50"} p-4 space-y-4`}>
      {/* Header row: name + enable + delete */}
      <div className="flex items-center gap-3">
        <input
          type="text"
          value={channel.name}
          onChange={(e) => onChange({ ...channel, name: e.target.value })}
          placeholder="Channel name (e.g. Service Desk)"
          className="block flex-1 px-3 py-2 border border-gray-300 rounded-lg text-gray-900 font-medium placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-sky-500 focus:border-sky-500 transition-colors"
        />
        <label className="flex items-center gap-2 text-sm text-gray-600 whitespace-nowrap">
          Enabled
          <button
            type="button"
            onClick={() => onChange({ ...channel, enabled: !channel.enabled })}
            className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${channel.enabled ? "bg-sky-500" : "bg-gray-300"}`}
          >
            <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${channel.enabled ? "translate-x-6" : "translate-x-1"}`} />
          </button>
        </label>
        <button
          type="button"
          onClick={onRemove}
          className="flex-shrink-0 p-2 text-gray-400 hover:text-red-600 transition-colors"
          aria-label="Remove channel"
        >
          <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
          </svg>
        </button>
      </div>

      {/* Provider */}
      <div className="flex items-center gap-2">
        <select
          value={channel.providerType}
          onChange={(e) => {
            const next = Number(e.target.value);
            // Custom headers are generic-only; clear them when leaving the generic provider so
            // a later switch back can't revive stale auth headers and persist them to a new endpoint.
            onChange({
              ...channel,
              providerType: next,
              customHeadersJson: next === GENERIC_PROVIDER ? channel.customHeadersJson : undefined,
            });
          }}
          className="block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 focus:outline-none focus:ring-2 focus:ring-sky-500 focus:border-sky-500 transition-colors"
        >
          {PROVIDERS.map((p) => (
            <option key={p.value} value={p.value}>
              {p.label}
            </option>
          ))}
        </select>
        {selectedProvider.badge && (
          <span className={`inline-flex items-center px-2.5 py-1 rounded-full text-xs font-medium whitespace-nowrap ${selectedProvider.badgeColor}`}>
            {selectedProvider.badge}
          </span>
        )}
      </div>

      {channel.providerType === 1 && (
        <div className="p-3 rounded-lg bg-amber-50 border border-amber-200">
          <p className="text-sm text-amber-800">
            <strong>Note:</strong> Office 365 Connectors are deprecated by Microsoft. Consider switching to <strong>Microsoft Teams (Workflow Webhook)</strong> for continued support.
          </p>
        </div>
      )}

      {/* URL */}
      <div>
        <label className="block">
          <span className="text-gray-700 font-medium text-sm">Webhook URL</span>
          <p className="text-xs text-gray-500 mb-1">
            {channel.providerType === 2
              ? "Create a Workflow in Teams (Channel → Manage channel → Workflows → \"Post to a channel when a webhook request is received\") and paste the URL here."
              : channel.providerType === 10
              ? "Create an Incoming Webhook in Slack (Apps → Incoming Webhooks → Add to channel) and paste the URL here."
              : channel.providerType === GENERIC_PROVIDER
              ? "Any HTTPS endpoint that accepts a JSON POST. Receives a stable payload (schemaVersion + eventType, e.g. \"enrollment_succeeded\") for ticketing, automation, or an SMTP gateway like Postal."
              : "Create an Incoming Webhook in your Teams channel (Channel → Connectors → Incoming Webhook) and paste the URL here."}
          </p>
          <div className="flex items-center gap-2">
            <input
              type="url"
              value={channel.url ?? ""}
              onChange={(e) => onChange({ ...channel, url: e.target.value })}
              placeholder={selectedProvider.placeholder}
              className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-sky-500 focus:border-sky-500 transition-colors font-mono text-sm"
            />
            {isActive && (
              <span className="mt-1 inline-flex items-center px-2.5 py-1 rounded-full text-xs font-medium bg-green-100 text-green-800 whitespace-nowrap">
                Active
              </span>
            )}
          </div>
        </label>
      </div>

      {/* Custom Headers (generic provider only) */}
      {channel.providerType === GENERIC_PROVIDER && (
        <WebhookHeaderEditor
          value={channel.customHeadersJson ?? ""}
          onChange={(v) => onChange({ ...channel, customHeadersJson: v || undefined })}
        />
      )}

      {/* Event subscriptions */}
      <div>
        <span className="text-gray-700 font-medium text-sm">Send to this channel</span>
        <p className="text-xs text-gray-500 mb-2">
          A channel with everything off can still be targeted by Analyze Rules (rule-level notifications).
        </p>
        <div className="flex flex-wrap gap-2">
          {EVENT_TOGGLES.map(({ key, label, hint }) => {
            const on = Boolean(channel[key]);
            return (
              <button
                key={key}
                type="button"
                title={hint}
                onClick={() => onChange({ ...channel, [key]: !on })}
                className={`inline-flex items-center px-3 py-1.5 rounded-full text-xs font-medium border transition-colors ${
                  on
                    ? "bg-sky-100 text-sky-800 border-sky-300"
                    : "bg-white text-gray-500 border-gray-300 hover:border-sky-300"
                }`}
              >
                <span className={`mr-1.5 inline-block h-2 w-2 rounded-full ${on ? "bg-sky-500" : "bg-gray-300"}`} />
                {label}
              </button>
            );
          })}
        </div>
      </div>

      {/* Test */}
      <div className="flex items-center gap-3">
        <button
          type="button"
          onClick={onTest}
          disabled={!isActive || testing}
          className="inline-flex items-center px-3 py-1.5 border border-gray-300 rounded-lg text-sm font-medium text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-sky-500 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
        >
          {testing ? (
            <>
              <svg className="animate-spin -ml-1 mr-2 h-4 w-4 text-gray-500" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
              </svg>
              Sending...
            </>
          ) : (
            <>
              <svg className="w-4 h-4 mr-2" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 19l9 2-9-18-9 18 9-2zm0 0v-8" />
              </svg>
              Send Test
            </>
          )}
        </button>
        <span className="text-xs text-gray-400">Tests the last saved configuration of this channel.</span>
        {testResult && (
          <span className={`inline-flex items-center px-2.5 py-1 rounded-full text-xs font-medium ${testResult.success ? "bg-green-100 text-green-800" : "bg-red-100 text-red-800"}`}>
            {testResult.message}
          </span>
        )}
      </div>
    </div>
  );
}

export default function NotificationsSection({
  channels,
  setChannels,
  onTestChannel,
  testingChannelId,
  testChannelResult,
  onSave,
  onReset,
  saving,
  readOnly = false,
}: NotificationsSectionProps) {
  const addChannel = () => {
    if (channels.length >= MAX_NOTIFICATION_CHANNELS) return;
    setChannels([
      ...channels,
      {
        id: crypto.randomUUID(),
        name: "",
        providerType: 2, // Teams Workflow (recommended default)
        url: "",
        enabled: true,
        notifyOnSuccess: true,
        notifyOnFailure: true,
      },
    ]);
  };

  return (
    <div className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-sky-50 to-blue-50">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-sky-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-gray-900">Notifications</h2>
            <p className="text-sm text-gray-500 mt-1">
              Send notifications to one or more channels — Teams, Slack, or any JSON webhook (ticketing, automation, bots). Each channel picks which events it receives.
            </p>
          </div>
        </div>
      </div>
      <div className="p-6 space-y-4">
        <ReadOnlyFieldset readOnly={readOnly}>
        <div className="space-y-4">
        {channels.length === 0 && (
          <p className="text-sm text-gray-500">
            No channels configured yet. Add a channel to receive enrollment, SLA, or rule notifications.
          </p>
        )}

        {channels.map((channel) => (
          <ChannelEditor
            key={channel.id}
            channel={channel}
            onChange={(next) => setChannels(channels.map((c) => (c.id === next.id ? next : c)))}
            onRemove={() => setChannels(channels.filter((c) => c.id !== channel.id))}
            onTest={() => onTestChannel(channel.id)}
            testing={testingChannelId === channel.id}
            testResult={testChannelResult?.channelId === channel.id ? testChannelResult : null}
          />
        ))}

        <button
          type="button"
          onClick={addChannel}
          disabled={channels.length >= MAX_NOTIFICATION_CHANNELS}
          className="inline-flex items-center text-sm font-medium text-sky-600 hover:text-sky-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
        >
          <svg className="w-4 h-4 mr-1" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
          </svg>
          Add channel{channels.length >= MAX_NOTIFICATION_CHANNELS ? ` (max ${MAX_NOTIFICATION_CHANNELS})` : ""}
        </button>
        </div>
        </ReadOnlyFieldset>

        {!readOnly && <SaveResetBar onSave={onSave} onReset={onReset} saving={saving} />}
      </div>
    </div>
  );
}
