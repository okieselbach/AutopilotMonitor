using AutopilotMonitor.Functions.Functions.Progress;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the Progress Portal's serial-knowledge authorization model: the policy-catalog
/// registration (no tenant-wide list route; lookup resolves the role flags), the role-based
/// lookup matching (<see cref="ProgressPortalFunction.FindBestMatch"/>), and the serial proof
/// both the events route and the SignalR session-group join share (<see cref="SerialKnowledgeProof"/>).
/// </summary>
public class ProgressPortalFunctionTests
{
    private const string Serial = "PF3XKQ7";
    private const string Device = "AP-LAB-042";

    private static SessionSummary Session(
        string serial = Serial, string device = Device, int ageMinutes = 0, string? sessionId = null)
        => new()
        {
            SessionId = sessionId ?? Guid.NewGuid().ToString(),
            TenantId = "11111111-1111-1111-1111-111111111111",
            SerialNumber = serial,
            DeviceName = device,
            StartedAt = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-ageMinutes),
        };

    // ── Policy catalog ───────────────────────────────────────────────────────

    [Fact]
    public void TenantWide_list_route_is_gone_from_the_catalog()
    {
        // THE FINDING: GET progress/sessions shipped the tenant's latest 100 sessions to any
        // authenticated (roleless) user, with the serial filter applied only in the browser.
        // The route must stay unregistered — the catalog fail-closes unknown routes to 403.
        Assert.Null(EndpointAccessPolicyCatalog.FindPolicy("GET", "progress/sessions"));
    }

    [Fact]
    public void Lookup_route_is_AuthenticatedUserWithRole_with_QueryParam_scoping()
    {
        // AuthenticatedUserWithRole, NOT MemberRead (the portal's audience is roleless end
        // users — regression history c4dabeee) and NOT plain AuthenticatedUser (the function
        // needs the resolved role flags to grant members substring search).
        var entry = EndpointAccessPolicyCatalog.FindPolicy("GET", "progress/sessions/lookup");

        Assert.NotNull(entry);
        Assert.Equal(EndpointPolicy.AuthenticatedUserWithRole, entry!.Policy);
        Assert.Equal(TenantScoping.QueryParam, entry.TenantScoping);
    }

    [Fact]
    public void Events_route_stays_AuthenticatedUser_with_QueryParam_scoping()
    {
        // The serial proof is enforced in-function; the tier must remain reachable for
        // roleless end users.
        var entry = EndpointAccessPolicyCatalog.FindPolicy(
            "GET", "progress/sessions/22222222-2222-2222-2222-222222222222/events");

        Assert.NotNull(entry);
        Assert.Equal(EndpointPolicy.AuthenticatedUser, entry!.Policy);
        Assert.Equal(TenantScoping.QueryParam, entry.TenantScoping);
    }

    // ── FindBestMatch: roleless callers need the EXACT serial / device name ──

    [Theory]
    [InlineData(Serial)]
    [InlineData("pf3xkq7")]        // case-insensitive
    [InlineData("  PF3XKQ7  ")]    // trimmed
    [InlineData(Device)]
    [InlineData("ap-lab-042")]
    public void Roleless_exact_serial_or_device_name_matches(string search)
    {
        var match = ProgressPortalFunction.FindBestMatch(new[] { Session() }, search, allowSubstring: false);

        Assert.NotNull(match);
    }

    [Theory]
    [InlineData("PF3")]            // serial prefix
    [InlineData("3XKQ")]           // serial fragment
    [InlineData("AP-LAB")]         // device-name prefix
    [InlineData("P")]              // one-character fishing
    public void Roleless_substring_does_NOT_match(string search)
    {
        // The exact serial IS the authorization proof. Substring matching would degrade it to
        // "knows any one character" and let a roleless user fish through the tenant.
        var match = ProgressPortalFunction.FindBestMatch(new[] { Session() }, search, allowSubstring: false);

        Assert.Null(match);
    }

    [Theory]
    [InlineData("PF3")]
    [InlineData("AP-LAB")]
    [InlineData("lab-042")]
    public void Member_substring_still_matches(string search)
    {
        // Members/GA keep the portal's original fuzzy search — for them the same data is
        // MemberRead at REST anyway (regression guard for the helpdesk workflow).
        var match = ProgressPortalFunction.FindBestMatch(new[] { Session() }, search, allowSubstring: true);

        Assert.NotNull(match);
    }

    [Fact]
    public void Newest_matching_session_wins()
    {
        var older = Session(ageMinutes: 120, sessionId: "older");
        var newest = Session(ageMinutes: 0, sessionId: "newest");
        var unrelated = Session(serial: "OTHER01", device: "OTHER-DEV", ageMinutes: 1, sessionId: "unrelated");

        var match = ProgressPortalFunction.FindBestMatch(
            new[] { older, unrelated, newest }, Serial, allowSubstring: false);

        Assert.Equal("newest", match!.SessionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NO-SUCH-DEVICE")]
    public void No_match_returns_null(string search)
    {
        Assert.Null(ProgressPortalFunction.FindBestMatch(new[] { Session() }, search, allowSubstring: false));
        Assert.Null(ProgressPortalFunction.FindBestMatch(new[] { Session() }, search, allowSubstring: true));
    }

    // ── SerialKnowledgeProof: the shared events/SignalR-join comparison ──────

    [Theory]
    [InlineData(Serial, Serial)]
    [InlineData(Serial, "pf3xkq7")]      // case-insensitive
    [InlineData(Serial, " PF3XKQ7 ")]    // trimmed
    [InlineData(" PF3XKQ7 ", Serial)]    // stored value trimmed too
    public void SerialProof_matches_exact_serial(string stored, string provided)
    {
        Assert.True(SerialKnowledgeProof.Matches(stored, provided));
    }

    [Theory]
    [InlineData(Serial, "PF3XKQ")]       // near-miss
    [InlineData(Serial, "PF3")]          // prefix is NOT proof
    [InlineData(Serial, null)]
    [InlineData(Serial, "")]
    [InlineData(Serial, "   ")]
    [InlineData(null, Serial)]           // session without a serial is unreachable (fail-closed)
    [InlineData("", Serial)]
    [InlineData(null, null)]
    public void SerialProof_rejects_everything_else(string? stored, string? provided)
    {
        Assert.False(SerialKnowledgeProof.Matches(stored, provided));
    }
}
