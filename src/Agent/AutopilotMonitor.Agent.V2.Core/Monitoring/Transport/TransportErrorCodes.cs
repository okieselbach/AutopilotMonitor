using System;
using System.ComponentModel;
using System.Globalization;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Transport
{
    /// <summary>
    /// The native status behind a transport failure. Schannel and WinHTTP report it only on the
    /// innermost <see cref="Win32Exception"/> of the chain (e.g. <c>0x80090326</c>); the outer
    /// <c>HttpRequestException</c> and <c>WebException</c> texts are generic.
    /// </summary>
    internal static class TransportErrorCodes
    {
        /// <summary>The innermost <see cref="Win32Exception.NativeErrorCode"/> as <c>0x80090326</c>; null when the chain has none.</summary>
        public static string InnermostNativeCode(Exception ex)
        {
            string code = null;
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is Win32Exception win32)
                    code = Hex(win32.NativeErrorCode);
            }
            return code;
        }

        /// <summary><paramref name="ex"/>'s message with the innermost native code appended as <c>(win32 0x80090326)</c>.</summary>
        public static string MessageWithNativeCode(Exception ex)
        {
            if (ex == null) return null;
            var code = InnermostNativeCode(ex);
            return code == null ? ex.Message : $"{ex.Message} (win32 {code})";
        }

        /// <summary>Eight-digit hex with <c>0x</c>: <c>-2146893018</c> → <c>0x80090326</c>.</summary>
        public static string Hex(int value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
    }
}
