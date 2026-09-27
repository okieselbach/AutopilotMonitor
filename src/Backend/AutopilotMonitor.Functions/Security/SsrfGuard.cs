using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Prevents Server-Side Request Forgery (SSRF) for customer-configured outbound URLs: format
    /// checks at config save time, and an address gate on the webhook client's connections.
    /// </summary>
    public static class SsrfGuard
    {
        /// <summary>
        /// Allowed host suffix for the customer-supplied diagnostics SAS URL. Limits the
        /// destination to Azure Commercial Blob Storage so a malicious tenant admin can't
        /// configure an arbitrary collector endpoint that the agent would egress to.
        /// </summary>
        private const string AzureBlobHostSuffix = ".blob.core.windows.net";

        /// <summary>
        /// Validates webhook URL format (sync). Call at config save time for immediate feedback.
        /// Returns null if valid, or an error message if invalid.
        /// Empty/null URLs are considered valid (means "no webhook configured").
        /// </summary>
        public static string? ValidateWebhookUrlFormat(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return "Webhook URL is not a valid absolute URL.";

            if (uri.Scheme != Uri.UriSchemeHttps)
                return "Webhook URL must use HTTPS.";

            if (uri.IsLoopback)
                return "Webhook URL must not target localhost.";

            if (IPAddress.TryParse(uri.Host, out _))
                return "Webhook URL must use a DNS hostname, not an IP address.";

            return null;
        }

        /// <summary>
        /// Validates the customer-supplied diagnostics blob SAS URL format. The agent
        /// uploads diagnostics packages (logs, registry, hardware IDs) to this URL, so
        /// it must be locked down to Azure Commercial Blob Storage — otherwise a tenant
        /// admin could redirect agent egress to an attacker-controlled endpoint.
        /// Returns null if valid, or an error message if invalid.
        /// Empty/null URLs are considered valid (means "no diagnostics storage configured").
        /// </summary>
        public static string? ValidateAzureBlobSasUrlFormat(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return "Diagnostics SAS URL is not a valid absolute URL.";

            if (uri.Scheme != Uri.UriSchemeHttps)
                return "Diagnostics SAS URL must use HTTPS.";

            if (string.IsNullOrEmpty(uri.Host) || !uri.Host.EndsWith(AzureBlobHostSuffix, StringComparison.OrdinalIgnoreCase))
                return $"Diagnostics SAS URL host must end with {AzureBlobHostSuffix}.";

            return null;
        }

        /// <summary>
        /// <see cref="SocketsHttpHandler.ConnectCallback"/> of the webhook client — the SSRF gate.
        /// A refused destination surfaces as an <see cref="HttpRequestException"/> whose
        /// <see cref="Exception.InnerException"/> is the <see cref="SsrfException"/>.
        /// </summary>
        public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> ConnectToPublicAddress { get; } =
            CreateConnectCallback(Dns.GetHostAddressesAsync);

        /// <summary>
        /// Builds the gate: it resolves the host exactly once per connection and connects the socket
        /// only to the addresses that resolution returned, after every one of them passed
        /// <see cref="IsBlockedAddress"/> — so the address checked is the address connected to. A
        /// separate pre-flight resolution would let a DNS-rebinding host answer the check and the
        /// connect differently. HTTPS only; TLS then runs on the returned stream against the request
        /// host as usual. <paramref name="isBlocked"/> is widened only by tests that need to reach a
        /// loopback listener.
        /// </summary>
        internal static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> CreateConnectCallback(
            Func<string, CancellationToken, Task<IPAddress[]>> resolve,
            Func<IPAddress, bool>? isBlocked = null)
        {
            isBlocked ??= IsBlockedAddress;
            return async (context, cancellationToken) =>
            {
                if (context.InitialRequestMessage.RequestUri?.Scheme != Uri.UriSchemeHttps)
                    throw new SsrfException("Webhook URL must use HTTPS.");

                var host = context.DnsEndPoint.Host;
                IPAddress[] addresses;
                if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
                {
                    addresses = new[] { literal };
                }
                else
                {
                    try
                    {
                        addresses = await resolve(host, cancellationToken);
                    }
                    catch (SocketException)
                    {
                        throw new SsrfException("Could not resolve webhook hostname.");
                    }
                }

                if (addresses.Length == 0)
                    throw new SsrfException("Webhook hostname resolved to no addresses.");

                if (addresses.Any(isBlocked))
                    throw new SsrfException("Webhook URL targets a private or reserved network.");

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        // IPv4: every block of the IANA IPv4 Special-Purpose Address Registry plus multicast and
        // 240/4 (which holds the limited broadcast).
        private static readonly IPNetwork[] BlockedIpv4 =
        {
            IPNetwork.Parse("0.0.0.0/8"),        // "this network"
            IPNetwork.Parse("10.0.0.0/8"),       // private use
            IPNetwork.Parse("100.64.0.0/10"),    // shared address space (CGNAT)
            IPNetwork.Parse("127.0.0.0/8"),      // loopback
            IPNetwork.Parse("169.254.0.0/16"),   // link-local, holds the cloud metadata endpoints
            IPNetwork.Parse("172.16.0.0/12"),    // private use
            IPNetwork.Parse("192.0.0.0/24"),     // IETF protocol assignments
            IPNetwork.Parse("192.0.2.0/24"),     // documentation (TEST-NET-1)
            IPNetwork.Parse("192.31.196.0/24"),  // AS112-v4
            IPNetwork.Parse("192.52.193.0/24"),  // AMT
            IPNetwork.Parse("192.88.99.0/24"),   // deprecated 6to4 relay anycast
            IPNetwork.Parse("192.168.0.0/16"),   // private use
            IPNetwork.Parse("192.175.48.0/24"),  // direct delegation AS112
            IPNetwork.Parse("198.18.0.0/15"),    // benchmarking
            IPNetwork.Parse("198.51.100.0/24"),  // documentation (TEST-NET-2)
            IPNetwork.Parse("203.0.113.0/24"),   // documentation (TEST-NET-3)
            IPNetwork.Parse("224.0.0.0/4"),      // multicast
            IPNetwork.Parse("240.0.0.0/4"),      // reserved, limited broadcast
        };

        // IPv6: only global unicast 2000::/3 is allowed at all — one rule that refuses loopback,
        // unspecified, IPv4-mapped/-compatible, NAT64, discard, unique-local, link-/site-local and
        // multicast — and inside it the IANA special-purpose blocks are refused.
        private static readonly IPNetwork GlobalUnicastIpv6 = IPNetwork.Parse("2000::/3");

        private static readonly IPNetwork[] BlockedGlobalUnicastIpv6 =
        {
            IPNetwork.Parse("2001::/23"),         // IETF protocol assignments (Teredo, benchmarking, ORCHID, AMT, AS112, ...)
            IPNetwork.Parse("2001:db8::/32"),     // documentation
            IPNetwork.Parse("2002::/16"),         // 6to4
            IPNetwork.Parse("2620:4f:8000::/48"), // direct delegation AS112
            IPNetwork.Parse("3fff::/20"),         // documentation
        };

        /// <summary>
        /// True for every address a webhook must not connect to: all IANA special-purpose space, and
        /// any IPv6 address outside global unicast or carrying a zone id.
        /// </summary>
        internal static bool IsBlockedAddress(IPAddress addr) => addr.AddressFamily switch
        {
            AddressFamily.InterNetwork => BlockedIpv4.Any(n => n.Contains(addr)),
            AddressFamily.InterNetworkV6 => addr.ScopeId != 0
                // Refused before any IPNetwork check: IPNetwork.Contains judges an IPv4-mapped
                // address by its IPv4 form, so 2000::/3 "contains" ::ffff:8.8.8.8.
                || addr.IsIPv4MappedToIPv6
                || !GlobalUnicastIpv6.Contains(addr)
                || BlockedGlobalUnicastIpv6.Any(n => n.Contains(addr)),
            _ => true,
        };
    }

    /// <summary>
    /// Thrown when a webhook URL targets a blocked (private/reserved) network destination.
    /// </summary>
    public class SsrfException : Exception
    {
        public SsrfException(string message) : base(message) { }
    }
}
