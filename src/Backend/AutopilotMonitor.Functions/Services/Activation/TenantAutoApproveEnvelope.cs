using System;

namespace AutopilotMonitor.Functions.Services.Activation
{
    /// <summary>
    /// Queue message contract for <c>tenant-auto-approve</c>. One envelope per new tenant
    /// signup, enqueued with <see cref="ActivationDelay"/> visibility delay so the operator's
    /// Telegram / portal notification lands before the activation happens.
    /// </summary>
    public sealed class TenantAutoApproveEnvelope
    {
        /// <summary>Delay between signup and the auto-activation attempt.</summary>
        public static readonly TimeSpan ActivationDelay = TimeSpan.FromMinutes(1);

        /// <summary>The tenant to activate.</summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>UPN of the first user whose sign-in created the tenant (diagnostic only).</summary>
        public string SignupUpn { get; set; } = string.Empty;

        /// <summary>When the signup happened (diagnostic only).</summary>
        public DateTime EnqueuedAtUtc { get; set; }
    }
}
