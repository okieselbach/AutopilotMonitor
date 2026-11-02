using System;
using System.Net;
using System.Runtime.InteropServices;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;

namespace AutopilotMonitor.Agent.V2.Core.Transport
{
    internal static class ProxySettingsReader
    {
        public const string RouteDirect = "direct";

        /// <summary>Reads the registry through WinHTTP, no network. Does not throw on a failed read.</summary>
        public static ProxySettings Read()
        {
            var ie = new WinHttpNativeMethods.WINHTTP_CURRENT_USER_IE_PROXY_CONFIG();
            var winHttp = new WinHttpNativeMethods.WINHTTP_PROXY_INFO();
            try
            {
                if (!WinHttpNativeMethods.WinHttpGetIEProxyConfigForCurrentUser(ref ie))
                {
                    var error = Marshal.GetLastWin32Error();
                    // The account has never had settings written, so nothing configures a proxy.
                    if (error != WinHttpNativeMethods.ERROR_FILE_NOT_FOUND) return ProxySettings.Unreadable(error);
                }

                if (!WinHttpNativeMethods.WinHttpGetDefaultProxyConfiguration(ref winHttp))
                {
                    return ProxySettings.Unreadable(Marshal.GetLastWin32Error());
                }

                return new ProxySettings(
                    autoDetect: ie.fAutoDetect,
                    pacUrl: Marshal.PtrToStringUni(ie.lpszAutoConfigUrl),
                    proxy: Marshal.PtrToStringUni(ie.lpszProxy),
                    bypass: Marshal.PtrToStringUni(ie.lpszProxyBypass),
                    winHttpProxy: winHttp.dwAccessType == WinHttpNativeMethods.WINHTTP_ACCESS_TYPE_NAMED_PROXY
                        ? Marshal.PtrToStringUni(winHttp.lpszProxy)
                        : null);
            }
            finally
            {
                Free(ie.lpszAutoConfigUrl);
                Free(ie.lpszProxy);
                Free(ie.lpszProxyBypass);
                Free(winHttp.lpszProxy);
                Free(winHttp.lpszProxyBypass);
            }
        }

        /// <summary>
        /// The next hop .NET chooses for <paramref name="target"/>: the same decision an
        /// <c>HttpClientHandler</c> with default settings takes, PAC and auto-detect evaluated.
        /// Auto-detect can take seconds on a network without WPAD.
        /// </summary>
        public static string ReadRoute(Uri target)
        {
            var hop = WebRequest.DefaultWebProxy?.GetProxy(target);
            if (hop == null || string.Equals(hop.Host, target.Host, StringComparison.OrdinalIgnoreCase)) return RouteDirect;
            return hop.IsDefaultPort ? hop.Host : $"{hop.Host}:{hop.Port}";
        }

        private static void Free(IntPtr memory)
        {
            if (memory != IntPtr.Zero) WinHttpNativeMethods.GlobalFree(memory);
        }
    }
}
