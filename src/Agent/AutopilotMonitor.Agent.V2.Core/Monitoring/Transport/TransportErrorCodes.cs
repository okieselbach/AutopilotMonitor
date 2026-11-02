using System;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

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

        /// <summary>
        /// What a failed send hit, in one line: the <see cref="WebException.Status"/>, the socket error
        /// or native code, and the innermost message. On .NET Framework a socket error's message names
        /// the endpoint it tried, which tells a backend that does not answer from a proxy that does not.
        /// </summary>
        public static string DescribeSendFailure(Exception ex)
        {
            if (ex == null) return null;

            WebExceptionStatus? status = null;
            SocketError? socketError = null;
            var innermost = ex;
            for (var current = ex; current != null; current = current.InnerException)
            {
                innermost = current;
                if (current is WebException web && status == null) status = web.Status;
                if (current is SocketException socket) socketError = socket.SocketErrorCode;
            }

            var code = socketError.HasValue ? $"socket={socketError.Value}" : NativeCodePart(ex);
            var details = status.HasValue
                ? (code == null ? $"status={status.Value}" : $"status={status.Value}, {code}")
                : code;
            return details == null ? innermost.Message : $"{details}: {innermost.Message}";
        }

        private static string NativeCodePart(Exception ex)
        {
            var code = InnermostNativeCode(ex);
            return code == null ? null : $"win32={code}";
        }

        /// <summary>Eight-digit hex with <c>0x</c>: <c>-2146893018</c> → <c>0x80090326</c>.</summary>
        public static string Hex(int value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
    }
}
