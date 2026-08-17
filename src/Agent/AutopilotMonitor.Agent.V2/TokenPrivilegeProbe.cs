using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutopilotMonitor.Agent.V2
{
    /// <summary>
    /// Checks whether the current process token HOLDS a privilege — present in the token at
    /// all, enabled or disabled. This is different from the enable-on-demand pattern in
    /// <c>UefiSecureBootCertReader</c>: a privilege that was REMOVED from the token cannot be
    /// re-enabled with <c>AdjustTokenPrivileges</c> by anyone, including SYSTEM.
    /// <para>
    /// Why install mode needs this: MDM-originated install chains (EnterpriseDesktopAppManagement
    /// CSP → msiexec → bootstrap runner) carry a SYSTEM token with several privileges removed —
    /// observed 2026-08-17 with the Bootstrap MSI: SeTimeZonePrivilege and
    /// SeSystemEnvironmentPrivilege were gone, so tzutil exited 5 (ACCESS_DENIED) and firmware
    /// reads reported privilege_denied. WMI <c>Win32_Process.Create</c> duplicates the CALLER's
    /// token onto the new process, so the restriction propagates to the runtime; the Task
    /// Scheduler service, by contrast, starts the task with a fresh full SYSTEM token. The
    /// launch orchestration therefore needs to know up front whether its own token is stripped.
    /// </para>
    /// <para>
    /// Fail-soft: every API failure reports "held" so the default (WMI-first) launch path is
    /// kept — a probe defect must never change behavior for the healthy platform-script chain.
    /// </para>
    /// </summary>
    internal static class TokenPrivilegeProbe
    {
        public const string TimeZonePrivilege = "SeTimeZonePrivilege";

        public static bool IsPrivilegeHeld(string privilegeName)
        {
            try
            {
                NativeMethods.LUID target;
                if (!NativeMethods.LookupPrivilegeValueW(null, privilegeName, out target))
                    return true;

                SafeTokenHandle token;
                if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(),
                        NativeMethods.TOKEN_QUERY, out token))
                {
                    if (token != null) token.Dispose();
                    return true;
                }

                using (token)
                {
                    uint required;
                    NativeMethods.GetTokenInformation(token, NativeMethods.TokenPrivileges,
                        IntPtr.Zero, 0, out required);
                    if (required == 0 || required > 64 * 1024)
                        return true;

                    var buffer = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenPrivileges,
                                buffer, required, out required))
                            return true;

                        // TOKEN_PRIVILEGES layout: DWORD PrivilegeCount, then PrivilegeCount ×
                        // LUID_AND_ATTRIBUTES { LUID (uint Low, int High), DWORD Attributes } = 12 bytes.
                        var count = Marshal.ReadInt32(buffer);
                        for (var i = 0; i < count; i++)
                        {
                            var entry = 4 + i * 12;
                            var low = unchecked((uint)Marshal.ReadInt32(buffer, entry));
                            var high = Marshal.ReadInt32(buffer, entry + 4);
                            if (low == target.LowPart && high == target.HighPart)
                                return true;
                        }
                        return false;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
            catch
            {
                return true;
            }
        }

        private sealed class SafeTokenHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeTokenHandle() : base(true) { }

            protected override bool ReleaseHandle()
            {
                return NativeMethods.CloseHandle(handle);
            }
        }

        private static class NativeMethods
        {
            public const uint TOKEN_QUERY = 0x0008;
            public const int TokenPrivileges = 3; // TOKEN_INFORMATION_CLASS.TokenPrivileges

            [StructLayout(LayoutKind.Sequential)]
            public struct LUID
            {
                public uint LowPart;
                public int HighPart;
            }

            [DllImport("kernel32.dll")]
            public static extern IntPtr GetCurrentProcess();

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeTokenHandle tokenHandle);

            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool LookupPrivilegeValueW(string lpSystemName, string lpName, out LUID lpLuid);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetTokenInformation(SafeTokenHandle tokenHandle, int tokenInformationClass,
                IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr handle);
        }
    }
}
