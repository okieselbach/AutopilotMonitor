using System;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Reads AAD join information from the Windows registry and classifies the
    /// signed-in user e-mail into "real user" vs. "provisioning placeholder".
    ///
    /// Registry path: HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo\&lt;thumbprint&gt;\UserEmail
    ///
    /// Placeholder users (<see cref="IsPlaceholderUserEmail"/>) appear transiently during
    /// Autopilot pre-provisioning (WhiteGlove) and must NOT be counted as a real AAD join.
    /// </summary>
    internal static class AadJoinInfo
    {
        internal const string JoinInfoRegistryPath =
            @"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo";

        /// <summary>
        /// Reads the first JoinInfo sub-key and returns the UserEmail + thumbprint.
        /// Returns true when ANY sub-key was found (even without UserEmail); the caller
        /// inspects the out-parameters to decide semantics.
        /// </summary>
        /// <param name="userEmail">Raw UserEmail value (may be null/empty).</param>
        /// <param name="thumbprint">The certificate thumbprint sub-key name (may be null).</param>
        /// <param name="isPlaceholderUser">
        /// True when <paramref name="userEmail"/> matches a known provisioning-placeholder
        /// pattern (foouser@*, autopilot@*). Placeholders are NOT a real AAD join.
        /// </param>
        internal static bool TryReadAadJoinedUser(
            out string userEmail,
            out string thumbprint,
            out bool isPlaceholderUser)
        {
            userEmail = null;
            thumbprint = null;
            isPlaceholderUser = false;

            try
            {
                using (var joinInfoKey = Registry.LocalMachine.OpenSubKey(JoinInfoRegistryPath))
                {
                    if (joinInfoKey == null)
                        return false;

                    var subKeyNames = joinInfoKey.GetSubKeyNames();
                    if (subKeyNames == null || subKeyNames.Length == 0)
                        return false;

                    thumbprint = subKeyNames[0];
                    using (var subKey = joinInfoKey.OpenSubKey(thumbprint))
                    {
                        if (subKey == null)
                            return true;

                        userEmail = subKey.GetValue("UserEmail")?.ToString();
                        isPlaceholderUser = IsPlaceholderUserEmail(userEmail);
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns true when the given email matches a known transient provisioning-account
        /// pattern (foouser@*, autopilot@*). These accounts appear during Autopilot device
        /// preparation and must not be treated as a real signed-in user.
        /// </summary>
        internal static bool IsPlaceholderUserEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            return email.StartsWith("foouser@", StringComparison.OrdinalIgnoreCase)
                || email.StartsWith("autopilot@", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The domain part of a sign-in address, or "unknown" when there is none. Telemetry
        /// carries this instead of the address: the part before the last '@' stays on the device.
        /// </summary>
        internal static string ExtractUserDomain(string email)
        {
            if (string.IsNullOrEmpty(email)) return "unknown";
            var at = email.LastIndexOf('@');
            return at >= 0 && at + 1 < email.Length ? email.Substring(at + 1) : "unknown";
        }
    }
}
