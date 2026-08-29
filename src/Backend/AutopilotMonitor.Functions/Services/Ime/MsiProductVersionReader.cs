using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AutopilotMonitor.Functions.Services.Ime
{
    /// <summary>
    /// Reads the <c>ProductVersion</c> property out of a Windows Installer package without
    /// Windows Installer: an MSI is an OLE Compound File (CFB) whose tables are streams, so
    /// the reader walks the CFB directory, decodes the MSI stream-name encoding, loads the
    /// shared string pool (<c>_StringPool</c> + <c>_StringData</c>) and resolves the two
    /// string-ref columns of the <c>Property</c> table. Pure managed code — it runs on the
    /// Linux Function host where <c>msiexec</c>/<c>MsiGetProperty</c> do not exist.
    /// <para>
    /// Contract: never throws for malformed input — every structural problem yields
    /// <c>null</c> ("could not read"), which the archiver treats as a version mismatch.
    /// Bounded work: sector chains are capped at the FAT length, so a cyclic FAT cannot loop.
    /// </para>
    /// </summary>
    internal static class MsiProductVersionReader
    {
        private const string ProductVersionProperty = "ProductVersion";
        // Decoded names keep the '!' table-prefix marker (0x4840) — tables are "!<name>".
        private const string StringPoolStream = "!_StringPool";
        private const string StringDataStream = "!_StringData";
        private const string PropertyTableStream = "!Property";

        private const uint EndOfChain = 0xFFFFFFFE;
        private const uint FreeSector = 0xFFFFFFFF;
        private const int DirectoryEntrySize = 128;
        private const int HeaderSize = 512;

        private static readonly byte[] CfbSignature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

        /// <summary>
        /// Base-64 alphabet of the MSI stream-name encoding (Windows Installer stores table
        /// names as UTF-16 code points in the 0x3800–0x4840 range, two characters per unit).
        /// </summary>
        private const string NameAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._";

        /// <summary>
        /// Returns the package's <c>ProductVersion</c> (e.g. <c>1.105.103.0</c>) or <c>null</c>
        /// when the stream is not a readable MSI. <paramref name="msi"/> must be seekable; the
        /// position is not restored.
        /// </summary>
        public static string? TryReadProductVersion(Stream msi)
        {
            if (msi is null || !msi.CanSeek || !msi.CanRead) return null;
            try
            {
                var properties = ReadPropertyTable(msi);
                return properties is not null && properties.TryGetValue(ProductVersionProperty, out var version)
                    && !string.IsNullOrWhiteSpace(version)
                    ? version.Trim()
                    : null;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or IndexOutOfRangeException
                                       or OverflowException or InvalidDataException or DecoderFallbackException)
            {
                return null;
            }
        }

        /// <summary>
        /// The whole <c>Property</c> table (property → value). Internal for tests that cross-check
        /// several properties against a real package; <c>null</c> when unreadable.
        /// </summary>
        internal static Dictionary<string, string>? ReadPropertyTable(Stream msi)
        {
            var cfb = CompoundFile.Open(msi);
            if (cfb is null) return null;

            var pool = cfb.ReadStream(StringPoolStream);
            var data = cfb.ReadStream(StringDataStream);
            var table = cfb.ReadStream(PropertyTableStream);
            if (pool is null || data is null || table is null) return null;

            var strings = DecodeStringPool(pool, data, out var refBytes);
            if (strings is null) return null;

            // Column-major: all "Property" refs first, then all "Value" refs.
            var rowSize = 2 * refBytes;
            if (table.Length < rowSize || table.Length % rowSize != 0) return null;
            var rows = table.Length / rowSize;

            var result = new Dictionary<string, string>(rows, StringComparer.Ordinal);
            for (var row = 0; row < rows; row++)
            {
                var keyRef = ReadRef(table, row * refBytes, refBytes);
                var valueRef = ReadRef(table, (rows + row) * refBytes, refBytes);
                if (!strings.TryGetValue(keyRef, out var key) || !strings.TryGetValue(valueRef, out var value))
                    continue;
                result[key] = value;
            }
            return result;
        }

        /// <summary>Decodes one MSI stream name (directory entry name → table name).</summary>
        internal static string DecodeStreamName(string encoded)
        {
            var sb = new StringBuilder(encoded.Length * 2);
            foreach (var ch in encoded)
            {
                if (ch >= 0x3800 && ch < 0x4800)
                {
                    var v = ch - 0x3800;
                    sb.Append(NameAlphabet[v & 0x3F]).Append(NameAlphabet[(v >> 6) & 0x3F]);
                }
                else if (ch >= 0x4800 && ch < 0x4840)
                {
                    sb.Append(NameAlphabet[ch - 0x4800]);
                }
                else if (ch == 0x4840)
                {
                    sb.Append('!'); // table prefix marker
                }
                else
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// String pool layout: 4-byte entries (ushort length, ushort refcount). Entry 0 holds the
        /// codepage; bit 15 of its refcount word selects 3-byte string refs in tables. A zero
        /// length with a non-zero refcount marks a long string whose 32-bit length follows in
        /// the next entry (which therefore yields no string id of its own). String ids are
        /// 1-based pool positions.
        /// </summary>
        private static Dictionary<int, string>? DecodeStringPool(byte[] pool, byte[] data, out int refBytes)
        {
            refBytes = 2;
            if (pool.Length < 4 || pool.Length % 4 != 0) return null;

            var codepage = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(0, 2));
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(2, 2));
            if ((flags & 0x8000) != 0) refBytes = 3;
            var encoding = ResolveEncoding(codepage | ((flags & 0x7FFF) << 16));

            var strings = new Dictionary<int, string>(pool.Length / 4);
            var entries = pool.Length / 4;
            var offset = 0;
            for (var i = 1; i < entries; i++)
            {
                int len = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(i * 4, 2));
                int refs = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(i * 4 + 2, 2));
                var id = i;
                if (len == 0)
                {
                    if (refs == 0) continue; // hole (deleted string) — id stays unused
                    if (i + 1 >= entries) return null;
                    i++;
                    len = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(pool.AsSpan(i * 4, 4)));
                }
                if (len < 0 || offset + len > data.Length) return null;
                strings[id] = encoding.GetString(data, offset, len);
                offset += len;
            }
            return strings;
        }

        private static Encoding ResolveEncoding(int codepage)
        {
            if (codepage == 0 || codepage == 65001) return Encoding.UTF8;
            if (codepage == 1252 || codepage == 1200) return Encoding.Latin1; // no CodePagesEncodingProvider needed for ASCII-range properties
            try { return Encoding.GetEncoding(codepage); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.Latin1; }
        }

        private static int ReadRef(byte[] table, int offset, int refBytes)
            => refBytes == 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(offset, 2))
                : table[offset] | (table[offset + 1] << 8) | (table[offset + 2] << 16);

        /// <summary>
        /// Minimal read-only OLE Compound File (v3/v4) reader: header, DIFAT → FAT, directory,
        /// mini-FAT/mini-stream. Only what the MSI property lookup needs.
        /// </summary>
        private sealed class CompoundFile
        {
            private readonly Stream _stream;
            private readonly int _sectorSize;
            private readonly int _miniSectorSize;
            private readonly uint _miniStreamCutoff;
            private readonly uint[] _fat;
            private readonly uint[] _miniFat;
            private readonly byte[] _miniStream;
            private readonly List<(string Name, uint StartSector, uint Size)> _streams;

            private CompoundFile(Stream stream, int sectorSize, int miniSectorSize, uint miniStreamCutoff,
                uint[] fat, uint[] miniFat, byte[] miniStream, List<(string, uint, uint)> streams)
            {
                _stream = stream;
                _sectorSize = sectorSize;
                _miniSectorSize = miniSectorSize;
                _miniStreamCutoff = miniStreamCutoff;
                _fat = fat;
                _miniFat = miniFat;
                _miniStream = miniStream;
                _streams = streams;
            }

            public static CompoundFile? Open(Stream stream)
            {
                if (stream.Length < HeaderSize) return null;
                stream.Position = 0;
                var header = new byte[HeaderSize];
                if (!ReadExactly(stream, header)) return null;
                if (!header.AsSpan(0, 8).SequenceEqual(CfbSignature)) return null;

                var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x1E, 2));
                var miniShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x20, 2));
                if (sectorShift is < 7 or > 12 || miniShift is < 2 or > 12 || miniShift > sectorShift) return null;
                var sectorSize = 1 << sectorShift;
                var miniSectorSize = 1 << miniShift;

                var fatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x2C, 4));
                var firstDirSector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x30, 4));
                var miniCutoff = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x38, 4));
                var firstMiniFatSector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x3C, 4));
                var firstDifatSector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x44, 4));
                var difatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x48, 4));

                var totalSectors = Math.Max(0, (stream.Length + sectorSize - 1) / sectorSize - 1);
                if (fatSectorCount > totalSectors || difatSectorCount > totalSectors) return null;

                // DIFAT: 109 header entries, then a chain of DIFAT sectors.
                var fatSectors = new List<uint>((int)fatSectorCount);
                for (var i = 0; i < 109 && fatSectors.Count < fatSectorCount; i++)
                {
                    var s = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x4C + i * 4, 4));
                    if (s >= EndOfChain) break;
                    fatSectors.Add(s);
                }
                var entriesPerSector = sectorSize / 4;
                var difat = firstDifatSector;
                for (var d = 0; d < difatSectorCount && difat < EndOfChain; d++)
                {
                    var sector = ReadSector(stream, sectorSize, difat);
                    if (sector is null) return null;
                    for (var i = 0; i < entriesPerSector - 1 && fatSectors.Count < fatSectorCount; i++)
                    {
                        var s = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(i * 4, 4));
                        if (s >= EndOfChain) break;
                        fatSectors.Add(s);
                    }
                    difat = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan((entriesPerSector - 1) * 4, 4));
                }

                var fat = new uint[fatSectors.Count * entriesPerSector];
                for (var i = 0; i < fatSectors.Count; i++)
                {
                    var sector = ReadSector(stream, sectorSize, fatSectors[i]);
                    if (sector is null) return null;
                    for (var j = 0; j < entriesPerSector; j++)
                        fat[i * entriesPerSector + j] = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(j * 4, 4));
                }

                var directory = ReadChain(stream, sectorSize, fat, firstDirSector, uint.MaxValue);
                if (directory is null || directory.Length < DirectoryEntrySize) return null;

                // Root entry (index 0) owns the mini stream; MSI table streams live at root level,
                // so a flat scan of stream entries is sufficient — no tree walk needed.
                var streams = new List<(string, uint, uint)>();
                uint rootStart = 0, rootSize = 0;
                for (var off = 0; off + DirectoryEntrySize <= directory.Length; off += DirectoryEntrySize)
                {
                    var type = directory[off + 66];
                    var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(off + 64, 2));
                    if (nameLen < 2 || nameLen > 64) continue;
                    var name = Encoding.Unicode.GetString(directory, off, nameLen - 2); // strip NUL
                    var start = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(off + 116, 4));
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(off + 120, 4)); // low dword suffices (< 2 GB)
                    if (off == 0 && type == 5)
                    {
                        rootStart = start;
                        rootSize = size;
                    }
                    else if (type == 2)
                    {
                        streams.Add((DecodeStreamName(name), start, size));
                    }
                }

                uint[] miniFat = Array.Empty<uint>();
                byte[] miniStream = Array.Empty<byte>();
                if (firstMiniFatSector < EndOfChain && rootSize > 0)
                {
                    var miniFatBytes = ReadChain(stream, sectorSize, fat, firstMiniFatSector, uint.MaxValue);
                    if (miniFatBytes is null) return null;
                    miniFat = new uint[miniFatBytes.Length / 4];
                    for (var i = 0; i < miniFat.Length; i++)
                        miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFatBytes.AsSpan(i * 4, 4));
                    miniStream = ReadChain(stream, sectorSize, fat, rootStart, rootSize) ?? Array.Empty<byte>();
                }

                return new CompoundFile(stream, sectorSize, miniSectorSize, miniCutoff, fat, miniFat, miniStream, streams);
            }

            /// <summary>Reads a root-level stream by decoded MSI name; <c>null</c> when absent or corrupt.</summary>
            public byte[]? ReadStream(string decodedName)
            {
                foreach (var (name, start, size) in _streams)
                {
                    if (!string.Equals(name, decodedName, StringComparison.Ordinal)) continue;
                    if (size == 0) return Array.Empty<byte>();
                    return size < _miniStreamCutoff
                        ? ReadMiniChain(start, size)
                        : ReadChain(_stream, _sectorSize, _fat, start, size);
                }
                return null;
            }

            private byte[]? ReadMiniChain(uint start, uint size)
            {
                var result = new byte[size];
                var written = 0;
                var sector = start;
                var hops = 0;
                while (sector < EndOfChain && written < size)
                {
                    if (++hops > _miniFat.Length) return null; // cyclic chain
                    var offset = (long)sector * _miniSectorSize;
                    var take = (int)Math.Min(_miniSectorSize, size - written);
                    if (offset + take > _miniStream.Length) return null;
                    Buffer.BlockCopy(_miniStream, (int)offset, result, written, take);
                    written += take;
                    if (sector >= _miniFat.Length) return null;
                    sector = _miniFat[sector];
                }
                return written == size ? result : null;
            }

            /// <summary>Follows a regular FAT chain; <paramref name="size"/> = uint.MaxValue reads the whole chain.</summary>
            private static byte[]? ReadChain(Stream stream, int sectorSize, uint[] fat, uint start, uint size)
            {
                using var ms = new MemoryStream();
                var sector = start;
                var hops = 0;
                while (sector < EndOfChain && ms.Length < size)
                {
                    if (++hops > fat.Length) return null; // cyclic chain
                    var bytes = ReadSector(stream, sectorSize, sector);
                    if (bytes is null) return null;
                    var take = (int)Math.Min(sectorSize, size - ms.Length);
                    ms.Write(bytes, 0, take);
                    if (sector >= fat.Length) return null;
                    sector = fat[sector];
                }
                if (size != uint.MaxValue && ms.Length != size) return null;
                return ms.ToArray();
            }

            private static byte[]? ReadSector(Stream stream, int sectorSize, uint sector)
            {
                if (sector >= FreeSector - 4) return null;
                // Sector 0 starts one full sector into the file: the 512-byte header is padded
                // to the sector size (matters for v4 packages with 4096-byte sectors).
                var offset = ((long)sector + 1) * sectorSize;
                if (offset + sectorSize > stream.Length) return null;
                stream.Position = offset;
                var buffer = new byte[sectorSize];
                return ReadExactly(stream, buffer) ? buffer : null;
            }

            private static bool ReadExactly(Stream stream, byte[] buffer)
            {
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = stream.Read(buffer, total, buffer.Length - total);
                    if (read <= 0) return false;
                    total += read;
                }
                return true;
            }
        }
    }
}
