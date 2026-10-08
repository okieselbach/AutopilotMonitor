namespace AutopilotMonitor.Push;

/// <summary>RFC 8030 <c>Urgency</c> header values.</summary>
public enum PushUrgency
{
    /// <summary>Deliver on power and Wi-Fi only.</summary>
    VeryLow,

    /// <summary>Deliver on power or Wi-Fi.</summary>
    Low,

    /// <summary>Default delivery.</summary>
    Normal,

    /// <summary>Deliver immediately.</summary>
    High,
}

/// <summary>Per-message delivery options: TTL (mandatory and positive), urgency and an optional replacement topic.</summary>
public sealed record PushSendOptions
{
    /// <summary>Longest accepted topic (RFC 8030 section 5.4).</summary>
    public const int MaxTopicLength = 32;

    /// <summary>Validates and captures the options; throws <see cref="ArgumentException"/> on an invalid value.</summary>
    public PushSendOptions(TimeSpan ttl, PushUrgency urgency = PushUrgency.Normal, string? topic = null)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentException("The TTL must be positive; push services reject zero.", nameof(ttl));
        }

        if (!Enum.IsDefined(urgency))
        {
            throw new ArgumentException("Unknown urgency.", nameof(urgency));
        }

        if (topic is not null && !IsValidTopic(topic))
        {
            throw new ArgumentException($"The topic must be 1 to {MaxTopicLength} base64url characters.", nameof(topic));
        }

        Ttl = ttl;
        Urgency = urgency;
        Topic = topic;
    }

    /// <summary>How long the push service keeps the message for an offline device.</summary>
    public TimeSpan Ttl { get; }

    /// <summary>Delivery urgency.</summary>
    public PushUrgency Urgency { get; }

    /// <summary>Replacement topic: a later message with the same topic replaces an undelivered one.</summary>
    public string? Topic { get; }

    /// <summary>The TTL in whole seconds as sent on the wire, rounded up and never below 1.</summary>
    public long TtlSeconds => Math.Max(1, (long)Math.Ceiling(Ttl.TotalSeconds));

    /// <summary>The <c>Urgency</c> header value.</summary>
    public string UrgencyHeaderValue => Urgency switch
    {
        PushUrgency.VeryLow => "very-low",
        PushUrgency.Low => "low",
        PushUrgency.High => "high",
        _ => "normal",
    };

    private static bool IsValidTopic(string topic)
    {
        if (topic.Length is 0 or > MaxTopicLength)
        {
            return false;
        }

        foreach (var c in topic)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
            {
                return false;
            }
        }

        return true;
    }
}
