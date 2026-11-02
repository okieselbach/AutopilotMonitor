using System;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Transport;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    public sealed class ProxySettingsTests
    {
        private static ProxySettings Settings(
            bool autoDetect = false, string? pac = null, string? proxy = null, string? bypass = null, string? winHttp = null) =>
            new ProxySettings(autoDetect, pac, proxy, bypass, winHttp);

        [Fact]
        public void Each_kind_of_setting_describes_itself()
        {
            Assert.Equal("no proxy", Settings().Describe());
            Assert.Equal("auto-detect", Settings(autoDetect: true).Describe());
            Assert.Equal("PAC https://pac.example.test/proxy.pac", Settings(pac: "https://pac.example.test/proxy.pac").Describe());
            Assert.Equal("proxy proxy.example.test:8080", Settings(proxy: "proxy.example.test:8080").Describe());
            Assert.Equal("auto-detect, proxy p:8080", Settings(autoDetect: true, proxy: "p:8080").Describe());
            Assert.Equal("direct", Settings().DescribeWinHttp());
            Assert.Equal("proxy p:8080", Settings(winHttp: "p:8080").DescribeWinHttp());
            Assert.Equal("unreadable (Win32 error 5)", ProxySettings.Unreadable(5).Describe());
        }

        [Theory]
        [InlineData(false, null, null, "Direct")]
        [InlineData(true, null, null, "AutoDetect")]
        [InlineData(true, "https://pac.example.test/p.pac", null, "PAC")]
        [InlineData(true, "https://pac.example.test/p.pac", "p:8080", "Proxy")]
        public void Type_keeps_the_values_the_download_rule_matches(bool autoDetect, string? pac, string? proxy, string expected)
        {
            Assert.Equal(expected, Settings(autoDetect, pac, proxy).ProxyType);
        }

        [Fact]
        public void An_unreadable_state_has_its_own_type()
        {
            Assert.Equal("Unknown", ProxySettings.Unreadable(5).ProxyType);
        }

        [Fact]
        public void Equal_content_is_equal_and_any_field_makes_it_different()
        {
            Assert.Equal(Settings(autoDetect: true), Settings(autoDetect: true));
            Assert.NotEqual(Settings(autoDetect: true), Settings());
            Assert.NotEqual(Settings(pac: "https://a.test/p"), Settings(pac: "https://b.test/p"));
            Assert.NotEqual(Settings(proxy: "a:1"), Settings(proxy: "b:1"));
            Assert.NotEqual(Settings(proxy: "a:1", bypass: "x"), Settings(proxy: "a:1", bypass: "y"));
            Assert.NotEqual(Settings(winHttp: "a:1"), Settings());
        }

        [Fact]
        public void An_empty_string_counts_as_not_set()
        {
            Assert.Equal(Settings(), Settings(pac: "", proxy: "", bypass: "", winHttp: ""));
        }

        [Theory]
        [InlineData("user:secret@p:8080")]
        [InlineData("http://user:secret@p:8080")]
        [InlineData("http=user:secret@p:8080;https=user:secret@q:8443")]
        [InlineData("user:se=cret@p:8080")]
        [InlineData("user:se;cret@p:8080")]
        [InlineData("http=user:se;cret@p:8080")]
        [InlineData("user:se://cret@p:8080")]
        public void Credentials_never_reach_logs_or_events(string configured)
        {
            var settings = Settings(proxy: configured, winHttp: configured);
            var data = settings.ToEventData();

            foreach (var text in new[] { settings.DescribeAll(), (string)data["proxyServer"], (string)data["winHttpProxy"] })
            {
                Assert.DoesNotContain("user", text);
                Assert.DoesNotContain("cret", text);
            }

            Assert.Equal(ProxySettings.CredentialsHidden, settings.Proxy);
            Assert.Equal(ProxySettings.CredentialsHidden, settings.WinHttpProxy);
            Assert.True((bool)data["proxyEnabled"]);
            Assert.True((bool)data["winHttpProxyConfigured"]);
        }

        [Theory]
        [InlineData("p:8080")]
        [InlineData("http://p:8080")]
        [InlineData("http=p:8080;https=q:8443")]
        public void A_value_without_credentials_stays_as_it_is(string configured)
        {
            Assert.Equal(configured, Settings(proxy: configured).Proxy);
            Assert.Equal(configured, Settings(winHttp: configured).WinHttpProxy);
        }

        [Theory]
        [InlineData("https://pac.example.test/p.pac?key=secret", "https://pac.example.test/p.pac")]
        [InlineData("http://user:secret@pac.example.test:8080/p.pac", "http://pac.example.test:8080/p.pac")]
        [InlineData("http://user:se=cr;et@pac.example.test/p.pac", "http://pac.example.test/p.pac")]
        [InlineData("not a url with secret", "(unparsable URL)")]
        public void A_pac_url_keeps_scheme_host_port_and_path(string configured, string expected)
        {
            Assert.Equal(expected, Settings(pac: configured).PacUrl);
        }

        [Fact]
        public void A_change_names_what_moved()
        {
            var before = Settings(autoDetect: true);

            Assert.Equal("IE settings of this account: auto-detect -> no proxy", Settings().DescribeChangeFrom(before));
            Assert.Equal("WinHTTP: direct -> proxy p:1", Settings(autoDetect: true, winHttp: "p:1").DescribeChangeFrom(before));
            Assert.Equal(
                "IE settings of this account: auto-detect -> no proxy; WinHTTP: direct -> proxy p:1",
                Settings(winHttp: "p:1").DescribeChangeFrom(before));
            Assert.Equal(
                "IE settings of this account: proxy bypass list changed",
                Settings(proxy: "p:1", bypass: "b").DescribeChangeFrom(Settings(proxy: "p:1", bypass: "a")));
        }

        [Fact]
        public void Only_the_account_settings_count_as_a_change_for_uploads()
        {
            var before = Settings(autoDetect: true);

            Assert.True(Settings().DiffersForUploadsFrom(before));
            Assert.False(Settings(autoDetect: true, winHttp: "p:1").DiffersForUploadsFrom(before));
        }

        [Fact]
        public void The_event_tells_auto_detect_from_direct()
        {
            var data = Settings(autoDetect: true).ToEventData();

            Assert.Equal("AutoDetect", data["proxyType"]);
            Assert.True((bool)data["autoDetect"]);
            Assert.False((bool)data["proxyEnabled"]);
            Assert.Null(data["proxyServer"]);
            Assert.False((bool)data["winHttpProxyConfigured"]);
            Assert.Null(data["winHttpProxy"]);
        }

        [Fact]
        public void The_event_carries_a_real_winhttp_proxy()
        {
            var data = Settings(winHttp: "p:8080").ToEventData();

            Assert.True((bool)data["winHttpProxyConfigured"]);
            Assert.Equal("p:8080", data["winHttpProxy"]);
        }

        [Fact]
        public void The_real_settings_of_this_account_can_be_read()
        {
            var settings = ProxySettingsReader.Read();

            Assert.False(settings.IsUnreadable, settings.Describe());
        }

        [Fact]
        public async Task The_real_route_can_be_read()
        {
            var read = Task.Run(() => ProxySettingsReader.ReadRoute(new Uri("https://backend.example.invalid/")));

            Assert.False(string.IsNullOrEmpty(await read));
        }
    }
}
