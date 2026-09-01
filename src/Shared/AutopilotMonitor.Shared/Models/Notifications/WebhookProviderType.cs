namespace AutopilotMonitor.Shared.Models.Notifications
{
    /// <summary>
    /// Determines which renderer formats the notification payload for a webhook.
    /// </summary>
    public enum WebhookProviderType
    {
        /// <summary>No webhook configured.</summary>
        None = 0,

        /// <summary>Microsoft Teams legacy Office 365 Connector (MessageCard format). Deprecated by Microsoft.</summary>
        TeamsLegacyConnector = 1,

        /// <summary>Microsoft Teams Workflow webhook (Adaptive Card format). Recommended replacement.</summary>
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
    }
}
