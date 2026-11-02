using System;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;
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

        // The .NET Framework shape of a failed connect or name lookup through HttpClient.
        private static Exception SendFailureChain(WebExceptionStatus status, SocketError socketError) =>
            new HttpRequestException(
                "An error occurred while sending the request.",
                new WebException("Unable to connect to the remote server", new SocketException((int)socketError), status, null));

        [Theory]
        [InlineData(WebExceptionStatus.ConnectFailure, SocketError.TimedOut, "status=ConnectFailure, socket=TimedOut: ")]
        [InlineData(WebExceptionStatus.NameResolutionFailure, SocketError.HostNotFound, "status=NameResolutionFailure, socket=HostNotFound: ")]
        public void Send_failure_names_status_and_socket_error(WebExceptionStatus status, SocketError socketError, string expectedPrefix)
        {
            var described = TransportErrorCodes.DescribeSendFailure(SendFailureChain(status, socketError));

            Assert.StartsWith(expectedPrefix, described);
            Assert.DoesNotContain("An error occurred while sending the request", described);
        }

        [Fact]
        public void Send_failure_of_a_tls_handshake_names_status_and_native_code()
        {
            var described = TransportErrorCodes.DescribeSendFailure(TlsFailureChain(new Win32Exception(IllegalMessage)));

            Assert.StartsWith("status=SecureChannelFailure, win32=0x80090326: ", described);
        }

        [Fact]
        public void Send_failure_without_details_is_the_innermost_message()
        {
            Assert.Equal("inner", TransportErrorCodes.DescribeSendFailure(new HttpRequestException("outer", new InvalidOperationException("inner"))));
            Assert.Equal("boom", TransportErrorCodes.DescribeSendFailure(new InvalidOperationException("boom")));
            Assert.Null(TransportErrorCodes.DescribeSendFailure(null!));
        }

        [Fact]
        public async Task Real_refused_connect_names_the_endpoint_it_tried()
        {
            // The endpoint in the message is what tells the backend from a proxy, and only a real
            // connect produces it: a hand-built SocketException carries none.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{port}/"));

            var described = TransportErrorCodes.DescribeSendFailure(failure);
            Assert.StartsWith("status=ConnectFailure, socket=ConnectionRefused: ", described);
            Assert.Contains($"127.0.0.1:{port}", described);
        }
    }
}
