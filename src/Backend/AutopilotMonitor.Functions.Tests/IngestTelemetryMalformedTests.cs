using System.IO;
using System.Text;
using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Adversarial / malformed-body coverage for the ingest parse entrypoint. These exercise the same
/// seam the existing malformed-JSON test uses — <see cref="IngestTelemetryFunction.ReadBodyWithSizeCapAsync"/>
/// — because <c>Run()</c> feeds the raw (already gzip-decompressed) request stream straight into it
/// (Run L142). Anything this helper throws as a <c>JsonException</c> is caught at Run L144-148 and
/// mapped to a controlled <c>400 "Malformed JSON body"</c>; nothing escapes as an unhandled crash.
///
/// <para>Body construction mirrors <see cref="IngestTelemetryFunctionTests"/> verbatim
/// (<c>MemoryStream</c> over raw bytes → <c>ReadBodyWithSizeCapAsync(stream, maxBytes)</c>).</para>
/// </summary>
public class IngestTelemetryMalformedTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    // ============================================================ Deeply-nested JSON (JSON-bomb)

    [Theory]
    [InlineData("array")]
    [InlineData("object")]
    public async Task DeeplyNestedJson_is_a_controlled_JsonException_not_a_crash(string shape)
    {
        // 6000 levels of nesting — a JSON-bomb-style payload. Newtonsoft 13.x enforces a default
        // reader MaxDepth of 64, so the parse fails fast with a JsonReaderException (a JsonException
        // subclass) LONG before the nesting can exhaust the stack. Run() catches this at L144 and
        // returns a controlled 400 "Malformed JSON body". CURRENT BEHAVIOR — this is safe by design
        // (bounded depth), NOT a hardening gap.
        const int depth = 6000;
        var json = shape == "array"
            ? new string('[', depth) + new string(']', depth)
            : "[" + string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + new string('}', depth) + "]";

        var payload = Encoding.UTF8.GetBytes(json);
        using var stream = new MemoryStream(payload);

        await Assert.ThrowsAnyAsync<Newtonsoft.Json.JsonException>(
            () => IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 10 * 1024 * 1024));
    }

    // ============================================================ Non-UTF8 / invalid bytes

    [Fact]
    public async Task InvalidUtf8Bytes_decode_to_replacement_chars_and_surface_as_JsonException()
    {
        // Lone UTF-8 continuation bytes (0x80-0xBF) are not valid UTF-8 and are not a byte-order
        // mark. The StreamReader's default replacement fallback turns them into U+FFFD (it never
        // throws on decode), and the resulting text is not valid JSON → controlled JsonException,
        // no crash. CURRENT BEHAVIOR.
        var payload = new byte[] { 0x80, 0x81, 0x82, 0x83, 0x84, 0x85 };
        using var stream = new MemoryStream(payload);

        await Assert.ThrowsAnyAsync<Newtonsoft.Json.JsonException>(
            () => IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 1024));
    }

    [Fact]
    public async Task Utf8BomPrefixedValidJson_is_stripped_and_parsed()
    {
        // A legitimate UTF-8 BOM (EF BB BF) in front of a valid empty batch: the StreamReader
        // detects and strips the BOM, so the parse succeeds and yields an empty list (which Run
        // then rejects downstream at L160 with 400 "No telemetry items provided"). Proves the UTF8
        // decode path handles a real-world byte-order mark without corruption.
        var payload = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("[]"))
            .ToArray();
        using var stream = new MemoryStream(payload);

        var (exceeded, items) = await IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 1024);

        Assert.False(exceeded);
        Assert.NotNull(items);
        Assert.Empty(items!);
    }

    [Fact]
    public async Task MultiByteUtf8_string_values_round_trip_through_the_parser()
    {
        // Contrast to the invalid-byte case: well-formed multi-byte UTF-8 (accents + emoji) inside a
        // string value must decode losslessly, confirming the StreamReader(UTF-8) path is correct
        // for legitimate non-ASCII payloads.
        var json = "[{\"Kind\":\"Signal\",\"PayloadJson\":\"café-\\u00e9-🚀\"}]";
        var payload = Encoding.UTF8.GetBytes(json);
        using var stream = new MemoryStream(payload);

        var (exceeded, items) = await IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 1024);

        Assert.False(exceeded);
        var item = Assert.Single(items!);
        Assert.Equal("Signal", item.Kind);
        Assert.Equal("café-é-🚀", item.PayloadJson);
    }

    // ============================================================ Content-Type is not inspected

    [Fact]
    public async Task WrongContentType_ValidJsonBytes_still_parse_because_seam_reads_raw_stream()
    {
        // ReadBodyWithSizeCapAsync (and Run() around it) never inspects Content-Type — it reads the
        // raw decompressed byte stream. So a body sent with, e.g., text/plain but carrying valid
        // JSON bytes parses exactly as an application/json body would. CURRENT BEHAVIOR: parse is
        // content-type-agnostic.
        var payload = Encoding.UTF8.GetBytes("[{\"Kind\":\"Event\"}]");
        using var stream = new MemoryStream(payload);

        var (exceeded, items) = await IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 1024);

        Assert.False(exceeded);
        var item = Assert.Single(items!);
        Assert.Equal("Event", item.Kind);
    }

    // ============================================================ Adversarial-but-valid shapes

    [Theory]
    [InlineData("[]")]                 // empty array → empty list (Run → 400 "No telemetry items")
    [InlineData("[]   \n\t  ")]        // trailing whitespace is ignored by the parser
    public async Task EmptyArray_yields_empty_list_not_null(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        using var stream = new MemoryStream(payload);

        var (exceeded, items) = await IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 1024);

        Assert.False(exceeded);
        Assert.NotNull(items);
        Assert.Empty(items!);
    }

    [Theory]
    [InlineData("{\"Kind\":\"Event\"}")]        // top-level object, not an array
    [InlineData("\"just-a-string\"")]           // top-level primitive
    [InlineData("[{\"Kind\":\"Event\"},")]      // truncated array
    [InlineData("[{\"Kind\":}]")]               // missing value
    public async Task StructurallyBrokenOrWrongRootJson_throws_JsonException(string json)
    {
        // Each is caught at Run L144 and mapped to 400 "Malformed JSON body". A top-level object or
        // primitive cannot bind to List<TelemetryItemDto> and surfaces as a JsonException too.
        var payload = Encoding.UTF8.GetBytes(json);
        using var stream = new MemoryStream(payload);

        await Assert.ThrowsAnyAsync<Newtonsoft.Json.JsonException>(
            () => IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 1024));
    }
}
