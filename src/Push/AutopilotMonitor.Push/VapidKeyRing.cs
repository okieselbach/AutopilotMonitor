namespace AutopilotMonitor.Push;

/// <summary>
/// The active VAPID key plus the retired keys that older subscriptions were created with.
/// A subscription is signed with the key it was created with (RFC 8292 section 4.2), so a
/// rotation keeps the old key here until no subscription names its <see cref="VapidKey.Kid"/>.
/// </summary>
public sealed class VapidKeyRing : IDisposable
{
    private const char RetiredSeparator = ';';

    /// <summary>Creates a ring; the ring owns the keys and disposes them.</summary>
    public VapidKeyRing(VapidKey active, IReadOnlyList<VapidKey>? retired = null)
    {
        ArgumentNullException.ThrowIfNull(active);
        Active = active;
        Retired = retired ?? Array.Empty<VapidKey>();
    }

    /// <summary>The key new subscriptions are created with.</summary>
    public VapidKey Active { get; }

    /// <summary>Keys that are no longer handed out but still sign for the subscriptions created with them.</summary>
    public IReadOnlyList<VapidKey> Retired { get; }

    /// <summary>
    /// Parses the app-setting form: the active key, and optionally retired keys separated by
    /// semicolons. Throws <see cref="ArgumentException"/> on malformed input; the message never
    /// contains key material.
    /// </summary>
    public static VapidKeyRing FromSettings(string? activeBase64Url, string? retiredSemicolonSeparated = null)
    {
        if (string.IsNullOrWhiteSpace(activeBase64Url))
        {
            throw new ArgumentException("The active VAPID key is not set.", nameof(activeBase64Url));
        }

        VapidKey active;
        try
        {
            active = VapidKey.Parse(activeBase64Url.Trim());
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"The active VAPID key is invalid: {ex.Message}", nameof(activeBase64Url));
        }

        var retired = new List<VapidKey>();
        try
        {
            var entries = (retiredSemicolonSeparated ?? string.Empty)
                .Split(RetiredSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < entries.Length; i++)
            {
                try
                {
                    retired.Add(VapidKey.Parse(entries[i]));
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException($"Retired VAPID key #{i + 1} is invalid: {ex.Message}", nameof(retiredSemicolonSeparated));
                }
            }
        }
        catch
        {
            active.Dispose();
            foreach (var key in retired)
            {
                key.Dispose();
            }

            throw;
        }

        return new VapidKeyRing(active, retired);
    }

    /// <summary>Finds the key with the given identifier, the active key first; null when no key matches.</summary>
    public VapidKey? Resolve(string? kid)
    {
        if (string.IsNullOrEmpty(kid))
        {
            return null;
        }

        if (string.Equals(Active.Kid, kid, StringComparison.Ordinal))
        {
            return Active;
        }

        foreach (var key in Retired)
        {
            if (string.Equals(key.Kid, kid, StringComparison.Ordinal))
            {
                return key;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Active.Dispose();
        foreach (var key in Retired)
        {
            key.Dispose();
        }
    }
}
