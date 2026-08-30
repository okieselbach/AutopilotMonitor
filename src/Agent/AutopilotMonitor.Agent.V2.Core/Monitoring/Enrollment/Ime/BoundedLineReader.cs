using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Byte-oriented, length-bounded line reader for tailing IME logs.
    /// <para>
    /// Why not <see cref="StreamReader.ReadLineAsync"/>: it materializes the whole line before the
    /// caller can look at its length, so a single multi-hundred-MB line appended by any process
    /// that can write to the IME Logs folder (standard users can) is allocated in full — an
    /// OutOfMemory in the SYSTEM tracker's poll loop, which retries from the same position every
    /// second. This reader stops accumulating at <see cref="MaxLineBytes"/>, discards the rest of
    /// the line while scanning for the terminator, and reports the line as
    /// <see cref="LastLineTruncated"/> with only the capped prefix returned. Its read-ahead is
    /// tracked in bytes, so <see cref="Position"/> is the exact file offset of the next unread
    /// byte — a consumer can bookmark the start of a line it did not process instead of the
    /// StreamReader's opaque decoder position.
    /// </para>
    /// Semantics: '\n' terminates a line; one trailing '\r' is stripped (IME writes CRLF, never a
    /// lone CR); a UTF-8 BOM is skipped only at absolute file offset 0; bytes are decoded as UTF-8
    /// (invalid sequences become U+FFFD, matching the previous StreamReader behaviour). An
    /// unterminated tail at EOF is returned with <see cref="LastLineTerminated"/> == false so the
    /// caller can decide whether to hold it back for the writer to finish.
    /// </summary>
    internal sealed class BoundedLineReader
    {
        private const int ReadBufferSize = 64 * 1024;
        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

        private readonly Stream _stream;
        private readonly int _maxLineBytes;
        private readonly byte[] _readBuffer = new byte[ReadBufferSize];
        private int _readPos;
        private int _readLen;
        private long _readBufferFileOffset;
        private bool _eof;

        private byte[] _line = new byte[4096];
        private int _lineLen;

        /// <param name="stream">Seekable stream positioned at the first byte to read.</param>
        /// <param name="maxLineBytes">Upper bound of bytes kept per line; the remainder is discarded.</param>
        public BoundedLineReader(Stream stream, int maxLineBytes)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (maxLineBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxLineBytes));
            _stream = stream;
            _maxLineBytes = maxLineBytes;
            _readBufferFileOffset = stream.Position;
        }

        /// <summary>Cap in bytes applied to every line.</summary>
        public int MaxLineBytes => _maxLineBytes;

        /// <summary>Absolute file offset of the next unread byte (exact — no decoder read-ahead).</summary>
        public long Position => _readBufferFileOffset + _readPos;

        /// <summary>Absolute file offset of the first byte of the line most recently returned.</summary>
        public long LastLineStart { get; private set; }

        /// <summary>True when the last returned line ended with '\n'; false for an EOF tail.</summary>
        public bool LastLineTerminated { get; private set; }

        /// <summary>True when the last returned line exceeded <see cref="MaxLineBytes"/> and was cut.</summary>
        public bool LastLineTruncated { get; private set; }

        /// <summary>
        /// Reads the next line. Returns null at EOF when no bytes remain. A truncated line is
        /// returned as its capped prefix (see <see cref="LastLineTruncated"/>).
        /// </summary>
        public async Task<string> ReadLineAsync(CancellationToken token)
        {
            LastLineStart = Position;
            LastLineTerminated = false;
            LastLineTruncated = false;
            _lineLen = 0;
            var sawAnyByte = false;

            while (true)
            {
                if (_readPos >= _readLen)
                {
                    if (_eof || !await FillAsync(token).ConfigureAwait(false))
                    {
                        if (!sawAnyByte) return null;
                        return Materialize();
                    }
                }

                sawAnyByte = true;
                var newline = Array.IndexOf(_readBuffer, (byte)'\n', _readPos, _readLen - _readPos);
                if (newline >= 0)
                {
                    Append(_readPos, newline - _readPos);
                    _readPos = newline + 1;
                    LastLineTerminated = true;
                    return Materialize();
                }

                Append(_readPos, _readLen - _readPos);
                _readPos = _readLen;
            }
        }

        private async Task<bool> FillAsync(CancellationToken token)
        {
            _readBufferFileOffset += _readLen;
            _readPos = 0;
            _readLen = await _stream.ReadAsync(_readBuffer, 0, _readBuffer.Length, token).ConfigureAwait(false);
            if (_readLen <= 0)
            {
                _readLen = 0;
                _eof = true;
                return false;
            }
            return true;
        }

        private void Append(int offset, int count)
        {
            if (count <= 0) return;
            var room = _maxLineBytes - _lineLen;
            if (count > room)
            {
                LastLineTruncated = true;
                count = room;
                if (count <= 0) return;
            }
            if (_lineLen + count > _line.Length)
            {
                var grown = new byte[Math.Max(_line.Length * 2, _lineLen + count)];
                Buffer.BlockCopy(_line, 0, grown, 0, _lineLen);
                _line = grown;
            }
            Buffer.BlockCopy(_readBuffer, offset, _line, _lineLen, count);
            _lineLen += count;
        }

        private string Materialize()
        {
            var start = 0;
            var len = _lineLen;
            if (LastLineStart == 0 && len >= 3
                && _line[0] == Utf8Bom[0] && _line[1] == Utf8Bom[1] && _line[2] == Utf8Bom[2])
            {
                start = 3;
                len -= 3;
            }
            // Only a terminated line can carry a CR before its LF; a capped line has had its
            // tail (and thus any CR) discarded, so the check is skipped there.
            if (LastLineTerminated && !LastLineTruncated && len > 0 && _line[start + len - 1] == (byte)'\r')
                len--;
            return len == 0 ? string.Empty : Encoding.UTF8.GetString(_line, start, len);
        }
    }
}
