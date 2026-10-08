namespace AutopilotMonitor.Push;

/// <summary>
/// Decides which push resource URLs the relay will ever send to. Applied when a device is
/// paired and when it re-subscribes; the SSRF connect gate is the second, independent layer.
/// </summary>
public static class PushEndpointPolicy
{
    /// <summary>Longest accepted absolute URL.</summary>
    public const int MaxEndpointLength = 2048;

    /// <summary>
    /// Known push services. An entry starting with a dot matches any host below that domain
    /// (never the bare domain); an entry without a leading dot matches that host exactly.
    /// </summary>
    public static IReadOnlyList<string> AllowedHostSuffixes { get; } = new[]
    {
        ".push.apple.com",
        "fcm.googleapis.com",
        "updates.push.services.mozilla.com",
        ".notify.windows.com",
    };

    /// <summary>Returns null when the endpoint is acceptable, otherwise a generic reason that never echoes the URL.</summary>
    public static string? Validate(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri)
        {
            return "Endpoint must be an absolute URL.";
        }

        if (endpoint.AbsoluteUri.Length > MaxEndpointLength)
        {
            return "Endpoint is too long.";
        }

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "Endpoint must use https.";
        }

        if (endpoint.UserInfo.Length > 0)
        {
            return "Endpoint must not contain credentials.";
        }

        if (endpoint.HostNameType != UriHostNameType.Dns || endpoint.IsLoopback)
        {
            return "Endpoint host must be a public DNS name.";
        }

        return IsAllowedHost(endpoint.Host) ? null : "Endpoint is not a known push service.";
    }

    private static bool IsAllowedHost(string host)
    {
        foreach (var entry in AllowedHostSuffixes)
        {
            if (entry[0] == '.')
            {
                if (host.Length > entry.Length && host.EndsWith(entry, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(host, entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
