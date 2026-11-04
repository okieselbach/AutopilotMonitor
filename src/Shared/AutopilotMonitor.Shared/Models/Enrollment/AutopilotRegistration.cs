namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// The device's Windows Autopilot registration in Intune (Graph windowsAutopilotDeviceIdentity),
    /// read by the Autopilot device validation from the object it already fetches. The assigned user
    /// is kept as a yes/no only; the user's name never leaves the validator.
    /// </summary>
    public sealed class AutopilotRegistration
    {
        /// <summary>Object id of the registration (<c>id</c>).</summary>
        public string? DeviceId { get; set; }

        /// <summary><c>groupTag</c>, trimmed; null when empty.</summary>
        public string? GroupTag { get; set; }

        /// <summary><c>purchaseOrderIdentifier</c>, trimmed; null when empty.</summary>
        public string? PurchaseOrderId { get; set; }

        /// <summary><c>displayName</c>, the device name set on the registration; null when empty.</summary>
        public string? DisplayName { get; set; }

        /// <summary>True when <c>userPrincipalName</c> is set on the registration.</summary>
        public bool UserAssigned { get; set; }
    }
}
