namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// Outcome of one send to one notification channel, whatever the provider. Real deliveries and
    /// "send test" requests produce the same shape; <see cref="NotificationChannelDispatcher"/>
    /// records it in the channel's health row and the test endpoints return it to the portal.
    /// </summary>
    public class NotificationSendResult
    {
        public bool Success { get; set; }

        /// <summary>HTTP status of the destination's answer; null when there was no answer or the provider has none.</summary>
        public int? StatusCode { get; set; }

        /// <summary>Human-readable outcome for the admin who pressed "Send Test"; may quote up to 200 characters of the response body.</summary>
        public string Message { get; set; } = "";
    }
}
