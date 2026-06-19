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
    }
}
