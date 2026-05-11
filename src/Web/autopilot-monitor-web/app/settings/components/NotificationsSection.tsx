"use client";

import SaveResetBar from "./SaveResetBar";

interface NotificationsSectionProps {
  webhookProviderType: number;
  setWebhookProviderType: (v: number) => void;
  webhookUrl: string;
  setWebhookUrl: (v: string) => void;
  webhookNotifyOnSuccess: boolean;
  setWebhookNotifyOnSuccess: (v: boolean) => void;
  webhookNotifyOnFailure: boolean;
  setWebhookNotifyOnFailure: (v: boolean) => void;
  webhookNotifyOnStart: boolean;
  setWebhookNotifyOnStart: (v: boolean) => void;
  onTestWebhook: () => Promise<void>;
  testingWebhook: boolean;
  testWebhookResult: { success: boolean; message: string } | null;
  onSave: () => Promise<void> | void;
  onReset: () => void;
  saving: boolean;
}

const PROVIDERS = [
  { value: 0, label: "None (disabled)", placeholder: "" },
  { value: 1, label: "Microsoft Teams (Legacy Connector)", placeholder: "https://your-org.webhook.office.com/webhookb2/...", badge: "Deprecated", badgeColor: "bg-amber-100 text-amber-800" },
  { value: 2, label: "Microsoft Teams (Workflow Webhook)", placeholder: "https://prod-xx.westeurope.logic.azure.com:443/workflows/...", badge: "Recommended", badgeColor: "bg-green-100 text-green-800" },
  { value: 10, label: "Slack", placeholder: "https://hooks.slack.com/services/T.../B.../..." },
];

export default function NotificationsSection({
  webhookProviderType,
  setWebhookProviderType,
  webhookUrl,
  setWebhookUrl,
  webhookNotifyOnSuccess,
  setWebhookNotifyOnSuccess,
  webhookNotifyOnFailure,
  setWebhookNotifyOnFailure,
  webhookNotifyOnStart,
  setWebhookNotifyOnStart,
  onTestWebhook,
  testingWebhook,
  testWebhookResult,
  onSave,
  onReset,
  saving,
}: NotificationsSectionProps) {
  const selectedProvider = PROVIDERS.find((p) => p.value === webhookProviderType) ?? PROVIDERS[0];
  const isActive = webhookProviderType !== 0 && webhookUrl.length > 0;

  return (
    <div className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-sky-50 to-blue-50">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-sky-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-gray-900">Notifications</h2>
            <p className="text-sm text-gray-500 mt-1">Send enrollment status notifications to a channel via webhook.</p>
          </div>
        </div>
      </div>
      <div className="p-6 space-y-4">

        {/* Provider Dropdown */}
        <div>
          <label className="block">
            <span className="text-gray-700 font-medium">Notification Provider</span>
            <p className="text-sm text-gray-500 mb-2">
              Select how you want to receive enrollment notifications.
            </p>
            <div className="flex items-center gap-2">
              <select
                value={webhookProviderType}
                onChange={(e) => setWebhookProviderType(Number(e.target.value))}
                className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 focus:outline-none focus:ring-2 focus:ring-sky-500 focus:border-sky-500 transition-colors"
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
          </label>
        </div>

        {/* Legacy deprecation warning */}
        {webhookProviderType === 1 && (
          <div className="p-3 rounded-lg bg-amber-50 border border-amber-200">
            <p className="text-sm text-amber-800">
              <strong>Note:</strong> Office 365 Connectors are deprecated by Microsoft. Consider switching to <strong>Microsoft Teams (Workflow Webhook)</strong> for continued support.
            </p>
          </div>
        )}

        {/* Webhook URL */}
        {webhookProviderType !== 0 && (
          <div>
            <label className="block">
              <span className="text-gray-700 font-medium">Webhook URL</span>
              <p className="text-sm text-gray-500 mb-2">
                {webhookProviderType === 2
                  ? "Create a Workflow in Teams (Channel → Manage channel → Workflows → \"Post to a channel when a webhook request is received\") and paste the URL here."
                  : webhookProviderType === 10
                  ? "Create an Incoming Webhook in Slack (Apps → Incoming Webhooks → Add to channel) and paste the URL here."
                  : "Create an Incoming Webhook in your Teams channel (Channel → Connectors → Incoming Webhook) and paste the URL here."}
              </p>
              <div className="flex items-center gap-2">
                <input
                  type="url"
                  value={webhookUrl}
                  onChange={(e) => setWebhookUrl(e.target.value)}
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
        )}

        {/* Notify on Start */}
        {webhookProviderType !== 0 && (
          <div className={`flex items-center justify-between p-4 rounded-lg border transition-colors ${isActive ? 'border-gray-200 hover:border-sky-200' : 'border-gray-100 opacity-50'}`}>
            <div>
              <p className="font-medium text-gray-900">Notify on Start</p>
              <p className="text-sm text-gray-500">Send a notification when an enrollment starts (one signal per fresh session and per Pre-Provisioning resume)</p>
            </div>
            <button
              onClick={() => isActive && setWebhookNotifyOnStart(!webhookNotifyOnStart)}
              disabled={!isActive}
              className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors disabled:cursor-not-allowed ${webhookNotifyOnStart && isActive ? 'bg-sky-500' : 'bg-gray-300'}`}
            >
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${webhookNotifyOnStart && isActive ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </div>
        )}

        {/* Notify on Success */}
        {webhookProviderType !== 0 && (
          <div className={`flex items-center justify-between p-4 rounded-lg border transition-colors ${isActive ? 'border-gray-200 hover:border-sky-200' : 'border-gray-100 opacity-50'}`}>
            <div>
              <p className="font-medium text-gray-900">Notify on Success</p>
              <p className="text-sm text-gray-500">Send a notification when an enrollment completes successfully</p>
            </div>
            <button
              onClick={() => isActive && setWebhookNotifyOnSuccess(!webhookNotifyOnSuccess)}
              disabled={!isActive}
              className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors disabled:cursor-not-allowed ${webhookNotifyOnSuccess && isActive ? 'bg-sky-500' : 'bg-gray-300'}`}
            >
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${webhookNotifyOnSuccess && isActive ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </div>
        )}

        {/* Notify on Failure */}
        {webhookProviderType !== 0 && (
          <div className={`flex items-center justify-between p-4 rounded-lg border transition-colors ${isActive ? 'border-gray-200 hover:border-sky-200' : 'border-gray-100 opacity-50'}`}>
            <div>
              <p className="font-medium text-gray-900">Notify on Failure</p>
              <p className="text-sm text-gray-500">Send a notification when an enrollment fails</p>
            </div>
            <button
              onClick={() => isActive && setWebhookNotifyOnFailure(!webhookNotifyOnFailure)}
              disabled={!isActive}
              className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors disabled:cursor-not-allowed ${webhookNotifyOnFailure && isActive ? 'bg-sky-500' : 'bg-gray-300'}`}
            >
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${webhookNotifyOnFailure && isActive ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </div>
        )}

        {/* Test Notification Button */}
        {webhookProviderType !== 0 && (
          <div className="pt-2">
            <button
              onClick={onTestWebhook}
              disabled={!isActive || testingWebhook}
              className="inline-flex items-center px-4 py-2 border border-gray-300 rounded-lg text-sm font-medium text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-sky-500 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
            >
              {testingWebhook ? (
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
                  Send Test Notification
                </>
              )}
            </button>
            {testWebhookResult && (
              <span className={`ml-3 inline-flex items-center px-2.5 py-1 rounded-full text-xs font-medium ${testWebhookResult.success ? 'bg-green-100 text-green-800' : 'bg-red-100 text-red-800'}`}>
                {testWebhookResult.message}
              </span>
            )}
          </div>
        )}

        <SaveResetBar onSave={onSave} onReset={onReset} saving={saving} />
      </div>
    </div>
  );
}
