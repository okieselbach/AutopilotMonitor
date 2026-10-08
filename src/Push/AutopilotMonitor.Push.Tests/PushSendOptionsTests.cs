using AutopilotMonitor.Push;

namespace AutopilotMonitor.Push.Tests;

public class PushSendOptionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ttl_must_be_positive(int seconds)
    {
        Assert.Throws<ArgumentException>(() => new PushSendOptions(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Ttl_seconds_round_up_and_never_reach_zero()
    {
        Assert.Equal(1, new PushSendOptions(TimeSpan.FromMilliseconds(1)).TtlSeconds);
        Assert.Equal(2, new PushSendOptions(TimeSpan.FromMilliseconds(1500)).TtlSeconds);
        Assert.Equal(14400, new PushSendOptions(TimeSpan.FromHours(4)).TtlSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123456789012345678901234567890123")]
    [InlineData("has+plus")]
    [InlineData("has/slash")]
    [InlineData("has=padding")]
    [InlineData("has space")]
    [InlineData("umlaut-ä")]
    public void Topic_must_be_1_to_32_base64url_characters(string topic)
    {
        Assert.Throws<ArgumentException>(() => new PushSendOptions(TimeSpan.FromMinutes(5), PushUrgency.Normal, topic));
    }

    [Fact]
    public void Topic_at_the_limit_is_accepted()
    {
        const string topic = "abcDEF0123456789-_abcDEF01234567";
        Assert.Equal(32, topic.Length);
        Assert.Equal(topic, new PushSendOptions(TimeSpan.FromMinutes(5), PushUrgency.High, topic).Topic);
        Assert.Null(new PushSendOptions(TimeSpan.FromMinutes(5)).Topic);
    }

    [Theory]
    [InlineData(PushUrgency.VeryLow, "very-low")]
    [InlineData(PushUrgency.Low, "low")]
    [InlineData(PushUrgency.Normal, "normal")]
    [InlineData(PushUrgency.High, "high")]
    public void Urgency_maps_to_the_RFC_8030_header_value(PushUrgency urgency, string expected)
    {
        Assert.Equal(expected, new PushSendOptions(TimeSpan.FromMinutes(5), urgency).UrgencyHeaderValue);
    }

    [Fact]
    public void Undefined_urgency_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new PushSendOptions(TimeSpan.FromMinutes(5), (PushUrgency)42));
    }
}
