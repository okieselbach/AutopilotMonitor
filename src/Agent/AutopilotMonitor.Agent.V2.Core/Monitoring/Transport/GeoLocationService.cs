using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Transport
{
    public class GeoLocationResult
    {
        public string Country { get; set; }
        public string Region { get; set; }
        public string City { get; set; }
        public string Loc { get; set; }
        public string Timezone { get; set; }
        public string Source { get; set; }

        /// <summary>
        /// Public/outbound (egress) IP as observed by the provider. Captured for
        /// network-correlation purposes and emitted as a separate Trace event — it is
        /// deliberately NOT part of <see cref="ToDictionary"/> so it never reaches the
        /// timeline-visible <c>device_location</c> event.
        /// </summary>
        public string Ip { get; set; }

        /// <summary>"City, Region, Country" without the parts a provider did not return.</summary>
        public string Describe() =>
            string.Join(", ", new[] { City, Region, Country }.Where(p => !string.IsNullOrEmpty(p)));

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "country", Country },
                { "region", Region },
                { "city", City },
                { "loc", Loc },
                { "timezone", Timezone },
                { "source", Source }
            };
        }
    }

    public class GeoLocationAttemptResult
    {
        public GeoLocationResult Location { get; set; }
        public string PrimaryError { get; set; }
        public string PrimaryRetryError { get; set; }
        public string FallbackError { get; set; }
        public string DoGeoError { get; set; }
    }

    public static class GeoLocationService
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

        internal const string IpInfoUrl = "https://ipinfo.io/json";
        internal const string IfConfigCoUrl = "https://ifconfig.co/json";

        /// <summary>
        /// Delivery Optimization's own geo endpoint. Country and egress IP only (no region, city,
        /// coordinates or timezone), but it is part of the Windows Update / DO endpoint set that
        /// Autopilot needs anyway, so it stays reachable in networks that block public geo services.
        /// </summary>
        internal const string DoGeoUrl = "https://geo.prod.do.dsp.mp.microsoft.com/geo";

        // Static HttpClient to avoid socket exhaustion from repeated short-lived instances.
        // HttpClient is designed to be reused across requests.
        private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();

        private static HttpClient CreateSharedHttpClient()
        {
            var client = new HttpClient { Timeout = RequestTimeout };
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            return client;
        }

        public static async Task<GeoLocationAttemptResult> GetLocationAsync(AgentLogger logger)
        {
            var attempt = new GeoLocationAttemptResult();

            // Try ipinfo.io first
            var (result, error) = await TryProvider("ipinfo.io", IpInfoUrl, ParseIpInfo, logger);
            if (result != null)
            {
                attempt.Location = result;
                return attempt;
            }
            attempt.PrimaryError = error;

            // Retry ipinfo.io once after a short delay (network may still be initializing during Autopilot)
            logger?.Info($"GeoLocation: Retrying ipinfo.io after {RetryDelay.TotalSeconds}s...");
            await Task.Delay(RetryDelay);

            (result, error) = await TryProvider("ipinfo.io", IpInfoUrl, ParseIpInfo, logger);
            if (result != null)
            {
                attempt.Location = result;
                return attempt;
            }
            attempt.PrimaryRetryError = error;

            // Fallback to ifconfig.co
            (result, error) = await TryProvider("ifconfig.co", IfConfigCoUrl, ParseIfConfigCo, logger);
            if (result != null)
            {
                attempt.Location = result;
                return attempt;
            }
            attempt.FallbackError = error;

            // Last resort: Delivery Optimization geo — country only, but better than no location
            (result, error) = await TryProvider("do-geo", DoGeoUrl, ParseDoGeo, logger);
            if (result != null)
            {
                attempt.Location = result;
                return attempt;
            }
            attempt.DoGeoError = error;

            logger?.Warning("GeoLocation: All providers failed, skipping location event");
            return attempt;
        }

        private static async Task<(GeoLocationResult result, string error)> TryProvider(
            string name, string url, Func<JObject, GeoLocationResult> parse, AgentLogger logger)
        {
            try
            {
                logger?.Info($"GeoLocation: Querying {name}...");

                using (var httpResponse = await SharedHttpClient.GetAsync(url))
                {
                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        var error = $"HTTP {(int)httpResponse.StatusCode} ({httpResponse.ReasonPhrase})";
                        logger?.Warning($"GeoLocation: {name} failed: {error}");
                        return (null, error);
                    }

                    var response = await httpResponse.Content.ReadAsStringAsync();
                    var result = parse(JObject.Parse(response));
                    if (result == null)
                    {
                        var error = "No country in response";
                        logger?.Warning($"GeoLocation: {name} failed: {error}");
                        return (null, error);
                    }

                    logger?.Info($"GeoLocation: {name} returned {result.Describe()}");
                    return (result, null);
                }
            }
            catch (TaskCanceledException)
            {
                var error = $"Timeout ({RequestTimeout.TotalSeconds}s)";
                logger?.Warning($"GeoLocation: {name} failed: {error}");
                return (null, error);
            }
            catch (Exception ex)
            {
                var error = ex.Message;
                logger?.Warning($"GeoLocation: {name} failed: {error}");
                return (null, error);
            }
        }

        internal static GeoLocationResult ParseIpInfo(JObject json) =>
            new GeoLocationResult
            {
                Country = json.Value<string>("country"),
                Region = json.Value<string>("region"),
                City = json.Value<string>("city"),
                Loc = json.Value<string>("loc"),
                Timezone = json.Value<string>("timezone"),
                Ip = json.Value<string>("ip"),
                Source = "ipinfo"
            };

        internal static GeoLocationResult ParseIfConfigCo(JObject json)
        {
            var latitude = json.Value<string>("latitude") ?? "";
            var longitude = json.Value<string>("longitude") ?? "";
            var loc = !string.IsNullOrEmpty(latitude) && !string.IsNullOrEmpty(longitude)
                ? $"{latitude},{longitude}"
                : "";

            return new GeoLocationResult
            {
                Country = json.Value<string>("country_iso"),
                Region = json.Value<string>("region_name"),
                City = json.Value<string>("city"),
                Loc = loc,
                Timezone = json.Value<string>("time_zone"),
                Ip = json.Value<string>("ip"),
                Source = "ifconfig.co"
            };
        }

        /// <summary>
        /// The DO geo response carries DO service configuration next to the geo fields; only
        /// <c>CountryCode</c> and <c>ExternalIpAddress</c> are location data. Without a country
        /// the response is worthless as a location, so it counts as a failure (null).
        /// </summary>
        internal static GeoLocationResult ParseDoGeo(JObject json)
        {
            var country = json.Value<string>("CountryCode");
            if (string.IsNullOrEmpty(country))
                return null;

            return new GeoLocationResult
            {
                Country = country,
                Ip = json.Value<string>("ExternalIpAddress"),
                Source = "do-geo"
            };
        }
    }
}
