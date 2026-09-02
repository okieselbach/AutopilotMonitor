"use client";

import SaveResetBar from "./SaveResetBar";
import ReadOnlyFieldset from "./ReadOnlyFieldset";
import { MAX_NOTIFICATION_CHANNELS, NotificationChannel } from "../types";
import { ChannelEditor } from "@/components/notifications/ChannelEditor";
import { SectionCardHeader } from "@/components/SectionCardHeader";
import { DOCS_PATHS } from "@/lib/docsPaths";

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
  /** Global Admin: the platform-bot Telegram provider is offered in the provider dropdown. */
  showTelegramProvider?: boolean;
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
  showTelegramProvider = false,
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
      <SectionCardHeader
        tone="sky"
        iconPath="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9"
        title="Notifications"
        subtitle="Send notifications to one or more channels — Teams, Slack, Discord, or any JSON webhook (ticketing, automation, bots). Each channel picks which events it receives."
        docsPath={DOCS_PATHS.notifications}
      />
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
            showTelegramProvider={showTelegramProvider}
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
