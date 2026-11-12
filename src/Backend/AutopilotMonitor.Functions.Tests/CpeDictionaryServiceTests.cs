using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Vulnerability;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="CpeDictionaryService"/> reduces the NVD product dictionary feed to the application
/// products that can be Windows software and keeps that copy as a blob. The feed is read as a
/// stream through a small buffer, one product name at a time; what the buffer size is must not
/// show in the result. A refresh never throws and never replaces the stored copy with less than
/// a whole dictionary.
/// </summary>
public class CpeDictionaryServiceTests
{
    private const string FeedVersion = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    // -----------------------------------------------------------------------
    // Feed fixtures
    // -----------------------------------------------------------------------

    private static string Quote(string text) =>
        "\"" + JsonEncodedText.Encode(text, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";

    /// <summary>One entry of a chunk's products array, laid out like the feed's own pretty-printed files.</summary>
    private static string Entry(string cpeName, bool deprecated, int refs, IEnumerable<(string Lang, string Title)> titles)
    {
        var json = new StringBuilder();
        json.Append("{\r\n      \"cpe\" :\t{\n");
        json.Append("        \"deprecated\": ").Append(deprecated ? "true" : "false").Append(",\n");
        json.Append("        \"cpeName\": ").Append(Quote(cpeName)).Append(",\n");
        json.Append("        \"cpeNameId\": \"00000000-0000-4000-8000-000000000000\",\n");
        json.Append("        \"lastModified\": \"2026-10-01T12:00:00.000\",\n");
        json.Append("        \"titles\": [ ");
        json.Append(string.Join(", ", titles.Select(t => $"{{ \"title\": {Quote(t.Title)}, \"lang\": {Quote(t.Lang)} }}")));
        json.Append(" ],\n        \"refs\": [");
        json.Append(string.Join(",", Enumerable.Range(1, refs).Select(n =>
            $"\n          {{ \"ref\": \"https://www.contoso.example/advisories/{n}\", \"type\": \"Vendor\" }}")));
        json.Append("\n        ]\n      }\n    }");
        return json.ToString();
    }

    private static string Name(string cpeName, params (string Lang, string Title)[] titles) => Entry(cpeName, false, 1, titles);

    private static string Withdrawn(string cpeName, params (string Lang, string Title)[] titles) => Entry(cpeName, true, 1, titles);

    /// <summary>A chunk file: the feed's root object with <paramref name="entries"/> as its products.</summary>
    private static byte[] Chunk(params string[] entries) => Encoding.UTF8.GetBytes(
        "{\n  \"resultsPerPage\": " + entries.Length.ToString(CultureInfo.InvariantCulture)
        + ",\n  \"startIndex\": 0,\n  \"format\": \"NVD_CPE\",\n  \"version\": \"2.0\",\n  \"timestamp\": \"2026-11-12T03:00:00.000\",\n  \"products\": [\n    "
        + string.Join(",\n    ", entries)
        + "\n  ]\n}\n");

    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    private static async Task<Dictionary<string, CpeDictionaryService.ProductState>> ReadAsync(Stream chunk, int? bufferBytes = null)
    {
        var reduced = new Dictionary<string, CpeDictionaryService.ProductState>(StringComparer.Ordinal);
        if (bufferBytes is { } size)
            await CpeDictionaryService.ReadChunkAsync(chunk, reduced, CancellationToken.None, size);
        else
            await CpeDictionaryService.ReadChunkAsync(chunk, reduced, CancellationToken.None);
        return reduced;
    }

    private static Task<Dictionary<string, CpeDictionaryService.ProductState>> ReadAsync(byte[] chunk, int? bufferBytes = null) =>
        ReadAsync(new MemoryStream(chunk), bufferBytes);

    /// <summary>What a read left per product, in key order, so two reads compare value by value.</summary>
    private static List<(string Product, string Title, bool WindowsCapable)> StateOf(
        Dictionary<string, CpeDictionaryService.ProductState> reduced) => reduced
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => (pair.Key, pair.Value.Title, pair.Value.WindowsCapable))
        .ToList();

    /// <summary>A chunk's bytes as a stream that tells how it was read, and can hand out a few bytes per read like a network stream.</summary>
    private sealed class ProbeStream : MemoryStream
    {
        private readonly int _mostPerRead;

        public ProbeStream(byte[] content, int mostPerRead = int.MaxValue) : base(content, writable: false) => _mostPerRead = mostPerRead;

        public int Reads { get; private set; }

        /// <summary>Room the reader offered on its first read: its whole buffer, still empty.</summary>
        public int FirstCapacity { get; private set; }

        public int LargestCapacity { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Reads++ == 0) FirstCapacity = buffer.Length;
            LargestCapacity = Math.Max(LargestCapacity, buffer.Length);
            return base.ReadAsync(buffer.Length > _mostPerRead ? buffer[.._mostPerRead] : buffer, cancellationToken);
        }
    }

    // -----------------------------------------------------------------------
    // ReadChunkAsync: what is kept
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReadChunk_KeepsApplicationProductsThatAreNotWithdrawn()
    {
        var chunk = Chunk(
            Name("cpe:2.3:a:contoso:widget:4.2:*:*:*:*:*:*:*", ("en", "Contoso Widget 4.2")),
            Name("cpe:2.3:o:contoso:widget_os:1.0:*:*:*:*:*:*:*", ("en", "Contoso Widget OS 1.0")),
            Name("cpe:2.3:h:contoso:widget_box:-:*:*:*:*:*:*:*", ("en", "Contoso Widget Box")),
            Withdrawn("cpe:2.3:a:contoso:retired:1.0:*:*:*:*:*:*:*", ("en", "Contoso Retired 1.0")),
            Name("cpe:2.3:a:contoso:*:1.0:*:*:*:*:*:*:*", ("en", "Contoso Anything")),
            Name(@"cpe:2.3:a:fabrikam:gadget\+\+:7.0:*:*:*:*:*:*:*", ("en", "Fabrikam Gadget++ 7.0")));

        var reduced = await ReadAsync(chunk);

        Assert.Equal(
            new[] { ("contoso:widget", "Contoso Widget 4.2", true), (@"fabrikam:gadget\+\+", "Fabrikam Gadget++ 7.0", true) },
            StateOf(reduced));
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("-", true)]
    [InlineData("windows", true)]
    // NVD names a release for some Windows software instead of the platform.
    [InlineData("windows_10", true)]
    [InlineData("windows_7", true)]
    // The phone, embedded and ARM-only lines share the prefix and are other platforms.
    [InlineData("windows_phone", false)]
    [InlineData("windows_mobile", false)]
    [InlineData("windows_ce", false)]
    [InlineData("windows_rt", false)]
    [InlineData("wordpress", false)]
    [InlineData("android", false)]
    [InlineData("node.js", false)]
    [InlineData("iphone_os", false)]
    [InlineData("macos", false)]
    public async Task ReadChunk_ProductCanBeWindowsSoftware_WhenANameTargetsWindowsOrNoPlatform(string targetSw, bool expected)
    {
        var reduced = await ReadAsync(Chunk(Name($"cpe:2.3:a:contoso:widget:4.2:*:*:*:*:{targetSw}:*:*", ("en", "Contoso Widget 4.2"))));

        Assert.Equal(expected, reduced["contoso:widget"].WindowsCapable);
    }

    [Fact]
    public async Task ReadChunk_WindowsLinesThatAreOtherPlatforms_AreOtherPlatformsForAScanToo()
    {
        foreach (var targetSw in new[] { "windows_phone", "windows_mobile", "windows_ce", "windows_rt" })
        {
            var name = $"cpe:2.3:a:contoso:widget:4.2:*:*:*:*:{targetSw}:*:*";

            var reduced = await ReadAsync(Chunk(Name(name, ("en", "Contoso Widget 4.2"))));

            Assert.False(reduced["contoso:widget"].WindowsCapable);
            // A product the dictionary keeps out for its platform must not have that platform's
            // CVE ranges compared with a desktop install either.
            Assert.True(CveVersionFilter.IsNonWindowsPlatform(name), $"A scan judges a range for {targetSw}.");
        }
    }

    [Fact]
    public async Task ReadChunk_OneNameThatCanBeWindowsSoftware_IsEnoughForTheProduct()
    {
        var chunk = Chunk(
            Name("cpe:2.3:a:contoso:widget:1.0:*:*:*:*:android:*:*", ("en", "Contoso Widget 1.0 for Android")),
            Name("cpe:2.3:a:contoso:widget:2.0:*:*:*:*:windows:*:*", ("en", "Contoso Widget 2.0 for Windows")),
            Name("cpe:2.3:a:contoso:widget:3.0:*:*:*:*:iphone_os:*:*", ("en", "Contoso Widget 3.0 for iPhone")),
            // A withdrawn name says nothing about the product any more.
            Name("cpe:2.3:a:contoso:plugin:1.0:*:*:*:*:wordpress:*:*", ("en", "Contoso Plugin 1.0")),
            Withdrawn("cpe:2.3:a:contoso:plugin:0.9:*:*:*:*:*:*:*", ("en", "Contoso Plugin 0.9")),
            // A name that ends before the target field targets no platform.
            Name("cpe:2.3:a:contoso:short_form", ("en", "Contoso Short Form")));

        var reduced = await ReadAsync(chunk);

        Assert.True(reduced["contoso:widget"].WindowsCapable);
        Assert.False(reduced["contoso:plugin"].WindowsCapable);
        Assert.True(reduced["contoso:short_form"].WindowsCapable);
    }

    [Fact]
    public async Task ReadChunk_TitleIsTheShortestEnglishOneOfAllNamesOfTheProduct()
    {
        var chunk = Chunk(
            Name("cpe:2.3:a:contoso:widget:1.0:*:*:*:*:*:*:*", ("de", "Widget"), ("en", "Contoso Widget Professional 1.0")),
            Name("cpe:2.3:a:contoso:widget:2.0:*:*:*:*:*:*:*", ("en", ""), ("en", "Contoso Widget 2.0"), ("en", "Contoso Widget 2.0 (64-bit)")),
            Name("cpe:2.3:a:contoso:widget:3.0:*:*:*:*:*:*:*"),
            Withdrawn("cpe:2.3:a:contoso:widget:0.1:*:*:*:*:*:*:*", ("en", "CW")),
            Name("cpe:2.3:a:contoso:untitled:1.0:*:*:*:*:*:*:*", ("ja", "コントソ")));

        var reduced = await ReadAsync(chunk);

        Assert.Equal("Contoso Widget 2.0", reduced["contoso:widget"].Title);
        Assert.Equal(string.Empty, reduced["contoso:untitled"].Title);
    }

    [Fact]
    public async Task ReadChunk_EntriesOfAnotherShape_AreIgnored()
    {
        var chunk = Json("""
            { "products": [
                42, "text", null, true,
                [ 1, [ 2, 3 ], { "cpe": { "cpeName": "cpe:2.3:a:decoy:in_an_array:1.0:*:*:*:*:*:*:*" } } ],
                { "note": "no cpe here" },
                { "cpe": "not an object" },
                { "cpe": { "cpeName": 7 } },
                { "cpe": { "cpeName": "not a cpe name" } },
                { "cpe": { "deprecated": "no", "cpeName": "cpe:2.3:a:contoso:widget:4.2:*:*:*:*:*:*:*", "titles": "none" } },
                { "cpe": { "cpeName": "cpe:2.3:a:contoso:gadget:1.0:*:*:*:*:*:*:*", "titles": [ 7, { "lang": "en" }, { "lang": 1, "title": "x" }, { "lang": "en", "title": "Contoso Gadget" } ] } }
            ] }
            """);

        var reduced = await ReadAsync(chunk);

        Assert.Equal(
            new[] { ("contoso:gadget", "Contoso Gadget", true), ("contoso:widget", string.Empty, true) },
            StateOf(reduced));
    }

    [Theory]
    [InlineData("""{ "format": "NVD_CPE", "products": { "cpe": { "cpeName": "cpe:2.3:a:decoy:object:1.0:*:*:*:*:*:*:*" } }, "version": "2.0" }""")]
    [InlineData("""{ "products": "none", "version": "2.0" }""")]
    [InlineData("""{ "products": null }""")]
    [InlineData("""{ "products": 42, "after": [ { "cpe": { "cpeName": "cpe:2.3:a:decoy:after:1.0:*:*:*:*:*:*:*" } } ] }""")]
    [InlineData("""{ "format": "NVD_CPE" }""")]
    [InlineData("""{ "feed": { "products": [ { "cpe": { "cpeName": "cpe:2.3:a:decoy:nested:1.0:*:*:*:*:*:*:*" } } ] } }""")]
    [InlineData("""[ { "products": [ { "cpe": { "cpeName": "cpe:2.3:a:decoy:in_a_root_array:1.0:*:*:*:*:*:*:*" } } ] } ]""")]
    [InlineData("{}")]
    public async Task ReadChunk_NoProductsArrayAtTheRoot_YieldsNothingAndDoesNotThrow(string json)
    {
        Assert.Empty(await ReadAsync(Json(json)));
        Assert.Empty(await ReadAsync(Json(json), bufferBytes: 16));
    }

    // -----------------------------------------------------------------------
    // ReadChunkAsync: the buffer
    // -----------------------------------------------------------------------

    private const int LargeChunkProducts = 480;
    private const int LargeChunkLargeEntries = 3;

    /// <summary>The names of one product of a stress chunk; which kind it is goes by its number.</summary>
    private static IEnumerable<string> NamesOfProduct(int number)
    {
        var id = number.ToString("000", CultureInfo.InvariantCulture);
        var product = $"cpe:2.3:a:contoso:product_{id}";
        switch (number % 6)
        {
            case 0:
                yield return Name($"{product}:1.0:*:*:*:*:*:*:*", ("en", $"Contoso Product {id}"));
                break;
            case 1:
                yield return Name($"{product}:1.0:*:*:*:*:android:*:*", ("en", $"Contoso Product {id} for Android"));
                yield return Name($"{product}:2.0:*:*:*:*:windows:*:*", ("en", $"Contoso Product {id}"));
                break;
            case 2:
                yield return Name($"{product}:1.0:*:*:*:*:wordpress:*:*", ("en", $"Contoso Product {id} Plugin"));
                break;
            case 3:
                yield return Withdrawn($"{product}:1.0:*:*:*:*:*:*:*", ("en", $"Contoso Product {id}"));
                break;
            case 4:
                yield return Name($"{product}:-:*:*:*:*:-:*:*", ("ja", "コントソ製品"), ("en", $"Contoso Prödükt {id} ✓"));
                break;
            default:
                yield return Name($"{product}:1.0:*:*:*:*:*:*:*", ("en", $"Contoso \"Product\" {id} [x64] {{beta}} \\ edition"));
                break;
        }
    }

    /// <summary>
    /// A chunk that puts every part of the reader between two buffer fills at some buffer size:
    /// <paramref name="products"/> products of a few hundred bytes per name, entries of several
    /// kilobytes near the end, entries that are no product names, and root properties around
    /// the products array that hold objects and arrays of their own; <paramref name="rootPadding"/>
    /// sizes the largest of them, which at 600 is larger than any entry. What the chunk reduces
    /// to is stated by <see cref="AssertStressChunk"/>.
    /// </summary>
    private static byte[] StressChunk(int products, int largeEntries, int rootPadding)
    {
        const string decoy = "{ \"cpe\": { \"deprecated\": false, \"cpeName\": \"cpe:2.3:a:decoy:{0}:1.0:*:*:*:*:*:*:*\", \"titles\": [ { \"title\": \"Decoy\", \"lang\": \"en\" } ] } }";
        string Decoy(string name) => decoy.Replace("{0}", name, StringComparison.Ordinal);

        var entries = new List<string>();
        for (var number = 0; number < products; number++)
        {
            entries.AddRange(NamesOfProduct(number));
            if (number % 97 == 0)
                entries.AddRange(new[] { "42", "\"text\"", "null", $"[ 1, [ 2, 3 ], {Decoy("in_an_array")} ]", "{ \"note\": \"no cpe here\" }" });
        }
        for (var large = 0; large < largeEntries; large++)
            entries.Add(Entry($"cpe:2.3:a:contoso:large_{large}:1.0:*:*:*:*:*:*:*", false, 60, new[] { ("en", $"Contoso Large {large}") }));
        entries.Add(Name("cpe:2.3:a:contoso:last:1.0:*:*:*:*:*:*:*", ("en", "Contoso Last")));

        var json = new StringBuilder();
        json.Append("{\n  \"resultsPerPage\": 500,\n  \"startIndex\": 0,\n  \"format\": \"NVD_CPE\",\n");
        json.Append("  \"source\": { \"name\": \"synthetic\", \"products\": [ ").Append(Decoy("before")).Append(" ], \"depth\": { \"a\": [ 1, 2, { \"b\": null } ] } },\n");
        json.Append("  \"ids\": [ 1, [ 2, 3 ], { \"products\": [] } ],\n");
        json.Append("  \"products\": [\n    ").Append(string.Join(",\n    ", entries)).Append("\n  ],\n");
        json.Append("  \"archive\": { \"note\": ").Append(Quote(string.Concat(Enumerable.Repeat("a } ] \" \\ é ", rootPadding))));
        json.Append(", \"products\": [ ").Append(Decoy("after")).Append(" ], \"list\": [ ");
        json.Append(string.Join(", ", Enumerable.Range(0, rootPadding))).Append(" ] },\n");
        json.Append("  \"remarks\": [ \"one\", [ \"two\", [ \"three\" ] ], { \"products\": [ ").Append(Decoy("in_remarks")).Append(" ] } ],\n");
        json.Append("  \"timestamp\": \"2026-11-12T03:00:00.000\",\n  \"complete\": true,\n  \"next\": null\n}\n   \n");
        return Encoding.UTF8.GetBytes(json.ToString());
    }

    private static void AssertStressChunk(Dictionary<string, CpeDictionaryService.ProductState> reduced, int products, int largeEntries)
    {
        var expectedCount = 0;
        for (var number = 0; number < products; number++)
        {
            var id = number.ToString("000", CultureInfo.InvariantCulture);
            var found = reduced.TryGetValue($"contoso:product_{id}", out var state);
            (string Title, bool WindowsCapable)? expected = (number % 6) switch
            {
                0 or 1 => ($"Contoso Product {id}", true),
                2 => ($"Contoso Product {id} Plugin", false),
                3 => null,
                4 => ($"Contoso Prödükt {id} ✓", true),
                _ => ($"Contoso \"Product\" {id} [x64] {{beta}} \\ edition", true),
            };

            Assert.Equal(expected != null, found);
            if (expected == null) continue;
            Assert.Equal(expected.Value, (state!.Title, state.WindowsCapable));
            expectedCount++;
        }

        for (var large = 0; large < largeEntries; large++)
        {
            var state = reduced[$"contoso:large_{large}"];
            Assert.Equal(($"Contoso Large {large}", true), (state.Title, state.WindowsCapable));
        }
        Assert.Equal(("Contoso Last", true), (reduced["contoso:last"].Title, reduced["contoso:last"].WindowsCapable));

        // Nothing but the root's own products array was read.
        Assert.Equal(expectedCount + largeEntries + 1, reduced.Count);
        Assert.DoesNotContain(reduced.Keys, key => key.StartsWith("decoy:", StringComparison.Ordinal));
    }

    private static readonly Lazy<byte[]> LargeChunk = new(() => StressChunk(LargeChunkProducts, LargeChunkLargeEntries, rootPadding: 600));

    [Fact]
    public async Task ReadChunk_LargeChunk_IsReducedToItsProducts()
    {
        var reduced = await ReadAsync(LargeChunk.Value);

        AssertStressChunk(reduced, LargeChunkProducts, LargeChunkLargeEntries);
        Assert.Equal(LargeChunkProducts / 6 * 5 + LargeChunkLargeEntries + 1, reduced.Count);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(64)]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task ReadChunk_BufferFarSmallerThanTheChunk_YieldsExactlyWhatTheDefaultBufferYields(int bufferBytes)
    {
        var withDefaultBuffer = await ReadAsync(LargeChunk.Value);
        var probe = new ProbeStream(LargeChunk.Value);

        var withSmallBuffer = await ReadAsync(probe, bufferBytes);

        Assert.Equal(StateOf(withDefaultBuffer), StateOf(withSmallBuffer));
        AssertStressChunk(withSmallBuffer, LargeChunkProducts, LargeChunkLargeEntries);

        // The comparison only says something when the buffer was filled many times over and had
        // to grow for an entry that did not fit; a pool that hands out one large array would
        // make it pass for nothing.
        Assert.InRange(probe.FirstCapacity, bufferBytes, 4096);
        Assert.True(probe.Reads >= 50, $"only {probe.Reads} reads");
        Assert.True(probe.LargestCapacity > probe.FirstCapacity, "the buffer never grew");
        Assert.True(probe.LargestCapacity < LargeChunk.Value.Length / 8, "the buffer came to hold a large part of the chunk");
    }

    [Theory]
    [InlineData(13)]
    [InlineData(4099)]
    public async Task ReadChunk_StreamThatHandsOutLessThanAskedFor_YieldsTheSame(int bytesPerRead)
    {
        var whole = await ReadAsync(LargeChunk.Value);

        var inPieces = await ReadAsync(new ProbeStream(LargeChunk.Value, bytesPerRead));

        Assert.Equal(StateOf(whole), StateOf(inPieces));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(16)]
    public async Task ReadChunk_StreamThatHandsOutOneByteAtATime_CutsEveryTokenEverywhere_AndYieldsTheSame(int? bufferBytes)
    {
        // Small enough to be read byte by byte, with every kind of entry and one large one in it.
        const int products = 36, largeEntries = 1;
        var chunk = StressChunk(products, largeEntries, rootPadding: 30);
        var probe = new ProbeStream(chunk, mostPerRead: 1);

        var byteByByte = await ReadAsync(probe, bufferBytes);

        AssertStressChunk(byteByByte, products, largeEntries);
        Assert.Equal(StateOf(await ReadAsync(chunk)), StateOf(byteByByte));
        Assert.True(probe.Reads > chunk.Length, "the chunk was not read byte by byte");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.001)]
    [InlineData(0.3)]
    [InlineData(0.7)]
    [InlineData(0.98)]
    public async Task ReadChunk_ChunkThatEndsBeforeItsEnd_IsAJsonError_AtEveryBufferSize(double share)
    {
        var whole = Chunk(Enumerable.Range(0, 30).SelectMany(NamesOfProduct).ToArray());
        var cut = whole[..(int)(whole.Length * share)];

        await Assert.ThrowsAnyAsync<JsonException>(() => ReadAsync(cut));
        await Assert.ThrowsAnyAsync<JsonException>(() => ReadAsync(cut, bufferBytes: 16));
        await Assert.ThrowsAnyAsync<JsonException>(() => ReadAsync(cut, bufferBytes: 1000));
    }

    [Fact]
    public async Task ReadChunk_ChunkWithoutItsClosingBrace_IsAJsonError()
    {
        var whole = Encoding.UTF8.GetString(Chunk(Name("cpe:2.3:a:contoso:widget:4.2:*:*:*:*:*:*:*", ("en", "Contoso Widget 4.2"))));
        var cut = Json(whole[..whole.LastIndexOf('}')]);

        await Assert.ThrowsAnyAsync<JsonException>(() => ReadAsync(cut));
        await Assert.ThrowsAnyAsync<JsonException>(() => ReadAsync(cut, bufferBytes: 16));
    }

    /// <summary>A chunk whose first entry never ends: its bytes are produced as they are asked for.</summary>
    private sealed class EndlessEntryStream : Stream
    {
        private static readonly byte[] Start = Encoding.UTF8.GetBytes("{ \"products\": [ { \"cpe\": { \"cpeName\": \"");

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(Span<byte> buffer)
        {
            var fromStart = (int)Math.Min(Math.Max(Start.Length - BytesRead, 0), buffer.Length);
            Start.AsSpan((int)Math.Min(BytesRead, Start.Length), fromStart).CopyTo(buffer);
            buffer[fromStart..].Fill((byte)'a');
            BytesRead += buffer.Length;
            return buffer.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ReadChunk_EntryThatNeverEnds_IsRefusedOnceTheBufferReachedItsLimit()
    {
        var endless = new EndlessEntryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(endless));

        // The buffer grew to its limit and no further: the file is given up, not loaded.
        Assert.InRange(endless.BytesRead, CpeDictionaryService.MaxEntryBytes, 2L * CpeDictionaryService.MaxEntryBytes);
    }

    // -----------------------------------------------------------------------
    // ReduceAsync
    // -----------------------------------------------------------------------

    /// <summary>The feed archive: a gzipped tar of the given files; an entry without content is a directory.</summary>
    private static byte[] Archive(params (string Name, byte[]? Content)[] entries)
    {
        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                if (content == null)
                    tar.WriteEntry(new UstarTarEntry(TarEntryType.Directory, name));
                else
                    tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) });
            }
        }
        return archive.ToArray();
    }

    private static readonly byte[] SmallArchive = Archive(
        ("nvdcpe-2.0-chunks/", null),
        ("nvdcpe-2.0-chunks/README.txt", Json("{ \"products\": [ { \"cpe\": { \"cpeName\": \"cpe:2.3:a:decoy:not_a_chunk:1.0:*:*:*:*:*:*:*\" } } ] }")),
        ("nvdcpe-2.0-chunks/nvdcpe-2.0-chunk-00001.json", Chunk(
            Name("cpe:2.3:a:contoso:widget:1.0:*:*:*:*:android:*:*", ("en", "Contoso Widget")),
            Name("cpe:2.3:a:contoso:plugin:1.0:*:*:*:*:wordpress:*:*", ("en", "Contoso Plugin 1.0")),
            Name("cpe:2.3:a:fabrikam:gadget:7.0:*:*:*:*:*:*:*", ("en", "Fabrikam Gadget 7.0")))),
        ("nvdcpe-2.0-chunks/nvdcpe-2.0-chunk-00002.JSON", Chunk(
            // The second file completes a product the first one began.
            Name("cpe:2.3:a:contoso:widget:2.0:*:*:*:*:windows:*:*", ("en", "Contoso Widget 2.0 for Windows")),
            Name("cpe:2.3:o:contoso:widget_os:1.0:*:*:*:*:*:*:*", ("en", "Contoso Widget OS")),
            Name("cpe:2.3:a:northwind:tool:3.1:*:*:*:*:-:*:*", ("en", "Northwind Tool 3.1")))));

    [Fact]
    public async Task Reduce_IsTheUnionOfAllChunkFiles_WithOnlyTheProductsThatCanBeWindowsSoftware()
    {
        var products = await CpeDictionaryService.ReduceAsync(new MemoryStream(SmallArchive), CancellationToken.None);

        Assert.Equal(
            new[]
            {
                ("contoso:widget", "Contoso Widget"),
                ("fabrikam:gadget", "Fabrikam Gadget 7.0"),
                ("northwind:tool", "Northwind Tool 3.1"),
            },
            products.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value.T)));
    }

    [Fact]
    public async Task Reduce_ArchiveWithoutChunkFiles_IsEmpty()
    {
        var archive = Archive(("nvdcpe-2.0-chunks/", null), ("nvdcpe-2.0-chunks/README.txt", Json("nothing to read")));

        Assert.Empty(await CpeDictionaryService.ReduceAsync(new MemoryStream(archive), CancellationToken.None));
    }

    // -----------------------------------------------------------------------
    // ParseFeedVersion
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("lastModifiedDate:2026-11-12T03:00:01-05:00\r\nsize:913338293\r\ngzSize:85733215\r\nsha256:0123abcdef\r\n", "0123ABCDEF")]
    [InlineData("sha256:0123ABCDEF", "0123ABCDEF")]
    [InlineData("size:1\n  SHA256:  0123abcdef  \nsize:2", "0123ABCDEF")]
    [InlineData("sha256:first\nsha256:second", "FIRST")]
    public void ParseFeedVersion_IsTheHashLineInUpperCase(string meta, string expected) =>
        Assert.Equal(expected, CpeDictionaryService.ParseFeedVersion(meta));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lastModifiedDate:2026-11-12T03:00:01-05:00\r\nsize:913338293\r\n")]
    [InlineData("sha256:\r\nsize:1")]
    [InlineData("sha256:   ")]
    [InlineData("<html>maintenance</html>")]
    public void ParseFeedVersion_WithoutAHash_IsNull(string? meta) =>
        Assert.Null(CpeDictionaryService.ParseFeedVersion(meta));

    // -----------------------------------------------------------------------
    // RefreshAsync and the stored copy
    // -----------------------------------------------------------------------

    /// <summary>Stands in for the NVD feed directory.</summary>
    private sealed class FeedStub : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Meta { get; set; } = () => Text($"lastModifiedDate:2026-11-12T03:00:01-05:00\r\nsha256:{FeedVersion.ToLowerInvariant()}\r\n");
        public Func<HttpResponseMessage> Archive { get; set; } = () => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            return Task.FromResult(url switch
            {
                CpeDictionaryService.MetaUrl => Meta(),
                CpeDictionaryService.FeedUrl => Archive(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }
    }

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static IEnumerable<(string ProductKey, string Title)> Products(int count) => Enumerable.Range(0, count)
        .Select(n => ($"contoso:product_{n:00000}", $"Contoso Product {n:00000}"));

    private const int FullSize = CpeDictionaryService.MinProducts + 5;

    /// <summary>An archive that reduces to a dictionary of full size, in two chunk files.</summary>
    private static readonly Lazy<byte[]> FullSizeArchive = new(() =>
    {
        byte[] ChunkOf(int from, int to) => Chunk(Enumerable.Range(from, to - from)
            .Select(n => Entry($"cpe:2.3:a:contoso:product_{n:00000}:1.0:*:*:*:*:*:*:*", false, 0, new[] { ("en", $"Contoso Product {n:00000}") }))
            .ToArray());
        return Archive(
            ("nvdcpe-2.0-chunks/nvdcpe-2.0-chunk-00001.json", ChunkOf(0, FullSize / 2)),
            ("nvdcpe-2.0-chunks/nvdcpe-2.0-chunk-00002.json", ChunkOf(FullSize / 2, FullSize)));
    });

    private static BlobBackedReferenceCache<CpeDictionaryProduct>.Snapshot SnapshotIn(StoredBlob blob) =>
        JsonSerializer.Deserialize<BlobBackedReferenceCache<CpeDictionaryProduct>.Snapshot>(blob.Content!)!;

    [Fact]
    public async Task Refresh_NewFeed_StoresTheReducedCopy_ServesIt_AndFindsNothingNewTheNextTime()
    {
        var blob = new StoredBlob();
        var feed = new FeedStub { Archive = () => Bytes(FullSizeArchive.Value) };
        var service = CpeDictionaryFixture.Service(feed, blob);

        var before = DateTime.UtcNow;
        var first = await service.RefreshAsync();

        Assert.Null(first.Error);
        Assert.True(first.Changed);
        Assert.Equal(FullSize, first.Products);
        Assert.Equal(FeedVersion, first.FeedVersion);
        Assert.Equal(new[] { CpeDictionaryService.MetaUrl, CpeDictionaryService.FeedUrl }, feed.Requests);

        Assert.Equal(1, blob.Uploads);
        var stored = SnapshotIn(blob);
        Assert.Equal(new[] { FeedVersion }, stored.CoveredDocuments);
        Assert.Equal(FullSize, stored.Entries.Count);
        Assert.Equal("Contoso Product 00042", stored.Entries["contoso:product_00042"].T);

        // The new copy is in service without being read back from the blob.
        var dictionary = await service.GetAsync();
        Assert.Equal(FullSize, dictionary!.Count);
        Assert.Equal("cpe:2.3:a:contoso:product_00042", dictionary.Resolve("product 00042", "contoso", null)!.Value.CpeUri);
        Assert.Equal(0, blob.Downloads);
        Assert.InRange((await service.LastRefreshAsync())!.Value, before, DateTime.UtcNow);

        var second = await service.RefreshAsync();

        Assert.Null(second.Error);
        Assert.False(second.Changed);
        Assert.Equal(FullSize, second.Products);
        Assert.Equal(FeedVersion, second.FeedVersion);
        Assert.Equal(new[] { CpeDictionaryService.MetaUrl, CpeDictionaryService.FeedUrl, CpeDictionaryService.MetaUrl }, feed.Requests);
        Assert.Equal(1, blob.Uploads);
    }

    [Fact]
    public async Task Refresh_FeedUnchanged_AndAStoredCopyOfFullSize_DownloadsNoArchive()
    {
        var blob = new StoredBlob(CpeDictionaryFixture.SnapshotJson(Products(CpeDictionaryService.MinProducts), FeedVersion));
        var feed = new FeedStub { Archive = () => Bytes(FullSizeArchive.Value) };

        var result = await CpeDictionaryFixture.Service(feed, blob).RefreshAsync();

        Assert.Null(result.Error);
        Assert.False(result.Changed);
        Assert.Equal(CpeDictionaryService.MinProducts, result.Products);
        Assert.Equal(FeedVersion, result.FeedVersion);
        Assert.Equal(new[] { CpeDictionaryService.MetaUrl }, feed.Requests);
        Assert.Equal(0, blob.Uploads);
    }

    [Theory]
    [InlineData("another feed version")]
    [InlineData("a stored copy that is too small")]
    public async Task Refresh_StoredCopyThatIsNotTheCurrentFeedInFull_IsReplaced(string reason)
    {
        var stored = reason == "another feed version"
            ? CpeDictionaryFixture.SnapshotJson(Products(CpeDictionaryService.MinProducts), "AN-EARLIER-FEED")
            : CpeDictionaryFixture.SnapshotJson(Products(CpeDictionaryService.MinProducts - 1), FeedVersion);
        var blob = new StoredBlob(stored);
        var feed = new FeedStub { Archive = () => Bytes(FullSizeArchive.Value) };

        var result = await CpeDictionaryFixture.Service(feed, blob).RefreshAsync();

        Assert.Null(result.Error);
        Assert.True(result.Changed);
        Assert.Equal(FullSize, result.Products);
        Assert.Equal(1, blob.Uploads);
        Assert.Equal(new[] { FeedVersion }, SnapshotIn(blob).CoveredDocuments);
    }

    [Fact]
    public async Task Refresh_ArchiveThatReducesToTooFewProducts_ReportsIt_AndKeepsTheStoredCopy()
    {
        var earlier = CpeDictionaryFixture.SnapshotJson(new[] { ("contoso:earlier", "Contoso Earlier") }, "AN-EARLIER-FEED");
        var blob = new StoredBlob(earlier);
        var feed = new FeedStub { Archive = () => Bytes(SmallArchive) };
        var service = CpeDictionaryFixture.Service(feed, blob);

        var result = await service.RefreshAsync();

        Assert.False(result.Changed);
        Assert.Contains("only 3 products", result.Error);
        Assert.Equal(FeedVersion, result.FeedVersion);
        Assert.Equal(0, blob.Uploads);
        Assert.Equal(earlier, blob.Content);
        Assert.NotNull((await service.GetAsync())!.Resolve("earlier", "contoso", null));
    }

    [Theory]
    [InlineData("the version file is not served")]
    [InlineData("the connection breaks")]
    [InlineData("the archive is not served")]
    [InlineData("the archive is no archive")]
    [InlineData("the archive is cut off")]
    public async Task Refresh_FailureOnTheWay_IsReportedInTheResult_NeverThrown(string failure)
    {
        var blob = new StoredBlob();
        var feed = new FeedStub();
        switch (failure)
        {
            case "the version file is not served": feed.Meta = () => new HttpResponseMessage(HttpStatusCode.InternalServerError); break;
            case "the connection breaks": feed.Meta = () => throw new HttpRequestException("connection reset"); break;
            case "the archive is not served": feed.Archive = () => new HttpResponseMessage(HttpStatusCode.BadGateway); break;
            case "the archive is no archive": feed.Archive = () => Text("<html>maintenance</html>"); break;
            default: feed.Archive = () => Bytes(FullSizeArchive.Value[..(FullSizeArchive.Value.Length / 2)]); break;
        }
        var service = CpeDictionaryFixture.Service(feed, blob);

        var result = await service.RefreshAsync();

        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.False(result.Changed);
        Assert.Equal(0, blob.Uploads);
        Assert.Null(await service.GetAsync());
    }

    [Fact]
    public async Task Get_NothingStored_IsNull_AndAStoredDictionaryIsLoadedOnce()
    {
        var nothingStored = CpeDictionaryFixture.Stored();
        var blob = new StoredBlob(CpeDictionaryFixture.SnapshotJson(new[] { ("contoso:widget", "Contoso Widget") }));
        var stored = CpeDictionaryFixture.Service(new FeedStub(), blob);

        Assert.Null(await nothingStored.GetAsync());
        Assert.Null(await nothingStored.LastRefreshAsync());

        var first = await stored.GetAsync();
        var second = await stored.GetAsync();

        Assert.Same(first, second);
        Assert.Equal(1, blob.Downloads);
        Assert.Equal("Contoso Widget", first!.Resolve("widget", "contoso", null)!.Value.Title);
        Assert.Equal(new DateTime(2026, 11, 11, 3, 0, 0, DateTimeKind.Utc), await stored.LastRefreshAsync());
    }

    [Fact]
    public async Task Get_StoredSnapshotWithoutProducts_CountsAsNoDictionary()
    {
        var service = CpeDictionaryFixture.Service(
            new FeedStub(), new StoredBlob(CpeDictionaryFixture.SnapshotJson(Array.Empty<(string, string)>(), FeedVersion)));

        Assert.Null(await service.GetAsync());
    }

    [Fact]
    public async Task Get_InstanceThatFoundNoDictionary_FindsTheOneStoredLater()
    {
        var blob = new StoredBlob();
        var service = CpeDictionaryFixture.Service(new FeedStub(), blob);
        Assert.Null(await service.GetAsync());

        // Another instance's nightly run stored the first dictionary.
        blob.Content = CpeDictionaryFixture.SnapshotJson(new[] { ("contoso:widget", "Contoso Widget") });

        Assert.NotNull((await service.GetAsync())!.Resolve("widget", "contoso", null));
    }

    /// <summary>
    /// Makes the copy an instance holds look as if it had been loaded <paramref name="age"/> ago.
    /// The hours cannot be waited for, and the service reads the clock itself.
    /// </summary>
    private static void BackdateLoadedCopy(CpeDictionaryService service, TimeSpan age)
    {
        var loadedAt = typeof(CpeDictionaryService).GetField("_loadedAtUtc", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(loadedAt);
        loadedAt!.SetValue(service, DateTime.UtcNow - age);
    }

    [Fact]
    public async Task Get_CopyLoadedLongAgo_LooksAtTheBlobAgain_AndServesWhatAnotherInstanceStoredSince()
    {
        var blob = new StoredBlob(CpeDictionaryFixture.SnapshotJson(new[] { ("contoso:widget", "Contoso Widget") }));
        var service = CpeDictionaryFixture.Service(new FeedStub(), blob);
        var first = await service.GetAsync();

        // Only the instance whose nightly run downloaded the feed holds the new dictionary by itself.
        blob.Content = CpeDictionaryFixture.SnapshotJson(new[] { ("contoso:widget", "Contoso Widget"), ("fabrikam:gadget", "Fabrikam Gadget") });
        BackdateLoadedCopy(service, CpeDictionaryService.LoadedCopyMaxAge - TimeSpan.FromMinutes(5));
        Assert.Same(first, await service.GetAsync());
        Assert.Equal(1, blob.Downloads);

        BackdateLoadedCopy(service, CpeDictionaryService.LoadedCopyMaxAge + TimeSpan.FromMinutes(5));
        var second = await service.GetAsync();

        Assert.Equal(2, blob.Downloads);
        Assert.NotNull(second!.Resolve("gadget", "fabrikam", null));
        // The copy just read is held again for its own time.
        Assert.Same(second, await service.GetAsync());
        Assert.Equal(2, blob.Downloads);
    }

    [Theory]
    [InlineData("the blob is gone")]
    [InlineData("the blob cannot be parsed")]
    [InlineData("the blob holds no product")]
    public async Task Get_CopyLoadedLongAgo_AndNoUsableBlob_KeepsServingTheCopyItHolds(string state)
    {
        var blob = new StoredBlob(CpeDictionaryFixture.SnapshotJson(new[] { ("contoso:widget", "Contoso Widget") }));
        var service = CpeDictionaryFixture.Service(new FeedStub(), blob);
        var held = await service.GetAsync();
        blob.Content = state switch
        {
            "the blob is gone" => null,
            "the blob cannot be parsed" => "{ \"entries\": ",
            _ => CpeDictionaryFixture.SnapshotJson(Array.Empty<(string, string)>(), FeedVersion),
        };
        BackdateLoadedCopy(service, CpeDictionaryService.LoadedCopyMaxAge + TimeSpan.FromMinutes(5));

        var served = await service.GetAsync();

        Assert.Same(held, served);
    }

    // -----------------------------------------------------------------------
    // One refresh at a time
    // -----------------------------------------------------------------------

    /// <summary>A feed whose archive is held back until the test lets it through.</summary>
    private sealed class HeldDownload : IDisposable
    {
        private readonly ManualResetEventSlim _asked = new(false);
        private readonly ManualResetEventSlim _released = new(false);

        public HeldDownload()
        {
            Feed = new FeedStub
            {
                Archive = () =>
                {
                    _asked.Set();
                    Assert.True(_released.Wait(TimeSpan.FromSeconds(30)), "The test never released the download.");
                    return Bytes(FullSizeArchive.Value);
                },
            };
            Service = CpeDictionaryFixture.Service(Feed, Blob);
            // The stub answers on the caller's thread, so the held refresh needs one of its own.
            Running = Task.Run(() => Service.RefreshAsync());
            Assert.True(_asked.Wait(TimeSpan.FromSeconds(30)), "The refresh never asked for the archive.");
        }

        public FeedStub Feed { get; }
        public StoredBlob Blob { get; } = new();
        public CpeDictionaryService Service { get; }
        public Task<CpeDictionaryRefreshResult> Running { get; }

        public Task<CpeDictionaryRefreshResult> ReleaseAsync()
        {
            _released.Set();
            return Running;
        }

        public void Dispose() => _released.Set();
    }

    [Fact]
    public async Task Refresh_WhileOneRuns_AndNotWaiting_DoesNothingAndSaysSo()
    {
        using var held = new HeldDownload();

        var second = await held.Service.RefreshAsync(CancellationToken.None, waitForRunningRefresh: false);

        Assert.True(second.AlreadyRunning);
        Assert.False(second.Changed);
        Assert.Null(second.Error);
        // The second call asked NVD for nothing: neither the version nor the archive.
        Assert.Equal(new[] { CpeDictionaryService.MetaUrl, CpeDictionaryService.FeedUrl }, held.Feed.Requests);

        var first = await held.ReleaseAsync();
        Assert.True(first.Changed);
        Assert.False(first.AlreadyRunning);
        Assert.Equal(1, held.Blob.Uploads);

        // The lock is free again.
        var third = await held.Service.RefreshAsync(CancellationToken.None, waitForRunningRefresh: false);
        Assert.False(third.AlreadyRunning);
        Assert.False(third.Changed);
        Assert.Equal(FullSize, third.Products);
    }

    [Fact]
    public async Task Refresh_WhileOneRuns_AndWaiting_FindsTheStoredCopyCurrentInsteadOfDownloadingAgain()
    {
        using var held = new HeldDownload();

        // The nightly run: it must not skip the dictionary because the button was a moment faster.
        var waiting = held.Service.RefreshAsync();
        await Task.Delay(150);
        Assert.False(waiting.IsCompleted);

        var first = await held.ReleaseAsync();
        var second = await waiting;

        Assert.True(first.Changed);
        Assert.False(second.AlreadyRunning);
        Assert.False(second.Changed);
        Assert.Null(second.Error);
        Assert.Equal(FullSize, second.Products);
        Assert.Equal(
            new[] { CpeDictionaryService.MetaUrl, CpeDictionaryService.FeedUrl, CpeDictionaryService.MetaUrl }, held.Feed.Requests);
        Assert.Equal(1, held.Blob.Uploads);
    }

    // -----------------------------------------------------------------------
    // The operator's button (POST vulnerability/sync-dictionary)
    // -----------------------------------------------------------------------

    private static TriggerCpeDictionarySyncFunction Button(CpeDictionaryService service) =>
        new(service, telemetryClient: null, NullLogger<TriggerCpeDictionarySyncFunction>.Instance);

    private static async Task<JsonElement> PressAsync(CpeDictionaryService service)
    {
        var response = await Button(service).Run(OperatorCall.Request());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return OperatorCall.BodyOf(response);
    }

    [Fact]
    public async Task Button_NoDictionaryYet_DownloadsIt_AndSaysHowManyProductsItHolds()
    {
        var blob = new StoredBlob();
        var service = CpeDictionaryFixture.Service(new FeedStub { Archive = () => Bytes(FullSizeArchive.Value) }, blob);

        var body = await PressAsync(service);

        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.True(body.GetProperty("changed").GetBoolean());
        Assert.Equal(FullSize, body.GetProperty("products").GetInt32());
        Assert.Equal("NVD product dictionary loaded (10,005 products)", body.GetProperty("message").GetString());
        Assert.False(body.TryGetProperty("error", out _));
        Assert.True(DateTime.TryParse(body.GetProperty("syncedAt").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _));
        Assert.Equal(1, blob.Uploads);
        // The instance that downloaded it serves it at once, which is what Auto-Map needs next.
        Assert.Equal(FullSize, (await service.GetAsync())!.Count);
    }

    [Fact]
    public async Task Button_NvdPublishedNothingNew_SaysTheStoredCopyIsCurrent()
    {
        var blob = new StoredBlob(CpeDictionaryFixture.SnapshotJson(Products(CpeDictionaryService.MinProducts), FeedVersion));
        var feed = new FeedStub { Archive = () => Bytes(FullSizeArchive.Value) };

        var body = await PressAsync(CpeDictionaryFixture.Service(feed, blob));

        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.False(body.GetProperty("changed").GetBoolean());
        Assert.Equal(CpeDictionaryService.MinProducts, body.GetProperty("products").GetInt32());
        Assert.Equal("NVD product dictionary is current (10,000 products)", body.GetProperty("message").GetString());
        Assert.Equal(new[] { CpeDictionaryService.MetaUrl }, feed.Requests);
        Assert.Equal(0, blob.Uploads);
    }

    [Fact]
    public async Task Button_NvdDoesNotAnswer_SaysWhy_AndThatTheStoredCopyStays()
    {
        var blob = new StoredBlob(CpeDictionaryFixture.SnapshotJson(Products(CpeDictionaryService.MinProducts), FeedVersion));
        var feed = new FeedStub { Meta = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };

        var body = await PressAsync(CpeDictionaryFixture.Service(feed, blob));

        // Not a server error: the request did its work, the answer says that NVD did not.
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.False(body.GetProperty("changed").GetBoolean());
        Assert.Equal(CpeDictionaryService.MinProducts, body.GetProperty("products").GetInt32());
        var message = body.GetProperty("message").GetString()!;
        Assert.StartsWith("NVD product dictionary sync failed: ", message);
        Assert.EndsWith(". The stored copy (10,000 products) stays in service.", message);
        Assert.DoesNotContain("..", message);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
        Assert.Equal(0, blob.Uploads);
    }

    [Fact]
    public async Task Button_NvdDoesNotAnswer_AndNothingIsStored_NamesNoStoredCopy()
    {
        var feed = new FeedStub { Meta = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };

        var body = await PressAsync(CpeDictionaryFixture.Service(feed, new StoredBlob()));

        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(0, body.GetProperty("products").GetInt32());
        var message = body.GetProperty("message").GetString()!;
        Assert.StartsWith("NVD product dictionary sync failed: ", message);
        Assert.DoesNotContain("stored copy", message);
    }

    [Fact]
    public async Task Button_WhileADownloadRuns_IsAConflict_AndStartsNoSecondOne()
    {
        using var held = new HeldDownload();

        var response = await Button(held.Service).Run(OperatorCall.Request());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.Conflict, EndpointHarness.ErrorCode(response));
        Assert.Equal(new[] { CpeDictionaryService.MetaUrl, CpeDictionaryService.FeedUrl }, held.Feed.Requests);

        Assert.True((await held.ReleaseAsync()).Changed);
    }

    [Theory]
    [InlineData(true, null, 34859, "NVD product dictionary loaded (34,859 products)")]
    [InlineData(false, null, 34859, "NVD product dictionary is current (34,859 products)")]
    [InlineData(false, "The reduced dictionary holds only 12 products, too few for a full feed.", 34859,
        "NVD product dictionary sync failed: The reduced dictionary holds only 12 products, too few for a full feed. The stored copy (34,859 products) stays in service.")]
    [InlineData(false, "A task was canceled", 0, "NVD product dictionary sync failed: A task was canceled.")]
    public void Button_Message_SaysWhatHappenedInOneSentenceEach(bool changed, string? error, int products, string expected) =>
        Assert.Equal(expected, TriggerCpeDictionarySyncFunction.MessageFor(
            new CpeDictionaryRefreshResult { Changed = changed, Error = error }, products));
}
