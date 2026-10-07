using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Services.Notifications;

/// <summary>
/// Whose channels an alert is being sent through: the platform's own ops channels or one
/// tenant's channels. Webhook providers never needed this — their destination is in the
/// channel — but a Push channel has no destination of its own: its recipients are the paired
/// devices of the SCOPE, resolved at send time. Every caller of the dispatcher sets the scope
/// from trusted context (the envelope's tenant, the evaluated tenant config, the ops path);
/// it is never derived from the alert's facts, because an ops alert's "Tenant" fact names the
/// customer the event is ABOUT, not the channel owner.
/// </summary>
public readonly record struct NotificationScope
{
    /// <summary>PushDevices partition key: <see cref="Constants.Push.PlatformScope"/> or the lowercase tenant id.</summary>
    public string Key { get; }

    private NotificationScope(string key) => Key = key;

    public static NotificationScope Platform { get; } = new(Constants.Push.PlatformScope);

    public static NotificationScope Tenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("tenantId required", nameof(tenantId));
        return new NotificationScope(tenantId.Trim().ToLowerInvariant());
    }

    /// <summary>The scope a stored partition key names (device rows, grants, watch scopes).</summary>
    public static NotificationScope FromKey(string key)
        => key == Constants.Push.PlatformScope ? Platform : Tenant(key);

    public bool IsPlatform => Key == Constants.Push.PlatformScope;

    /// <summary>False for <c>default(NotificationScope)</c> — a caller that forgot the scope; the push branch refuses it.</summary>
    public bool IsSet => !string.IsNullOrEmpty(Key);

    public override string ToString() => IsSet ? Key : "(unset)";
}
