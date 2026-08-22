using AutopilotMonitor.Functions.Services.Notifications;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// HMAC request-signature scheme for generic JSON webhooks: known-vector stability (the
/// scheme is a published contract receivers implement against), determinism, and
/// secret/message sensitivity.
/// </summary>
public class WebhookSignatureTests
{
    [Fact]
    public void ComputeSignature_MatchesKnownVector()
    {
        // Independently computed: HMACSHA256("whsec_test_0123456789abcdef", "1700000000.{"a":1}")
        var signature = WebhookSignatureCalculator.ComputeSignature(
            "whsec_test_0123456789abcdef", "1700000000", "{\"a\":1}");

        Assert.Equal("sha256=07728d7e7b1736bac3de36a46c9bc0b660d59aa4a6f251544124fede1dc42a6c", signature);
    }

    [Fact]
    public void ComputeSignature_IsDeterministic_AndSensitiveToEveryInput()
    {
        var baseline = WebhookSignatureCalculator.ComputeSignature("secret-0123456789", "1700000000", "{}");

        Assert.Equal(baseline, WebhookSignatureCalculator.ComputeSignature("secret-0123456789", "1700000000", "{}"));
        Assert.NotEqual(baseline, WebhookSignatureCalculator.ComputeSignature("secret-0123456789X", "1700000000", "{}"));
        Assert.NotEqual(baseline, WebhookSignatureCalculator.ComputeSignature("secret-0123456789", "1700000001", "{}"));
        Assert.NotEqual(baseline, WebhookSignatureCalculator.ComputeSignature("secret-0123456789", "1700000000", "{ }"));
    }

    [Fact]
    public void HeaderNames_AreTheDocumentedContract()
    {
        Assert.Equal("X-AutopilotMonitor-Timestamp", WebhookSignatureCalculator.TimestampHeader);
        Assert.Equal("X-AutopilotMonitor-Signature", WebhookSignatureCalculator.SignatureHeader);
    }
}
