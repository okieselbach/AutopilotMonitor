using System;
using System.Runtime.InteropServices;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Interop
{
    internal static class WinHttpNativeMethods
    {
        public const int ERROR_FILE_NOT_FOUND = 2;
        public const uint WINHTTP_ACCESS_TYPE_NAMED_PROXY = 3;

        [StructLayout(LayoutKind.Sequential)]
        public struct WINHTTP_CURRENT_USER_IE_PROXY_CONFIG
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAutoDetect;
            public IntPtr lpszAutoConfigUrl;
            public IntPtr lpszProxy;
            public IntPtr lpszProxyBypass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINHTTP_PROXY_INFO
        {
            public uint dwAccessType;
            public IntPtr lpszProxy;
            public IntPtr lpszProxyBypass;
        }

        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinHttpGetIEProxyConfigForCurrentUser(ref WINHTTP_CURRENT_USER_IE_PROXY_CONFIG config);

        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinHttpGetDefaultProxyConfiguration(ref WINHTTP_PROXY_INFO info);

        // WinHTTP allocates the strings of both structs with GlobalAlloc; the caller frees them.
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalFree(IntPtr hMem);
    }
}
