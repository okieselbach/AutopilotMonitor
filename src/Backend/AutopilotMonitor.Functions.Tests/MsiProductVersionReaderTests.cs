using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AutopilotMonitor.Functions.Services.Ime;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The MSI product-version reader is what stops the IME archiver from filing the wrong
/// build under a version folder (2026-08-29: Microsoft's versionless CDN hosts serve
/// different rings, so "new version == what the canonical URL serves" was false). These
/// tests build minimal OLE compound files in memory — mini-stream and regular-sector
/// layouts, long (>64 KB) pool strings, deleted-string holes, 3-byte string refs — and
/// pin the failure contract (never throw, null on anything unreadable). The local sweep
/// over real IME packages is the oracle for the string-pool id convention.
/// </summary>
public class MsiProductVersionReaderTests
{
    // =========================================================================
    // Stream-name decoding
    // =========================================================================

    [Theory]
    [InlineData("!_StringPool")]
    [InlineData("!_StringData")]
    [InlineData("!Property")]
    [InlineData("!ActionText")]
    [InlineData("Binary.x")]
    public void DecodeStreamName_RoundTripsEncoder(string name)
    {
        Assert.Equal(name, MsiProductVersionReader.DecodeStreamName(MsiBuilder.EncodeName(name)));
    }

    [Fact]
    public void DecodeStreamName_LeavesPlainNamesAlone()
    {
        Assert.Equal("SummaryInformation", MsiProductVersionReader.DecodeStreamName("SummaryInformation"));
    }

    // =========================================================================
    // Synthetic packages
    // =========================================================================

    [Fact]
    public void MiniStreamPackage_ReadsProductVersion()
    {
        var msi = MsiBuilder.Build(new Dictionary<string, string>
        {
            ["ProductName"] = "Microsoft Intune Management Extension",
            ["ProductVersion"] = "1.105.103.0",
            ["Manufacturer"] = "Microsoft Corporation",
        });

        Assert.Equal("1.105.103.0", MsiProductVersionReader.TryReadProductVersion(msi));
    }

    [Fact]
    public void RegularSectorPackage_WithLongStringAndHoleBeforeVersion_KeepsIdsAligned()
    {
        // _StringData grows past the 4 KB mini-stream cutoff AND the pool contains a >64 KB
        // string (two pool entries, one id) plus a deleted-string hole before ProductVersion —
        // every later id must still resolve to the right text.
        var msi = MsiBuilder.Build(
            new Dictionary<string, string>
            {
                ["ProductVersion"] = "1.104.102.0",
                ["ProductName"] = "IME",
            },
            extraStringsBeforeProperties: new[] { new string('x', 70_000), MsiBuilder.Hole, "filler" });

        var table = MsiProductVersionReader.ReadPropertyTable(msi);

        Assert.NotNull(table);
        Assert.Equal("1.104.102.0", table!["ProductVersion"]);
        Assert.Equal("IME", table["ProductName"]);
    }

    [Fact]
    public void ThreeByteStringRefs_AreHonoured()
    {
        var msi = MsiBuilder.Build(
            new Dictionary<string, string> { ["ProductVersion"] = "1.99.101.0" },
            threeByteRefs: true);

        Assert.Equal("1.99.101.0", MsiProductVersionReader.TryReadProductVersion(msi));
    }

    [Fact]
    public void MissingProductVersionProperty_ReturnsNull()
    {
        var msi = MsiBuilder.Build(new Dictionary<string, string> { ["ProductName"] = "IME" });

        Assert.Null(MsiProductVersionReader.TryReadProductVersion(msi));
    }

    [Fact]
    public void MissingPropertyStream_ReturnsNull()
    {
        var msi = MsiBuilder.Build(new Dictionary<string, string> { ["ProductVersion"] = "1.0.0.0" }, omitPropertyTable: true);

        Assert.Null(MsiProductVersionReader.TryReadProductVersion(msi));
    }

    [Fact]
    public void NotACompoundFile_ReturnsNull()
    {
        var random = new byte[64 * 1024];
        new Random(42).NextBytes(random);

        Assert.Null(MsiProductVersionReader.TryReadProductVersion(new MemoryStream(random)));
        Assert.Null(MsiProductVersionReader.TryReadProductVersion(new MemoryStream(Array.Empty<byte>())));
        Assert.Null(MsiProductVersionReader.TryReadProductVersion(new MemoryStream(Encoding.ASCII.GetBytes("MZ not an msi"))));
    }

    [Fact]
    public void TruncatedPackage_ReturnsNull_NeverThrows()
    {
        var full = ((MemoryStream)MsiBuilder.Build(new Dictionary<string, string> { ["ProductVersion"] = "1.0.0.0" })).ToArray();

        foreach (var cut in new[] { 8, 511, 512, 600, 1024, full.Length / 2, full.Length - 1 })
        {
            var truncated = new MemoryStream(full.Take(cut).ToArray());
            var ex = Record.Exception(() => MsiProductVersionReader.TryReadProductVersion(truncated));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void CyclicFatChain_ReturnsNull_NeverHangs()
    {
        var bytes = ((MemoryStream)MsiBuilder.Build(new Dictionary<string, string> { ["ProductVersion"] = "1.0.0.0" })).ToArray();
        // FAT lives in sector 0 (offset 512). Point the directory sector's FAT entry back at itself.
        var firstDirSector = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x30, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(512 + (int)firstDirSector * 4, 4), firstDirSector);

        Assert.Null(MsiProductVersionReader.TryReadProductVersion(new MemoryStream(bytes)));
    }

    [Fact]
    public void NonSeekableStream_ReturnsNull()
    {
        using var stream = new NonSeekableStream(((MemoryStream)MsiBuilder.Build(new Dictionary<string, string> { ["ProductVersion"] = "1.0.0.0" })).ToArray());

        Assert.Null(MsiProductVersionReader.TryReadProductVersion(stream));
    }

    // =========================================================================
    // Real packages (local oracle — the archived IME MSIs are not checked in)
    // =========================================================================

    /// <summary>
    /// Sweeps every <c>ime-files/&lt;version&gt;/IntuneWindowsAgent.msi</c> in the repo checkout
    /// (gitignored local scratch, populated by an operator tool) and asserts the reader
    /// returns exactly the folder's version. Silently passes when the folder is absent (CI)
    /// — the synthetic tests above carry the contract there; this one pins the string-pool
    /// id convention against Microsoft's real packages.
    /// </summary>
    [Fact]
    public void LocalImeArchive_EveryPackageReportsItsFolderVersion()
    {
        var root = FindRepoRoot();
        var archive = root is null ? null : Path.Combine(root, "ime-files");
        if (archive is null || !Directory.Exists(archive)) return;

        var checkedCount = 0;
        foreach (var dir in Directory.GetDirectories(archive))
        {
            var msiPath = Path.Combine(dir, "IntuneWindowsAgent.msi");
            if (!File.Exists(msiPath)) continue;
            var expected = Path.GetFileName(dir);

            using var stream = File.OpenRead(msiPath);
            var table = MsiProductVersionReader.ReadPropertyTable(stream);

            Assert.NotNull(table);
            Assert.Equal(expected, table!["ProductVersion"]);
            Assert.Contains("Intune", table["ProductName"]);
            checkedCount++;
        }
        Assert.True(checkedCount > 0, "ime-files exists but holds no packages");
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public NonSeekableStream(byte[] data) : base(data) { }
        public override bool CanSeek => false;
    }

    // =========================================================================
    // Minimal MSI (OLE compound file v3, 512-byte sectors) builder
    // =========================================================================

    internal static class MsiBuilder
    {
        public const string Hole = "\0<hole>";
        private const int SectorSize = 512;
        private const int MiniSectorSize = 64;
        private const int MiniCutoff = 4096;
        private const uint EndOfChain = 0xFFFFFFFE;
        private const uint FreeSector = 0xFFFFFFFF;
        private const uint NoStream = 0xFFFFFFFF;
        private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._";

        public static string EncodeName(string name)
        {
            var sb = new StringBuilder();
            var i = 0;
            if (name.StartsWith('!'))
            {
                sb.Append((char)0x4840);
                i = 1;
            }
            for (; i < name.Length; i += 2)
            {
                var a = Alphabet.IndexOf(name[i]);
                if (a < 0) { sb.Append(name[i]); i--; continue; }
                if (i + 1 < name.Length && Alphabet.IndexOf(name[i + 1]) >= 0)
                    sb.Append((char)(0x3800 + a + (Alphabet.IndexOf(name[i + 1]) << 6)));
                else
                    sb.Append((char)(0x4800 + a));
            }
            return sb.ToString();
        }

        public static Stream Build(
            Dictionary<string, string> properties,
            string[]? extraStringsBeforeProperties = null,
            bool threeByteRefs = false,
            bool omitPropertyTable = false)
        {
            // ---- string pool ----
            var pool = new List<byte>();
            var data = new List<byte>();
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            var codepage = 1252;
            pool.AddRange(BitConverter.GetBytes((ushort)codepage));
            pool.AddRange(BitConverter.GetBytes((ushort)(threeByteRefs ? 0x8000 : 0)));
            var nextId = 1;

            void AddString(string s)
            {
                if (s == Hole)
                {
                    pool.AddRange(new byte[4]); // len 0, refs 0 — deleted string
                    nextId++;
                    return;
                }
                var bytes = Encoding.Latin1.GetBytes(s);
                if (bytes.Length <= 0xFFFF)
                {
                    pool.AddRange(BitConverter.GetBytes((ushort)bytes.Length));
                    pool.AddRange(BitConverter.GetBytes((ushort)1));
                    ids[s] = nextId++;
                }
                else
                {
                    pool.AddRange(BitConverter.GetBytes((ushort)0));
                    pool.AddRange(BitConverter.GetBytes((ushort)1));
                    pool.AddRange(BitConverter.GetBytes((uint)bytes.Length));
                    ids[s] = nextId;
                    nextId += 2; // the length entry consumes a pool slot but no id
                }
                data.AddRange(bytes);
            }

            foreach (var s in extraStringsBeforeProperties ?? Array.Empty<string>()) AddString(s);
            foreach (var kv in properties.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!ids.ContainsKey(kv.Key)) AddString(kv.Key);
                if (!ids.ContainsKey(kv.Value)) AddString(kv.Value);
            }

            // ---- Property table (column-major) ----
            var refBytes = threeByteRefs ? 3 : 2;
            var table = new List<byte>();
            var ordered = properties.OrderBy(k => k.Key, StringComparer.Ordinal).ToList();
            foreach (var kv in ordered) WriteRef(table, ids[kv.Key], refBytes);
            foreach (var kv in ordered) WriteRef(table, ids[kv.Value], refBytes);

            var streams = new List<(string Name, byte[] Bytes)>
            {
                (EncodeName("!_StringPool"), pool.ToArray()),
                (EncodeName("!_StringData"), data.ToArray()),
                ("SummaryInformation", new byte[100]),
            };
            if (!omitPropertyTable) streams.Add((EncodeName("!Property"), table.ToArray()));

            return Assemble(streams);
        }

        private static void WriteRef(List<byte> target, int id, int refBytes)
        {
            target.Add((byte)(id & 0xFF));
            target.Add((byte)((id >> 8) & 0xFF));
            if (refBytes == 3) target.Add((byte)((id >> 16) & 0xFF));
        }

        private static Stream Assemble(List<(string Name, byte[] Bytes)> streams)
        {
            // Mini stream: every stream < cutoff, padded to 64-byte mini sectors, one mini-FAT chain each.
            var miniStream = new List<byte>();
            var miniFat = new List<uint>();
            var miniStarts = new Dictionary<int, uint>();
            for (var i = 0; i < streams.Count; i++)
            {
                if (streams[i].Bytes.Length >= MiniCutoff) continue;
                var count = Math.Max(1, (streams[i].Bytes.Length + MiniSectorSize - 1) / MiniSectorSize);
                miniStarts[i] = (uint)miniFat.Count;
                for (var s = 0; s < count; s++)
                    miniFat.Add(s == count - 1 ? EndOfChain : (uint)(miniFat.Count + 1));
                miniStream.AddRange(streams[i].Bytes);
                miniStream.AddRange(new byte[count * MiniSectorSize - streams[i].Bytes.Length]);
            }

            // Regular-sector payloads: directory, mini-FAT, mini stream, large streams.
            var payloads = new List<byte[]>(); // index → bytes (each occupies ceil(len/512) sectors)
            var dirIndex = payloads.Count; payloads.Add(Array.Empty<byte>()); // filled later
            var miniFatIndex = payloads.Count; payloads.Add(miniFat.SelectMany(BitConverter.GetBytes).ToArray());
            var miniStreamIndex = payloads.Count; payloads.Add(miniStream.ToArray());
            var largeIndex = new Dictionary<int, int>();
            for (var i = 0; i < streams.Count; i++)
            {
                if (streams[i].Bytes.Length < MiniCutoff) continue;
                largeIndex[i] = payloads.Count;
                payloads.Add(streams[i].Bytes);
            }

            // Directory entries: root + one per stream.
            var dir = new byte[(streams.Count + 1) * 128];
            static int SectorsFor(int len) => Math.Max(1, (len + SectorSize - 1) / SectorSize);
            payloads[dirIndex] = dir;

            // Sector map: FAT sectors first, then payloads in order.
            var payloadSectors = payloads.Sum(p => SectorsFor(p.Length));
            var fatSectors = 1;
            while ((fatSectors + payloadSectors) > fatSectors * (SectorSize / 4)) fatSectors++;
            var starts = new uint[payloads.Count];
            var cursor = (uint)fatSectors;
            for (var i = 0; i < payloads.Count; i++)
            {
                starts[i] = cursor;
                cursor += (uint)SectorsFor(payloads[i].Length);
            }
            var totalSectors = (int)cursor;

            // FAT
            var fat = new uint[fatSectors * (SectorSize / 4)];
            Array.Fill(fat, FreeSector);
            for (var i = 0; i < fatSectors; i++) fat[i] = 0xFFFFFFFD; // FATSECT
            for (var i = 0; i < payloads.Count; i++)
            {
                var n = SectorsFor(payloads[i].Length);
                for (var s = 0; s < n; s++)
                    fat[starts[i] + s] = s == n - 1 ? EndOfChain : starts[i] + (uint)s + 1;
            }

            // Directory content
            WriteDirEntry(dir, 0, "Root Entry", type: 5, left: NoStream, right: NoStream, child: 1,
                start: starts[miniStreamIndex], size: (uint)miniStream.Count);
            for (var i = 0; i < streams.Count; i++)
            {
                var (start, size) = streams[i].Bytes.Length >= MiniCutoff
                    ? (starts[largeIndex[i]], (uint)streams[i].Bytes.Length)
                    : (miniStarts[i], (uint)streams[i].Bytes.Length);
                WriteDirEntry(dir, i + 1, streams[i].Name, type: 2, left: NoStream,
                    right: i + 1 < streams.Count ? (uint)(i + 2) : NoStream, child: NoStream, start, size);
            }

            // Header
            var header = new byte[SectorSize];
            new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x18, 2), 0x3E); // minor
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1A, 2), 3);    // major
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1C, 2), 0xFFFE);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1E, 2), 9);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x20, 2), 6);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x2C, 4), (uint)fatSectors);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x30, 4), starts[dirIndex]);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x38, 4), MiniCutoff);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x3C, 4), starts[miniFatIndex]);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x40, 4), (uint)SectorsFor(payloads[miniFatIndex].Length));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x44, 4), EndOfChain);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x48, 4), 0);
            for (var i = 0; i < 109; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x4C + i * 4, 4), i < fatSectors ? (uint)i : FreeSector);

            // Assemble file
            var file = new byte[SectorSize + totalSectors * SectorSize];
            header.CopyTo(file, 0);
            for (var i = 0; i < fatSectors; i++)
                for (var j = 0; j < SectorSize / 4; j++)
                    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(SectorSize + i * SectorSize + j * 4, 4), fat[i * (SectorSize / 4) + j]);
            for (var i = 0; i < payloads.Count; i++)
                payloads[i].CopyTo(file, SectorSize + (int)starts[i] * SectorSize);

            return new MemoryStream(file);
        }

        private static void WriteDirEntry(byte[] dir, int index, string name, byte type,
            uint left, uint right, uint child, uint start, uint size)
        {
            var off = index * 128;
            var nameBytes = Encoding.Unicode.GetBytes(name);
            nameBytes.CopyTo(dir, off);
            BinaryPrimitives.WriteUInt16LittleEndian(dir.AsSpan(off + 64, 2), (ushort)(nameBytes.Length + 2));
            dir[off + 66] = type;
            dir[off + 67] = 1; // black
            BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(off + 68, 4), left);
            BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(off + 72, 4), right);
            BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(off + 76, 4), child);
            BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(off + 116, 4), start);
            BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(off + 120, 4), size);
        }
    }
}
