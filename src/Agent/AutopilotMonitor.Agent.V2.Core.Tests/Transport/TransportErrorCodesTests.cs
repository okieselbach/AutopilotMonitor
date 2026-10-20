using System;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    public sealed class TransportErrorCodesTests
    {
        private const int IllegalMessage = unchecked((int)0x80090326);

        // The .NET Framework shape of a failed TLS handshake through HttpClient.
        private static Exception TlsFailureChain(params Exception[] innermost) =>
            new HttpRequestException(
                "An error occurred while sending the request.",
                new WebException(
                    "The request was aborted: Could not create SSL/TLS secure channel.",
                    new AuthenticationException("A call to SSPI failed, see inner exception.", innermost.Length > 0 ? innermost[0] : null),
                    WebExceptionStatus.SecureChannelFailure,
                    null));

        [Fact]
        public void Hex_is_eight_digits_with_prefix()
        {
            Assert.Equal("0x80090326", TransportErrorCodes.Hex(IllegalMessage));
            Assert.Equal("0x00002746", TransportErrorCodes.Hex(10054));
        }

        [Fact]
        public void Innermost_win32_code_of_a_tls_failure()
        {
            var chain = TlsFailureChain(new Win32Exception(IllegalMessage));

            Assert.Equal("0x80090326", TransportErrorCodes.InnermostNativeCode(chain));
        }

        [Fact]
        public void Innermost_wins_over_an_outer_win32_exception()
        {
            // The outer code is whatever the last Win32 error was; only the innermost counts.
            var chain = new Win32Exception("outer", new Win32Exception(IllegalMessage));

            Assert.Equal("0x80090326", TransportErrorCodes.InnermostNativeCode(chain));
        }

        [Fact]
        public void No_win32_exception_no_code()
        {
            Assert.Null(TransportErrorCodes.InnermostNativeCode(TlsFailureChain()));
            Assert.Null(TransportErrorCodes.InnermostNativeCode(null!));
        }

        [Fact]
        public void Message_gets_the_code_appended()
        {
            var chain = TlsFailureChain(new Win32Exception(IllegalMessage));

            Assert.Equal(
                "An error occurred while sending the request. (win32 0x80090326)",
                TransportErrorCodes.MessageWithNativeCode(chain));
        }

        [Fact]
        public void Message_without_a_code_stays_as_is()
        {
            Assert.Equal("boom", TransportErrorCodes.MessageWithNativeCode(new InvalidOperationException("boom")));
            Assert.Null(TransportErrorCodes.MessageWithNativeCode(null!));
        }
    }
}
