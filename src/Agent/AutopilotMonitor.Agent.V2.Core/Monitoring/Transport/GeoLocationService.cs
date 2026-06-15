using System;
using System.Collections.Generic;
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
    }

    public static class GeoLocationService
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

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
            var (result, error) = await TryIpInfo(logger);
            if (result != null)
            {
                attempt.Location = result;
                return attempt;
            }
            attempt.PrimaryError = error;

            // Retry ipinfo.io once after a short delay (network may still be initializing during Autopilot)
            logger?.Info($"GeoLocation: Retrying ipinfo.io after {RetryDelay.TotalSeconds}s...");
            await Task.Delay(RetryDelay);

            (result, error) = await TryIpInfo(logger);
            if (result != null)
            {
                attempt.Location = result;
                return attempt;
            }
            attempt.PrimaryRetryError = error;

            // Fallback to ifconfig.co
            (result, error) = await TryIfConfigCo(logger);
            if (result != null)
            {
                attempt.Location = result;
                return attempt;
            }
            attempt.FallbackError = error;

            logger?.Warning("GeoLocation: All providers failed, skipping location event");
            return attempt;
        }

        private static async Task<(GeoLocationResult result, string error)> TryIpInfo(AgentLogger logger)
        {
            try
            {
                logger?.Info("GeoLocation: Querying ipinfo.io...");

                using (var httpResponse = await SharedHttpClient.GetAsync("https://ipinfo.io/json"))
                {
                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        var error = $"HTTP {(int)httpResponse.StatusCode} ({httpResponse.ReasonPhrase})";
                        logger?.Warning($"GeoLocation: ipinfo.io failed: {error}");
                        return (null, error);
                    }

                    var response = await httpResponse.Content.ReadAsStringAsync();
                    var json = JObject.Parse(response);

                    var result = new GeoLocationResult
                    {
                        Country = json.Value<string>("country"),
                        Region = json.Value<string>("region"),
                        City = json.Value<string>("city"),
                        Loc = json.Value<string>("loc"),
                        Timezone = json.Value<string>("timezone"),
                        Ip = json.Value<string>("ip"),
                        Source = "ipinfo"
                    };

                    logger?.Info($"GeoLocation: ipinfo.io returned {result.City}, {result.Region}, {result.Country}");
                    return (result, null);
                }
            }
            catch (TaskCanceledException)
            {
                var error = "Timeout (5s)";
                logger?.Warning($"GeoLocation: ipinfo.io failed: {error}");
                return (null, error);
            }
            catch (Exception ex)
            {
                var error = ex.Message;
                logger?.Warning($"GeoLocation: ipinfo.io failed: {error}");
                return (null, error);
            }
        }

        private static async Task<(GeoLocationResult result, string error)> TryIfConfigCo(AgentLogger logger)
        {
            try
            {
                logger?.Info("GeoLocation: Falling back to ifconfig.co...");

                using (var httpResponse = await SharedHttpClient.GetAsync("https://ifconfig.co/json"))
                {
                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        var error = $"HTTP {(int)httpResponse.StatusCode} ({httpResponse.ReasonPhrase})";
                        logger?.Warning($"GeoLocation: ifconfig.co failed: {error}");
                        return (null, error);
                    }

                    var response = await httpResponse.Content.ReadAsStringAsync();
                    var json = JObject.Parse(response);

                    var latitude = json.Value<string>("latitude") ?? "";
                    var longitude = json.Value<string>("longitude") ?? "";
                    var loc = !string.IsNullOrEmpty(latitude) && !string.IsNullOrEmpty(longitude)
                        ? $"{latitude},{longitude}"
                        : "";

                    var result = new GeoLocationResult
                    {
                        Country = json.Value<string>("country_iso"),
                        Region = json.Value<string>("region_name"),
                        City = json.Value<string>("city"),
                        Loc = loc,
                        Timezone = json.Value<string>("time_zone"),
                        Ip = json.Value<string>("ip"),
                        Source = "ifconfig.co"
                    };

                    logger?.Info($"GeoLocation: ifconfig.co returned {result.City}, {result.Region}, {result.Country}");
                    return (result, null);
                }
            }
            catch (TaskCanceledException)
            {
                var error = "Timeout (5s)";
                logger?.Warning($"GeoLocation: ifconfig.co failed: {error}");
                return (null, error);
            }
            catch (Exception ex)
            {
                var error = ex.Message;
                logger?.Warning($"GeoLocation: ifconfig.co failed: {error}");
                return (null, error);
            }
        }
    }
}
