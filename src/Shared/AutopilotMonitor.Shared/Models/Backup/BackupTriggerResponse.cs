namespace AutopilotMonitor.Shared.Models.Backup
{
    /// <summary>
    /// Response of <c>POST global/backups/trigger</c> (202 Accepted): the job is queued, not
    /// done. The portal polls <see cref="StatusUrl"/> for the <see cref="BackupJobStatus"/>.
    /// </summary>
    // Declaration order == wire order.
    public sealed class BackupTriggerResponse : IApiResponse
    {
        public string JobId { get; set; } = string.Empty;
        public string StatusUrl { get; set; } = string.Empty;
    }
}
