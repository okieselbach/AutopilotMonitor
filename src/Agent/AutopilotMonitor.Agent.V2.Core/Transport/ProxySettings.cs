using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Agent.V2.Core.Transport
{
    /// <summary>
    /// The proxy settings Windows holds for the account this process runs as (what
    /// <c>bitsadmin /util /getieproxy localsystem</c> shows for SYSTEM and what .NET uses), plus the
    /// WinHTTP proxy (<c>netsh winhttp show proxy</c>). Compared by content, because WinINET rewrites
    /// its settings blob without changing anything. The values end up in logs and events, so
    /// credentials and PAC query strings are removed on the way in.
    /// </summary>
    internal sealed class ProxySettings : IEquatable<ProxySettings>
    {
        public bool AutoDetect { get; }
        public string PacUrl { get; }
        public string Proxy { get; }
        public string Bypass { get; }
        public string WinHttpProxy { get; }

        /// <summary>Win32 error of a failed read; null when the settings were read.</summary>
        public int? ReadError { get; }

        public bool IsUnreadable => ReadError != null;

        public ProxySettings(bool autoDetect, string pacUrl, string proxy, string bypass, string winHttpProxy)
        {
            AutoDetect = autoDetect;
            PacUrl = SanitizeUrl(pacUrl);
            Proxy = StripCredentials(proxy);
            Bypass = EmptyToNull(bypass);
            WinHttpProxy = StripCredentials(winHttpProxy);
        }

        private ProxySettings(int readError)
        {
            ReadError = readError;
        }

        public static ProxySettings Unreadable(int win32Error) => new ProxySettings(win32Error);

        /// <summary>Kept in step with ANALYZE-CORR-003, which matches <c>Proxy|PAC</c>.</summary>
        public string ProxyType =>
            IsUnreadable ? "Unknown"
            : Proxy != null ? "Proxy"
            : PacUrl != null ? "PAC"
            : AutoDetect ? "AutoDetect"
            : "Direct";

        public string Describe()
        {
            if (IsUnreadable) return $"unreadable (Win32 error {ReadError})";

            var parts = new List<string>(3);
            if (AutoDetect) parts.Add("auto-detect");
            if (PacUrl != null) parts.Add($"PAC {PacUrl}");
            if (Proxy != null) parts.Add($"proxy {Proxy}");
            return parts.Count == 0 ? "no proxy" : string.Join(", ", parts);
        }

        public string DescribeWinHttp() => WinHttpProxy == null ? "direct" : $"proxy {WinHttpProxy}";

        public string DescribeAll() => $"IE settings of this account: {Describe()}; WinHTTP: {DescribeWinHttp()}";

        /// <summary>The account's own settings, which the agent's uploads follow; WinHTTP is apart.</summary>
        public bool DiffersForUploadsFrom(ProxySettings other) =>
            AutoDetect != other.AutoDetect
            || PacUrl != other.PacUrl
            || Proxy != other.Proxy
            || Bypass != other.Bypass;

        public string DescribeUploadChangeFrom(ProxySettings previous) =>
            Describe() == previous.Describe()
                ? "IE settings of this account: proxy bypass list changed"
                : $"IE settings of this account: {previous.Describe()} -> {Describe()}";

        public string DescribeChangeFrom(ProxySettings previous)
        {
            var parts = new List<string>(2);
            if (DiffersForUploadsFrom(previous)) parts.Add(DescribeUploadChangeFrom(previous));
            if (WinHttpProxy != previous.WinHttpProxy) parts.Add($"WinHTTP: {previous.DescribeWinHttp()} -> {DescribeWinHttp()}");
            return string.Join("; ", parts);
        }

        /// <summary>Payload of the <c>proxy_configuration</c> event.</summary>
        public Dictionary<string, object> ToEventData() => new Dictionary<string, object>
        {
            { "proxyType", ProxyType },
            { "autoDetect", AutoDetect },
            { "proxyEnabled", Proxy != null },
            { "proxyServer", Proxy },
            { "proxyOverride", Bypass },
            { "autoConfigUrl", PacUrl },
            { "winHttpProxyConfigured", WinHttpProxy != null },
            { "winHttpProxy", WinHttpProxy },
        };

        public bool Equals(ProxySettings other) =>
            other != null
            && ReadError == other.ReadError
            && !DiffersForUploadsFrom(other)
            && WinHttpProxy == other.WinHttpProxy;

        public override bool Equals(object obj) => Equals(obj as ProxySettings);

        public override int GetHashCode() => (AutoDetect, PacUrl, Proxy, Bypass, WinHttpProxy, ReadError).GetHashCode();

        private static string EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;

        /// <summary>Scheme, host, port and path. A query string can carry a key; an unparsable URL is not repeated.</summary>
        private static string SanitizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return "(unparsable URL)";
            var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
            return $"{uri.Scheme}://{uri.Host}{port}{uri.AbsolutePath}";
        }

        /// <summary>Handles <c>user:pass@host:8080</c>, also per scheme (<c>http=user:pass@host:8080;https=…</c>).</summary>
        private static string StripCredentials(string proxy)
        {
            if (string.IsNullOrEmpty(proxy)) return null;
            if (proxy.IndexOf('@') < 0) return proxy;

            var entries = proxy.Split(';');
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                var at = entry.LastIndexOf('@');
                if (at < 0) continue;

                var scheme = entry.IndexOf("://", StringComparison.Ordinal);
                var hostStart = scheme >= 0 ? scheme + 3 : entry.IndexOf('=') + 1;
                entries[i] = entry.Substring(0, hostStart) + entry.Substring(at + 1);
            }
            return string.Join(";", entries);
        }
    }
}
