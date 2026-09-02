using AutopilotMonitor.Shared.Delegation;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Round-trip + tamper + replay tests for the self-service delegation invitation ticket. The ticket only
/// LOCATES the invitation row (home tenant + invitation id); the row's status/ETag is the one-shot authority,
/// and the accepting tenant is always the caller's JWT tenant. Signing key injected process-wide by TestSetup.
/// </summary>
public class DelegationInviteTicketTests
{
    private const string Home = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string InvitationId = "0f3c2a9b1d4e4f5a8b6c7d8e9f0a1b2c";
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    static DelegationInviteTicketTests()
    {
        DelegationInviteTicket.SetSigningKeyForTesting(Convert.FromBase64String("dGVzdC1zaWduaW5nLWtleS0zMi1ieXRlcy1sb25nISEhISE="));
    }

    [Fact]
    public void RoundTrip_ValidTicket_DecodesToSameValues_Lowercased()
    {
        var token = DelegationInviteTicket.Encode(Home.ToUpperInvariant(), InvitationId, Now);

        var ok = DelegationInviteTicket.TryDecode(token, out var home, out var iid, out var reason, Now.AddDays(1));

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal(Home, home);
        Assert.Equal(InvitationId, iid);
    }

    [Fact]
    public void Tampered_Payload_Rejected()
    {
        var token = DelegationInviteTicket.Encode(Home, InvitationId, Now);
        var chars = token.ToCharArray();
        var mid = chars.Length / 2;
        chars[mid] = chars[mid] == 'A' ? 'B' : 'A';

        var ok = DelegationInviteTicket.TryDecode(new string(chars), out _, out _, out var reason, Now);

        Assert.False(ok);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Expired_AfterTtl_Rejected_ButValidJustBefore()
    {
        var token = DelegationInviteTicket.Encode(Home, InvitationId, Now);

        Assert.True(DelegationInviteTicket.TryDecode(token, out _, out _, out _, Now.Add(DelegationInviteTicket.DefaultTtl)));
        var ok = DelegationInviteTicket.TryDecode(token, out _, out _, out var reason, Now.Add(DelegationInviteTicket.DefaultTtl).AddSeconds(1));
        Assert.False(ok);
        Assert.Equal("expired", reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64!!")]
    [InlineData("eyJ4IjoxfQ")] // {"x":1} — well-formed base64url, wrong shape
    public void Garbage_Rejected(string raw)
    {
        Assert.False(DelegationInviteTicket.TryDecode(raw, out _, out _, out var reason, Now));
        Assert.NotNull(reason);
    }

    [Fact]
    public void DiagnosticsTicket_IsNotAnInvitation_DomainSeparation()
    {
        // Same signing key, different purpose + shape: a download ticket must never redeem as an invitation.
        var diag = AutopilotMonitor.Shared.Diagnostics.DiagnosticsDownloadTicket.Encode(Home, "blob.zip", "Hosted", Now);
        Assert.False(DelegationInviteTicket.TryDecode(diag, out _, out _, out var reason, Now));
        Assert.NotNull(reason);
    }

    [Fact]
    public void Encode_RequiresBothIds()
    {
        Assert.Throws<ArgumentException>(() => DelegationInviteTicket.Encode("", InvitationId));
        Assert.Throws<ArgumentException>(() => DelegationInviteTicket.Encode(Home, " "));
    }
}
