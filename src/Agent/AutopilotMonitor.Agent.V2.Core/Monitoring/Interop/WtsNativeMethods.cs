using System;
using System.Runtime.InteropServices;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Interop
{
    /// <summary>
    /// Win32 P/Invoke declarations for WTS (Remote Desktop Services) session queries.
    /// Used by <c>DesktopArrivalDetector</c> to resolve the logged-on user of an
    /// interactive session directly from the session manager — a single fast kernel
    /// round-trip, unlike the WMI <c>Win32_Process.GetOwner</c> path which depends on
    /// the WinMgmt service and fails persistently on some devices (session 4d5a0b78:
    /// GetOwner failed on every poll while WTS-level session data stayed available).
    /// </summary>
    internal static class WtsNativeMethods
    {
        /// <summary>WTS_INFO_CLASS.WTSUserName — user name associated with the session.</summary>
        public const int WTSUserName = 5;

        /// <summary>WTS_INFO_CLASS.WTSDomainName — domain of the user associated with the session.</summary>
        public const int WTSDomainName = 7;

        /// <summary>WTS_CURRENT_SERVER_HANDLE — query the local machine.</summary>
        public static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool WTSQuerySessionInformation(
            IntPtr hServer,
            int sessionId,
            int wtsInfoClass,
            out IntPtr ppBuffer,
            out int pBytesReturned);

        [DllImport("wtsapi32.dll")]
        public static extern void WTSFreeMemory(IntPtr pMemory);

        /// <summary>
        /// Queries a string-typed WTS session property (<see cref="WTSUserName"/> /
        /// <see cref="WTSDomainName"/>) for <paramref name="sessionId"/> on the local
        /// machine. Returns <c>null</c> when the API call fails; an empty string is a
        /// successful query with no value (e.g. no user associated with the session).
        /// Always frees the WTS buffer.
        /// </summary>
        public static string QuerySessionString(int sessionId, int wtsInfoClass)
        {
            var buffer = IntPtr.Zero;
            try
            {
                if (!WTSQuerySessionInformation(WTS_CURRENT_SERVER_HANDLE, sessionId, wtsInfoClass, out buffer, out var bytesReturned))
                    return null;

                // bytesReturned includes the trailing null terminator (2 bytes Unicode);
                // <= 2 means "no characters".
                return bytesReturned > 2 ? Marshal.PtrToStringUni(buffer) : string.Empty;
            }
            finally
            {
                if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
            }
        }
    }
}
