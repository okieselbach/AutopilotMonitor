using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
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
