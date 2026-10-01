using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather.Collectors
{
    /// <summary>
    /// Text encoding of a log file as the logparser reads it. Chosen from the byte order mark at
    /// the start of the file in <see cref="StreamReader"/>'s detection order; without one the file
    /// is UTF-8, which is also how ANSI logs were always read (invalid bytes decode to U+FFFD).
    /// </summary>
    internal sealed class LogTextEncoding
    {
        public static readonly LogTextEncoding Utf8 = new LogTextEncoding("utf-8", new UTF8Encoding(false), 1, 0);
        public static readonly LogTextEncoding Utf8WithBom = new LogTextEncoding("utf-8 with bom", new UTF8Encoding(false), 1, 3);
        public static readonly LogTextEncoding Utf16LittleEndian = new LogTextEncoding("utf-16le", new UnicodeEncoding(false, false), 2, 2);
        public static readonly LogTextEncoding Utf16BigEndian = new LogTextEncoding("utf-16be", new UnicodeEncoding(true, false), 2, 2);
        public static readonly LogTextEncoding Utf32LittleEndian = new LogTextEncoding("utf-32le", new UTF32Encoding(false, false), 4, 4);
        public static readonly LogTextEncoding Utf32BigEndian = new LogTextEncoding("utf-32be", new UTF32Encoding(true, false), 4, 4);

        private LogTextEncoding(string name, Encoding encoding, int unitSize, int byteOrderMarkLength)
        {
            Name = name;
            Encoding = encoding;
            UnitSize = unitSize;
            ByteOrderMarkLength = byteOrderMarkLength;
            LineFeed = encoding.GetBytes("\n");
            CarriageReturn = encoding.GetBytes("\r");
        }

        public string Name { get; }
        public Encoding Encoding { get; }

        /// <summary>Bytes per code unit; every line boundary lies on a unit boundary.</summary>
        public int UnitSize { get; }

        /// <summary>Length of the byte order mark at offset 0 — skipped, never part of a line.</summary>
        public int ByteOrderMarkLength { get; }

        public byte[] LineFeed { get; }
        public byte[] CarriageReturn { get; }

        public static LogTextEncoding Detect(byte[] head)
        {
            if (StartsWith(head, 0xEF, 0xBB, 0xBF)) return Utf8WithBom;
            if (StartsWith(head, 0xFF, 0xFE, 0x00, 0x00)) return Utf32LittleEndian;
            if (StartsWith(head, 0xFF, 0xFE)) return Utf16LittleEndian;
            if (StartsWith(head, 0xFE, 0xFF)) return Utf16BigEndian;
            if (StartsWith(head, 0x00, 0x00, 0xFE, 0xFF)) return Utf32BigEndian;
            return Utf8;
        }

        /// <summary>The last code-unit boundary at or before <paramref name="position"/>.</summary>
        public long AlignDown(long position)
        {
            if (position <= ByteOrderMarkLength) return position < ByteOrderMarkLength ? 0 : position;
            return position - (position - ByteOrderMarkLength) % UnitSize;
        }

        private static bool StartsWith(byte[] bytes, params byte[] prefix)
        {
            if (bytes.Length < prefix.Length) return false;
            for (var i = 0; i < prefix.Length; i++)
                if (bytes[i] != prefix[i]) return false;
            return true;
        }
    }

    /// <summary>
    /// The first bytes of a log file: they carry its byte order mark and identify the file, so a
    /// file replaced under the same name is recognised even when it is already longer than the
    /// position read so far.
    /// </summary>
    internal sealed class LogFileHead
    {
        internal const int MaxBytes = 4096;

        private readonly byte[] _bytes;

        private LogFileHead(byte[] bytes)
        {
            _bytes = bytes;
        }

        /// <summary>Up to <see cref="MaxBytes"/> from offset 0. Leaves the stream at an undefined position.</summary>
        public static LogFileHead Read(Stream stream)
        {
            stream.Seek(0, SeekOrigin.Begin);
            var buffer = new byte[(int)Math.Min(MaxBytes, stream.Length)];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < buffer.Length) Array.Resize(ref buffer, read);
            return new LogFileHead(buffer);
        }

        internal static LogFileHead FromBytes(byte[] bytes) => new LogFileHead(bytes);

        public int Length => _bytes.Length;

        public LogTextEncoding Encoding => LogTextEncoding.Detect(_bytes);

        /// <summary>SHA-256 of the first <paramref name="length"/> bytes, Base64.</summary>
        public string Hash(int length)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(_bytes, 0, length));
        }
    }

    /// <summary>
    /// Byte-exact line reader for the logparser collector. <see cref="Position"/> is the file
    /// offset right behind the last line returned, so a caller that stops after any line can
    /// resume exactly there — <see cref="StreamReader"/> only exposes its read-ahead boundary.
    /// <para>
    /// Line ends are those of <see cref="StreamReader.ReadLine"/>: "\r\n", "\n" and a lone "\r",
    /// in code units of the file's <see cref="LogTextEncoding"/>. A "\n" directly after the start
    /// position is skipped when the unit before it is "\r": the previous run ended on a "\r" whose
    /// "\n" had not been written yet. A line at end of file without a line end is returned with
    /// <see cref="LastLineTerminated"/> false; the caller decides whether the writer is still in
    /// it. A line longer than the cap is consumed without being kept: <see cref="ReadLine"/>
    /// returns an empty string and sets <see cref="LastLineTruncated"/>.
    /// </para>
    /// </summary>
    internal sealed class LogLineReader
    {
        private const int ReadBufferSize = 64 * 1024;

        private readonly Stream _stream;
        private readonly LogTextEncoding _encoding;
        private readonly int _unit;
        private readonly int _maxLineBytes;
        private readonly byte[] _buffer = new byte[ReadBufferSize];
        private int _pos;
        private int _len;
        private long _bufferOffset;
        private bool _eof;
        private bool _skipLineFeedAfterCarriageReturn;

        private byte[] _line = new byte[4096];
        private int _lineLength;

        /// <param name="stream">Seekable stream positioned at a code-unit boundary.</param>
        public LogLineReader(Stream stream, LogTextEncoding encoding, int maxLineBytes)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (encoding == null) throw new ArgumentNullException(nameof(encoding));
            if (maxLineBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxLineBytes));
            _stream = stream;
            _encoding = encoding;
            _unit = encoding.UnitSize;
            _maxLineBytes = maxLineBytes;
            _bufferOffset = stream.Position;
            _skipLineFeedAfterCarriageReturn = PrecededByCarriageReturn();
        }

        /// <summary>File offset of the next unread byte, exact.</summary>
        public long Position => _bufferOffset + _pos;

        /// <summary>File offset of the first byte of the line most recently returned.</summary>
        public long LastLineStart { get; private set; }

        /// <summary>False for a line that reached end of file without a line end.</summary>
        public bool LastLineTerminated { get; private set; }

        /// <summary>True when the last line was longer than the cap; it was consumed, not returned.</summary>
        public bool LastLineTruncated { get; private set; }

        /// <summary>The next line without its line end, or null at end of file.</summary>
        public string ReadLine()
        {
            if (Position == 0 && _encoding.ByteOrderMarkLength > 0 && Ensure(_encoding.ByteOrderMarkLength))
                _pos += _encoding.ByteOrderMarkLength;

            if (_skipLineFeedAfterCarriageReturn)
            {
                _skipLineFeedAfterCarriageReturn = false;
                if (Ensure(_unit) && UnitAt(_pos, _encoding.LineFeed))
                    _pos += _unit;
            }

            LastLineStart = Position;
            LastLineTerminated = false;
            LastLineTruncated = false;
            _lineLength = 0;

            while (true)
            {
                if (!Ensure(_unit))
                {
                    // End of file. A partial code unit (a writer mid-character) belongs to the line.
                    var rest = _len - _pos;
                    if (rest > 0)
                    {
                        Append(_pos, rest);
                        _pos = _len;
                    }
                    return Position > LastLineStart ? Decode() : null;
                }

                // Scan the whole code units in the buffer for the next line end and take the text
                // before it in one copy.
                var end = _pos + (_len - _pos) / _unit * _unit;
                var scan = _pos;
                while (scan < end && !IsLineEnd(scan))
                    scan += _unit;
                if (scan > _pos)
                {
                    Append(_pos, scan - _pos);
                    _pos = scan;
                }
                if (scan == end)
                    continue;   // no line end in the buffer yet — refill

                var carriageReturn = !UnitAt(_pos, _encoding.LineFeed);
                _pos += _unit;
                if (carriageReturn && Ensure(_unit) && UnitAt(_pos, _encoding.LineFeed))
                    _pos += _unit;
                LastLineTerminated = true;
                return Decode();
            }
        }

        private bool IsLineEnd(int offset)
        {
            if (_unit == 1)
            {
                var b = _buffer[offset];
                return b == (byte)'\n' || b == (byte)'\r';
            }
            return UnitAt(offset, _encoding.LineFeed) || UnitAt(offset, _encoding.CarriageReturn);
        }

        private bool PrecededByCarriageReturn()
        {
            var start = _bufferOffset;
            if (start - _unit < _encoding.ByteOrderMarkLength) return false;
            var previous = new byte[_unit];
            _stream.Seek(start - _unit, SeekOrigin.Begin);
            var read = 0;
            while (read < _unit)
            {
                var n = _stream.Read(previous, read, _unit - read);
                if (n <= 0) break;
                read += n;
            }
            _stream.Seek(start, SeekOrigin.Begin);
            if (read < _unit) return false;
            for (var i = 0; i < _unit; i++)
                if (previous[i] != _encoding.CarriageReturn[i]) return false;
            return true;
        }

        /// <summary>At least <paramref name="count"/> unread bytes in the buffer, unless the file ends first.</summary>
        private bool Ensure(int count)
        {
            while (_len - _pos < count)
            {
                if (_eof) return false;
                var remaining = _len - _pos;
                if (_pos > 0)
                {
                    Buffer.BlockCopy(_buffer, _pos, _buffer, 0, remaining);
                    _bufferOffset += _pos;
                    _pos = 0;
                    _len = remaining;
                }
                var read = _stream.Read(_buffer, _len, _buffer.Length - _len);
                if (read <= 0)
                {
                    _eof = true;
                    return _len - _pos >= count;
                }
                _len += read;
            }
            return true;
        }

        private bool UnitAt(int offset, byte[] unit)
        {
            for (var i = 0; i < _unit; i++)
                if (_buffer[offset + i] != unit[i]) return false;
            return true;
        }

        private void Append(int offset, int count)
        {
            var room = _maxLineBytes - _lineLength;
            if (count > room)
            {
                LastLineTruncated = true;
                count = room;
                if (count <= 0) return;
            }
            if (_lineLength + count > _line.Length)
            {
                var grown = new byte[Math.Max(_line.Length * 2, _lineLength + count)];
                Buffer.BlockCopy(_line, 0, grown, 0, _lineLength);
                _line = grown;
            }
            Buffer.BlockCopy(_buffer, offset, _line, _lineLength, count);
            _lineLength += count;
        }

        private string Decode() =>
            _lineLength == 0 || LastLineTruncated ? string.Empty : _encoding.Encoding.GetString(_line, 0, _lineLength);
    }
}
