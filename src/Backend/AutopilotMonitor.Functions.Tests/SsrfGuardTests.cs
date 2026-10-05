using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Format checks and the address classifier. The connection gate itself is covered on real sockets
/// in <see cref="WebhookConnectGateTests"/>.
/// </summary>
public class SsrfGuardTests
{
    // ── ValidateWebhookUrlFormat: valid URLs ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ValidateFormat_NullOrEmpty_ReturnsNull(string? url)
    {
        Assert.Null(SsrfGuard.ValidateWebhookUrlFormat(url));
    }

    [Theory]
    [InlineData("https://hooks.slack.com/services/T00/B00/xxx")]
    [InlineData("https://default0000.00.environment.api.powerplatform.com:443/powerautomate/automations/direct/workflows/abc/triggers/manual/paths/invoke")]
    [InlineData("https://prod-12.westeurope.logic.azure.com:443/workflows/abc")]
    public void ValidateFormat_ValidHttpsUrls_ReturnsNull(string url)
    {
        Assert.Null(SsrfGuard.ValidateWebhookUrlFormat(url));
    }

    // ── ValidateWebhookUrlFormat: invalid URLs ──

    [Fact]
    public void ValidateFormat_HttpScheme_ReturnsError()
    {
        var result = SsrfGuard.ValidateWebhookUrlFormat("http://hooks.slack.com/services/T00/B00/xxx");
        Assert.Contains("HTTPS", result);
    }

    [Fact]
    public void ValidateFormat_FtpScheme_ReturnsError()
    {
        var result = SsrfGuard.ValidateWebhookUrlFormat("ftp://example.com/webhook");
        Assert.Contains("HTTPS", result);
    }

    [Fact]
    public void ValidateFormat_NotAUrl_ReturnsError()
    {
        var result = SsrfGuard.ValidateWebhookUrlFormat("not-a-url");
        Assert.Contains("valid absolute URL", result);
    }

    [Fact]
    public void ValidateFormat_Localhost_ReturnsError()
    {
        var result = SsrfGuard.ValidateWebhookUrlFormat("https://localhost/webhook");
        Assert.Contains("localhost", result);
    }

    [Fact]
    public void ValidateFormat_LoopbackIpv6_ReturnsError()
    {
        var result = SsrfGuard.ValidateWebhookUrlFormat("https://[::1]/webhook");
        Assert.Contains("localhost", result);
    }

    [Theory]
    [InlineData("https://10.0.0.1/webhook")]
    [InlineData("https://192.168.1.1/webhook")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://172.16.0.1/webhook")]
    public void ValidateFormat_IpAddress_ReturnsError(string url)
    {
        var result = SsrfGuard.ValidateWebhookUrlFormat(url);
        Assert.Contains("DNS hostname", result);
    }

    // ── IsBlockedAddress: blocked IPv4 ranges ──

    [Theory]
    [InlineData("127.0.0.1")]       // loopback
    [InlineData("127.0.0.2")]       // loopback range
    [InlineData("10.0.0.1")]        // RFC 1918 Class A
    [InlineData("10.255.255.255")]   // RFC 1918 Class A end
    [InlineData("172.16.0.1")]      // RFC 1918 Class B start
    [InlineData("172.31.255.255")]   // RFC 1918 Class B end
    [InlineData("192.168.0.1")]     // RFC 1918 Class C
    [InlineData("192.168.255.255")] // RFC 1918 Class C end
    [InlineData("169.254.169.254")] // Azure IMDS
    [InlineData("169.254.0.1")]     // Link-local
    [InlineData("0.0.0.0")]         // Current network
    [InlineData("0.255.255.255")]   // Current network end
    [InlineData("100.64.0.1")]      // CGNAT
    [InlineData("100.127.255.255")] // CGNAT end
    [InlineData("192.0.0.1")]       // IETF Protocol Assignments
    [InlineData("192.0.2.1")]       // TEST-NET-1
    [InlineData("198.18.0.1")]      // Benchmark
    [InlineData("198.19.255.255")]  // Benchmark end
    [InlineData("198.51.100.1")]    // TEST-NET-2
    [InlineData("203.0.113.1")]     // TEST-NET-3
    [InlineData("224.0.0.1")]       // Multicast
    [InlineData("240.0.0.1")]       // Reserved
    [InlineData("255.255.255.255")] // Broadcast
    public void IsBlocked_PrivateIpv4_ReturnsTrue(string ip)
    {
        Assert.True(SsrfGuard.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    // ── IsBlockedAddress: blocked IPv6 ──

    [Theory]
    [InlineData("::1")]             // loopback
    [InlineData("fe80::1")]         // link-local
    [InlineData("fc00::1")]         // unique local
    [InlineData("fd00::1")]         // unique local
    [InlineData("ff02::1")]         // multicast
    public void IsBlocked_PrivateIpv6_ReturnsTrue(string ip)
    {
        Assert.True(SsrfGuard.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    // ── IsBlockedAddress: IPv6-mapped IPv4 ──

    [Theory]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:192.168.1.1")]
    public void IsBlocked_Ipv6MappedPrivateIpv4_ReturnsTrue(string ip)
    {
        Assert.True(SsrfGuard.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    // ── IsBlockedAddress: allowed public IPs ──

    [Theory]
    [InlineData("8.8.8.8")]         // Google DNS
    [InlineData("1.1.1.1")]         // Cloudflare
    [InlineData("52.114.77.33")]    // Azure public (Teams range)
    [InlineData("104.18.6.192")]    // Cloudflare (Slack range)
    [InlineData("172.15.255.255")]  // Just below RFC 1918 172.16/12
    [InlineData("172.32.0.0")]      // Just above RFC 1918 172.16/12
    [InlineData("100.63.255.255")]  // Just below CGNAT
    [InlineData("100.128.0.0")]     // Just above CGNAT
    public void IsBlocked_PublicIp_ReturnsFalse(string ip)
    {
        Assert.False(SsrfGuard.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    // ── IsBlockedAddress: IPv6 beyond the classic ranges ──

    [Theory]
    [InlineData("::")]                     // unspecified
    [InlineData("::ffff:8.8.8.8")]         // IPv4-mapped, even of a public address
    [InlineData("::8.8.8.8")]              // IPv4-compatible (deprecated)
    [InlineData("64:ff9b::a00:1")]         // NAT64 of 10.0.0.1
    [InlineData("2002:a00:1::1")]          // 6to4 of 10.0.0.1
    [InlineData("2001::1")]                // Teredo
    [InlineData("2001:db8::1")]            // documentation
    [InlineData("fec0::1")]                // deprecated site-local
    [InlineData("4000::1")]                // outside global unicast
    [InlineData("e000::1")]                // outside global unicast
    public void IsBlocked_Ipv6OutsidePublicUnicast_ReturnsTrue(string ip)
    {
        Assert.True(SsrfGuard.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("fe80::1%5")]
    [InlineData("2606:4700:4700::1111%5")]
    public void IsBlocked_Ipv6WithZoneId_ReturnsTrue(string ip)
    {
        Assert.True(SsrfGuard.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("2606:4700:4700::1111")]   // Cloudflare
    [InlineData("2a00:1450:4001::1")]      // Google
    [InlineData("2001:200::1")]            // just above 2001::/23
    [InlineData("2003::1")]                // just above 6to4 2002::/16
    [InlineData("2001:db9::1")]            // just above documentation 2001:db8::/32
    [InlineData("192.0.1.0")]              // just above 192.0.0.0/24
    [InlineData("198.20.0.0")]             // just above benchmarking 198.18.0.0/15
    [InlineData("223.255.255.255")]        // just below multicast
    public void IsBlocked_PublicNeighbourOfRefusedBlock_ReturnsFalse(string ip)
    {
        Assert.False(SsrfGuard.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    // ── IsBlockedAddress: every IANA special-purpose block (tests/fixtures/iana-special-purpose-addresses) ──

    private static readonly string[] RegistryFiles = { "iana-ipv4-special-registry-1.csv", "iana-ipv6-special-registry-1.csv" };

    public static IEnumerable<object[]> RegistryBlocks() =>
        RegistryFiles.SelectMany(file => ReadRegistryBlocks(file).Select(block => new object[] { file, block }));

    [Fact]
    public void Registry_IsReadCompletely()
    {
        foreach (var file in RegistryFiles)
        {
            var blocks = ReadRegistryBlocks(file);
            Assert.True(blocks.Count >= 20, $"{file}: only {blocks.Count} blocks read");
            Assert.All(blocks, block => Assert.Matches(@"^[0-9a-f.:]+/\d{1,3}$", block));
        }
    }

    [Theory]
    [MemberData(nameof(RegistryBlocks))]
    public void IsBlocked_EveryIanaSpecialPurposeBlock_FirstAndLastAddress(string file, string block)
    {
        var network = IPNetwork.Parse(block);
        var first = network.BaseAddress.GetAddressBytes();
        var last = (byte[])first.Clone();
        for (var bit = network.PrefixLength; bit < last.Length * 8; bit++)
            last[bit / 8] |= (byte)(0x80 >> (bit % 8));

        foreach (var ip in new[] { new IPAddress(first), new IPAddress(last) })
            Assert.True(SsrfGuard.IsBlockedAddress(ip), $"{file}: {block} -> {ip}");
    }

    private static List<string> ReadRegistryBlocks(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var rows = ParseCsv(File.ReadAllText(Path.Combine(dir!.FullName, "tests", "fixtures", "iana-special-purpose-addresses", file)));
        Assert.Equal("Address Block", rows[0][0]);
        // Footnote markers ("2002::/16 [3]") dropped, cells that list two blocks split.
        return rows.Skip(1)
            .SelectMany(row => row[0].Split(','))
            .Select(block => Regex.Replace(block, @"\[\d+\]", "").Trim())
            .Where(block => block.Length > 0)
            .ToList();
    }

    /// <summary>RFC 4180 reader — the registry quotes cells, wraps the RFC column over lines and doubles quotes.</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString());
                rows.Add(row);
                row = new List<string>();
                cell.Clear();
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    // ── ValidateAzureBlobSasUrlFormat ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ValidateBlobSas_NullOrEmpty_ReturnsNull(string? url)
    {
        Assert.Null(SsrfGuard.ValidateAzureBlobSasUrlFormat(url));
    }

    [Theory]
    [InlineData("https://contoso.blob.core.windows.net/diag?sv=2022-11-02&sig=abc")]
    [InlineData("https://fabrikam.blob.core.windows.net/container?sv=2022-11-02&sig=abc")]
    [InlineData("https://example.BLOB.core.windows.NET/container?sv=2022-11-02&sig=abc")] // case-insensitive
    public void ValidateBlobSas_ValidAzureBlobUrl_ReturnsNull(string url)
    {
        Assert.Null(SsrfGuard.ValidateAzureBlobSasUrlFormat(url));
    }

    [Fact]
    public void ValidateBlobSas_NotAUrl_ReturnsError()
    {
        var result = SsrfGuard.ValidateAzureBlobSasUrlFormat("not-a-url");
        Assert.Contains("valid absolute URL", result);
    }

    [Fact]
    public void ValidateBlobSas_HttpScheme_ReturnsError()
    {
        var result = SsrfGuard.ValidateAzureBlobSasUrlFormat("http://contoso.blob.core.windows.net/diag?sig=abc");
        Assert.Contains("HTTPS", result);
    }

    [Theory]
    [InlineData("https://attacker.example.com/x")]
    [InlineData("https://attacker.example/x")]
    [InlineData("https://blob.core.windows.net.attacker.example/x")]   // suffix lookalike
    [InlineData("https://contoso.blob.core.chinacloudapi.cn/diag")]    // sovereign cloud
    [InlineData("https://contoso.blob.core.usgovcloudapi.net/diag")]   // sovereign cloud
    [InlineData("https://contoso.dfs.core.windows.net/diag")]          // ADLS endpoint
    [InlineData("https://localhost/diag")]
    [InlineData("https://10.0.0.1/diag")]
    public void ValidateBlobSas_NonAzureCommercialHost_ReturnsError(string url)
    {
        var result = SsrfGuard.ValidateAzureBlobSasUrlFormat(url);
        Assert.Contains(".blob.core.windows.net", result);
    }
}
