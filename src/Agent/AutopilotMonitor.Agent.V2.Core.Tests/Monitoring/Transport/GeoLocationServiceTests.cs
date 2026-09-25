using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Transport
{
    /// <summary>
    /// Response parsers of the geo providers. Addresses come from the RFC 5737 documentation
    /// ranges — never real ones.
    /// </summary>
    public class GeoLocationServiceTests
    {
        [Fact]
        public void ParseDoGeo_maps_country_and_egress_ip_and_ignores_the_do_service_fields()
        {
            var json = JObject.Parse(@"{
                ""ExternalIpAddress"": ""203.0.113.7"",
                ""CountryCode"": ""DE"",
                ""KeyValue_EndpointFullUri"": ""https://kv601.prod.do.dsp.mp.microsoft.com/all"",
                ""Version"": ""26A6D4D6994E8DA5"",
                ""CacheId"": ""7"",
                ""ContentCert"": false,
                ""DownloadModeFailSafe"": """"
            }");

            var result = GeoLocationService.ParseDoGeo(json);

            Assert.NotNull(result);
            Assert.Equal("DE", result.Country);
            Assert.Equal("203.0.113.7", result.Ip);
            Assert.Equal("do-geo", result.Source);
            Assert.Null(result.Region);
            Assert.Null(result.City);
            Assert.Null(result.Loc);
            Assert.Null(result.Timezone);
        }

        [Theory]
        [InlineData(@"{ ""ExternalIpAddress"": ""203.0.113.7"" }")]
        [InlineData(@"{ ""ExternalIpAddress"": ""203.0.113.7"", ""CountryCode"": """" }")]
        public void ParseDoGeo_without_country_is_a_failure(string body)
        {
            Assert.Null(GeoLocationService.ParseDoGeo(JObject.Parse(body)));
        }

        [Fact]
        public void ParseIpInfo_maps_all_fields()
        {
            var json = JObject.Parse(@"{
                ""ip"": ""198.51.100.4"", ""city"": ""Stuttgart"", ""region"": ""Baden-Wurttemberg"",
                ""country"": ""DE"", ""loc"": ""48.7823,9.1770"", ""timezone"": ""Europe/Berlin""
            }");

            var result = GeoLocationService.ParseIpInfo(json);

            Assert.Equal("DE", result.Country);
            Assert.Equal("Baden-Wurttemberg", result.Region);
            Assert.Equal("Stuttgart", result.City);
            Assert.Equal("48.7823,9.1770", result.Loc);
            Assert.Equal("Europe/Berlin", result.Timezone);
            Assert.Equal("198.51.100.4", result.Ip);
            Assert.Equal("ipinfo", result.Source);
        }

        [Fact]
        public void ParseIfConfigCo_joins_coordinates_and_leaves_loc_empty_without_them()
        {
            var withCoords = GeoLocationService.ParseIfConfigCo(JObject.Parse(@"{
                ""ip"": ""198.51.100.4"", ""country_iso"": ""DE"", ""region_name"": ""Bavaria"",
                ""city"": ""Munich"", ""latitude"": 48.13, ""longitude"": 11.58, ""time_zone"": ""Europe/Berlin""
            }"));
            var withoutCoords = GeoLocationService.ParseIfConfigCo(JObject.Parse(@"{ ""country_iso"": ""DE"" }"));

            Assert.Equal("48.13,11.58", withCoords.Loc);
            Assert.Equal("DE", withCoords.Country);
            Assert.Equal("Bavaria", withCoords.Region);
            Assert.Equal("ifconfig.co", withCoords.Source);
            Assert.Equal("", withoutCoords.Loc);
        }

        [Fact]
        public void Describe_skips_the_parts_a_provider_did_not_return()
        {
            Assert.Equal("Stuttgart, BW, DE", new GeoLocationResult { City = "Stuttgart", Region = "BW", Country = "DE" }.Describe());
            Assert.Equal("DE", new GeoLocationResult { Country = "DE", Region = "" }.Describe());
        }
    }
}
