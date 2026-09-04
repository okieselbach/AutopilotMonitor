using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire-identity proof for the anonymous-object → typed-DTO migration: for every converted
/// call site there is a case here serializing the OLD anonymous literal (copied from the diff)
/// and the NEW DTO with the same values, comparing the JSON strings ordinally — key names,
/// key ORDER and presence/absence (WhenWritingNull) all must match, because MCP hands raw
/// response JSON to an LLM. Each site gets a populated case and, where a slot can be null at
/// runtime, a null-slot case (the key must vanish identically on both sides).
/// </summary>
public class ApiResponseWireParityTests
{
    private static readonly JsonSerializerOptions WireOptions = ApiJsonOptions.Create();

    /// <summary>
    /// Serialize both shapes with the production wire options and compare ordinally.
    /// The typed side serializes as its runtime type — serializing as IApiResponse would
    /// emit no properties at all.
    /// </summary>
    internal static void AssertWireIdentical(object anonymousLiteral, IApiResponse typed)
    {
        var expected = JsonSerializer.Serialize(anonymousLiteral, anonymousLiteral.GetType(), WireOptions);
        var actual = JsonSerializer.Serialize(typed, typed.GetType(), WireOptions);
        Assert.Equal(expected, actual);
    }

    // ---- Error envelope (2026-09, deliberate wire change) ----------------------------------
    // Non-2xx bodies used to be ~10 anonymous shapes ({ success = false, message }, { error },
    // { error, message }, { error, code }, …). They are ONE typed envelope now, so these pins fix
    // the NEW shape exactly rather than proving parity with any single legacy literal.

    [Fact]
    public void ApiErrorResponse_pins_the_minimal_envelope_shape()
    {
        var body = ApiErrorWriter.Build("cid-1", Constants.ApiErrorCodes.NotFound, "Session not found.");

        Assert.Equal(
            "{\"error\":\"Session not found.\",\"code\":\"NotFound\",\"correlationId\":\"cid-1\"}",
            JsonSerializer.Serialize(body, WireOptions));
    }

    [Fact]
    public void ApiErrorResponse_pins_the_full_envelope_shape_and_key_order()
    {
        var body = ApiErrorWriter.Build("cid-2", Constants.ApiErrorCodes.ServiceUnavailable,
            "Authorization service temporarily unavailable.", hint: "Retry.", retryAfterSeconds: 5, operation: "PolicyEnforcement");

        Assert.Equal(
            "{\"error\":\"Authorization service temporarily unavailable.\",\"code\":\"ServiceUnavailable\"," +
            "\"correlationId\":\"cid-2\",\"hint\":\"Retry.\",\"retryAfterSeconds\":5,\"operation\":\"PolicyEnforcement\"}",
            JsonSerializer.Serialize(body, WireOptions));
    }

    [Fact]
    public void SuccessMessageResponse_matches_the_canonical_anonymous_shape()
    {
        AssertWireIdentical(
            new { success = true, message = "Rule created" },
            new SuccessMessageResponse { Success = true, Message = "Rule created" });

        AssertWireIdentical(
            new { success = false, message = "Failed to create rule" },
            new SuccessMessageResponse { Success = false, Message = "Failed to create rule" });
    }

    [Fact]
    public void SuccessOnlyResponse_matches_the_canonical_anonymous_shape()
    {
        AssertWireIdentical(
            new { success = true },
            new SuccessOnlyResponse { Success = true });
    }
}
