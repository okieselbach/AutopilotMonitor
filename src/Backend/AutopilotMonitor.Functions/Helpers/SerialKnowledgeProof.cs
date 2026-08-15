namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// The Progress Portal's authorization model: a roleless authenticated end user may see a device's
/// enrollment progress iff they can present that device's serial number ("who knows the serial may
/// see the device"). This is the single comparison both enforcement points use — the REST events
/// route (ProgressPortalFunction) and the SignalR session-group join (SignalRAddToGroupFunction) —
/// so the proof can never drift between the initial load and the live stream.
/// </summary>
public static class SerialKnowledgeProof
{
    /// <summary>
    /// Whether <paramref name="providedSerialNumber"/> proves knowledge of the session's serial.
    /// Trimmed, case-insensitive. Fail-closed: a session without a stored serial (or a missing
    /// proof) never matches, making such sessions unreachable through the proof-gated paths.
    /// </summary>
    public static bool Matches(string? sessionSerialNumber, string? providedSerialNumber)
    {
        if (string.IsNullOrWhiteSpace(sessionSerialNumber) || string.IsNullOrWhiteSpace(providedSerialNumber))
            return false;

        return string.Equals(
            sessionSerialNumber.Trim(),
            providedSerialNumber.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }
}
