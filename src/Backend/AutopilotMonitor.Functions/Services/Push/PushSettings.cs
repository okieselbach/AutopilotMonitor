using AutopilotMonitor.Push;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Push;

/// <summary>
/// The push channel's configuration, read once at startup (App Settings use <c>Push__Key</c>):
/// <c>Push:Vapid:ActiveKey</c> (the key new pairings subscribe with; generated once, backed up in
/// the operator's secrets file, never rotated without a re-pair campaign),
/// <c>Push:Vapid:RetiredKeys</c> (optional, <c>;</c>-separated: keys devices paired earlier still
/// reference by kid), <c>Push:Vapid:Subject</c> (VAPID contact, <c>mailto:</c> or <c>https://</c>;
/// defaults to the product website so no personal address reaches the push services),
/// <c>Push:OwnerInactivityDays</c> (devices pause when their owner stopped signing in; 30),
/// <c>Push:PausedDeviceRetentionDays</c> (paused devices are deleted after; 90),
/// <c>Push:StaleDeviceRetentionDays</c> (devices whose endpoint is gone are deleted after; 14).
/// Without a valid active key the channel is off: pairing refuses, sends are logged no-ops.
/// </summary>
public sealed class PushSettings
{
    public const string ActiveKeyConfigKey = "Push:Vapid:ActiveKey";
    public const string RetiredKeysConfigKey = "Push:Vapid:RetiredKeys";
    public const string SubjectConfigKey = "Push:Vapid:Subject";
    public const string OwnerInactivityDaysConfigKey = "Push:OwnerInactivityDays";
    public const string PausedDeviceRetentionDaysConfigKey = "Push:PausedDeviceRetentionDays";
    public const string StaleDeviceRetentionDaysConfigKey = "Push:StaleDeviceRetentionDays";

    public bool IsConfigured => Keys != null;
    public VapidKeyRing? Keys { get; private init; }
    public string Subject { get; private init; } = Constants.WebsiteBaseUrl;
    public int OwnerInactivityDays { get; private init; } = 30;
    public int PausedDeviceRetentionDays { get; private init; } = 90;
    public int StaleDeviceRetentionDays { get; private init; } = 14;
    /// <summary>Why the channel is off (startup warning text); null when configured.</summary>
    public string? ConfigurationError { get; private init; }

    public static PushSettings Disabled(string reason) => new() { ConfigurationError = reason };

    public static PushSettings Load(IConfiguration configuration, ILogger logger)
    {
        var active = configuration[ActiveKeyConfigKey];
        if (string.IsNullOrWhiteSpace(active))
        {
            logger.LogWarning("{Key} is not configured — the Web Push channel is off (pairing refuses, sends are skipped)", ActiveKeyConfigKey);
            return Disabled($"{ActiveKeyConfigKey} is not configured.");
        }

        VapidKeyRing keys;
        try
        {
            keys = VapidKeyRing.FromSettings(active, configuration[RetiredKeysConfigKey]);
        }
        catch (ArgumentException ex)
        {
            // The message never echoes key material (library contract).
            logger.LogError("{Key} is invalid — the Web Push channel is off: {Reason}", ActiveKeyConfigKey, ex.Message);
            return Disabled($"{ActiveKeyConfigKey} is invalid: {ex.Message}");
        }

        var subject = configuration[SubjectConfigKey];
        if (string.IsNullOrWhiteSpace(subject))
            subject = Constants.WebsiteBaseUrl;

        return new PushSettings
        {
            Keys = keys,
            Subject = subject.Trim(),
            OwnerInactivityDays = ReadDays(configuration, OwnerInactivityDaysConfigKey, 30),
            PausedDeviceRetentionDays = ReadDays(configuration, PausedDeviceRetentionDaysConfigKey, 90),
            StaleDeviceRetentionDays = ReadDays(configuration, StaleDeviceRetentionDaysConfigKey, 14),
        };
    }

    /// <summary>Test seam: a ring built in code.</summary>
    internal static PushSettings ForKeys(VapidKeyRing keys, string? subject = null, int ownerInactivityDays = 30)
        => new() { Keys = keys, Subject = subject ?? Constants.WebsiteBaseUrl, OwnerInactivityDays = ownerInactivityDays };

    private static int ReadDays(IConfiguration configuration, string key, int fallback)
        => int.TryParse(configuration[key], out var days) && days >= 1 ? days : fallback;
}
