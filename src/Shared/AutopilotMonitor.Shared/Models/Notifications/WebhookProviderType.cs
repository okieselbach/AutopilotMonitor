namespace AutopilotMonitor.Shared.Models.Notifications
{
    /// <summary>
    /// Determines which renderer formats the notification payload for a webhook.
    /// </summary>
    public enum WebhookProviderType
    {
        /// <summary>No webhook configured.</summary>
        None = 0,

        // 1 was the Teams Office 365 Connector (MessageCard), switched off by Microsoft in May 2026.
        // Never reuse it: configuration backups still carry the value, and a retired value must
        // keep failing validation instead of silently meaning a different provider.

        /// <summary>Microsoft Teams Workflow webhook (Adaptive Card format).</summary>
        TeamsWorkflowWebhook = 2,

        /// <summary>Slack Incoming Webhook (Block Kit format).</summary>
        Slack = 10,

        /// <summary>
        /// Generic JSON webhook. Posts a stable, channel-agnostic JSON payload (schemaVersion + eventType)
        /// to any HTTP endpoint — for ticketing systems, automation, or SMTP gateways (e.g. Postal).
        /// Supports per-tenant custom request headers for API-key authentication.
        /// </summary>
        GenericJson = 20,

        /// <summary>
        /// Discord channel webhook (embed format). Webhooks cannot post buttons, so
        /// openUrl actions are rendered as markdown links inside the embed description.
        /// </summary>
        Discord = 30,

        /// <summary>
        /// Telegram chat (plain-text message via the platform bot). The odd one out: this is not
        /// a webhook. The channel's <c>Url</c> carries the destination CHAT ID, and the bot token
        /// belongs to the platform (PreviewConfig <c>WebhookUrl</c>), not to the tenant — a caller
        /// configuring one sends through OUR bot. That is why Telegram channels are Global-Admin
        /// only (enforced in TenantConfigValidation, not just hidden in the UI), and why they are
        /// dispatched by TelegramNotificationService instead of a renderer.
        /// </summary>
        Telegram = 40,

        /// <summary>
        /// Web Push to devices the channel's recipients paired in the portal (RFC 8030/8291/8292,
        /// end-to-end encrypted per device). Not a webhook: the channel has NO destination of its
        /// own — <c>Url</c> stays empty — because the recipients are resolved at send time from
        /// the paired devices of the scope's Admin/Operator members (tenant channel) or of the
        /// Global Admins (platform channel). Global-Admin only while the customer release is
        /// pending (same gate as Telegram, enforced in TenantConfigValidation). Value 50 stays
        /// reserved for this provider even if it were ever retired.
        /// </summary>
        Push = 50,
    }
}
