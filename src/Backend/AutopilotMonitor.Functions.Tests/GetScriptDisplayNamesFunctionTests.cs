using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Graph;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.GraphResolution;
using AutopilotMonitor.Shared.Models.Graph;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// POST tenants/{tenantId}/scripts/display-names: a lookup that ran into a time limit says so in
/// its answer, so the portal asks once more, and the request row tells a cut first lookup, a
/// follow-up and a follow-up that was cut again apart. A first lookup within its limits is the
/// normal case and carries neither the flag nor a marker.
/// </summary>
public class GetScriptDisplayNamesFunctionTests
{
    private const string TenantId = "00000000-0000-4000-8000-000000000002";
    private const string Ref = "Platform:1f9d3a52-4c1e-4b7a-9a01-52f3aaaa1001";

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, "timedOut")]
    [InlineData(false, true, "retry")]
    [InlineData(true, true, "retryTimedOut")]
    public async Task A_cut_lookup_is_flagged_in_the_answer_and_marked_on_the_request_row(
        bool timedOut, bool isFollowUp, string? expectedMarker)
    {
        var resolver = new Mock<IScriptDisplayNameResolver>();
        resolver
            .Setup(r => r.ResolveAsync(TenantId, It.IsAny<IReadOnlyCollection<ScriptRef>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyCollection<ScriptRef> refs, CancellationToken _) =>
                new ScriptDisplayNameLookup(refs.ToDictionary(r => r, _ => (string?)null), timedOut));
        var function = new GetScriptDisplayNamesFunction(NullLogger<GetScriptDisplayNamesFunction>.Instance, resolver.Object);
        var body = "{\"refs\":[\"" + Ref + "\"]" + (isFollowUp ? ",\"retry\":true" : "") + "}";
        var (req, context) = EndpointHarness.Request(TenantId, jsonBody: body);

        var response = await function.Run(req, TenantId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        response.Body.Position = 0;
        using var doc = JsonDocument.Parse(response.Body);
        Assert.True(doc.RootElement.GetProperty("refs").TryGetProperty(Ref, out _));
        // The key is left out, not false, when no time limit fired.
        Assert.Equal(timedOut, doc.RootElement.TryGetProperty("timedOut", out var flag));
        if (timedOut) Assert.True(flag.GetBoolean());

        if (expectedMarker == null)
            Assert.False(context.Items.ContainsKey(RequestRowMarkers.ScriptNameLookupKey));
        else
            Assert.Equal(expectedMarker, context.Items[RequestRowMarkers.ScriptNameLookupKey]);
    }
}
