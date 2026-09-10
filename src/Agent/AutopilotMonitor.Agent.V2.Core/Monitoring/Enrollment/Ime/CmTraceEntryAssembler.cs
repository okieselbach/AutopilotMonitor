using System.Text;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>FNV-1a 64-bit — the entry fingerprint used by the tracker's byte ledger.</summary>
    internal static class Fnv1a64
    {
        public const ulong OffsetBasis = 14695981039346656037UL;
        public const ulong Prime = 1099511628211UL;

        public static ulong Fold(ulong hash, ulong value) => (hash ^ value) * Prime;
    }

    /// <summary>One assembled CMTrace entry: its byte range in the file, the fingerprint of those bytes and the text.</summary>
    internal struct AssembledEntry
    {
        public long Offset;
        public int Length;
        public ulong Hash;
        public string Text;
    }

    /// <summary>
    /// Turns physical lines into CMTrace entries: a line is one entry, unless it opens
    /// <c>&lt;![LOG[</c> without its <c>]LOG]!&gt;</c> close — then the continuation lines up to the
    /// closing one belong to it. Oversized lines and capped entries are dropped together with
    /// their remaining continuation lines, so no fragment is ever matched as an entry.
    /// <para>
    /// The read loop and the ledger verification (<c>ImeLogTracker.Overwrite.cs</c>) both feed
    /// this class, which is what makes their entry boundaries — and therefore the fingerprints
    /// they compare — identical by construction.
    /// </para>
    /// </summary>
    internal sealed class CmTraceEntryAssembler
    {
        public enum Outcome
        {
            /// <summary>The line was absorbed into an open multiline entry.</summary>
            Buffered,
            /// <summary>An entry is complete.</summary>
            Entry,
            /// <summary>A line over the byte cap was dropped (its entry with it).</summary>
            OversizedDropped,
            /// <summary>An open multiline entry hit the line/char cap and was dropped.</summary>
            CapDropped,
            /// <summary>A continuation line of a dropped entry was skipped.</summary>
            Skipped,
        }

        private const string LogOpen = "<![LOG[";
        private const string LogClose = "]LOG]!>";

        private readonly int _maxLines;
        private readonly int _maxChars;

        private StringBuilder _buffer;
        private int _lineCount;
        private long _entryStart;
        private ulong _entryHash;
        private bool _skippingDroppedEntry;

        public CmTraceEntryAssembler(int maxMultiLineBufferLines, int maxEntryBytes)
        {
            _maxLines = maxMultiLineBufferLines;
            _maxChars = maxEntryBytes;
        }

        /// <summary>True while a multiline entry is being assembled.</summary>
        public bool HasOpenEntry => _buffer != null;

        /// <summary>File offset of the open multiline entry (valid while <see cref="HasOpenEntry"/>).</summary>
        public long OpenEntryStart => _entryStart;

        /// <summary>Line count of the entry dropped by the last <see cref="Outcome.CapDropped"/>.</summary>
        public int DroppedLines { get; private set; }

        /// <summary>Char count of the entry dropped by the last <see cref="Outcome.CapDropped"/>.</summary>
        public int DroppedChars { get; private set; }

        /// <summary>
        /// Feeds one physical line. <paramref name="lineStart"/> / <paramref name="positionAfter"/>
        /// are the line's file offsets (start, next unread byte), <paramref name="lineHash"/> the
        /// reader's raw-byte fingerprint of the line.
        /// </summary>
        public Outcome Feed(string line, long lineStart, long positionAfter, bool truncated, ulong lineHash, out AssembledEntry entry)
        {
            entry = default(AssembledEntry);

            if (truncated)
            {
                // The capped prefix only tells whether the line opened an entry whose remaining
                // lines must be skipped rather than matched as raw text.
                if (_buffer != null)
                {
                    _buffer = null;
                    _lineCount = 0;
                    _skippingDroppedEntry = true;
                }
                else if (line.StartsWith(LogOpen) && !line.Contains(LogClose))
                {
                    _skippingDroppedEntry = true;
                }
                return Outcome.OversizedDropped;
            }

            if (_skippingDroppedEntry)
            {
                // A line opening a new entry ends the skip and is processed itself — checked
                // BEFORE the close-tag test, because a complete single-line entry contains both.
                if (line.StartsWith(LogOpen))
                    _skippingDroppedEntry = false;
                else
                {
                    if (line.Contains(LogClose)) _skippingDroppedEntry = false;
                    return Outcome.Skipped;
                }
            }

            if (_buffer != null)
            {
                _buffer.Append('\n').Append(line);
                _lineCount++;
                _entryHash = Fnv1a64.Fold(_entryHash, lineHash);

                if (line.Contains(LogClose))
                {
                    entry = new AssembledEntry
                    {
                        Offset = _entryStart,
                        Length = (int)(positionAfter - _entryStart),
                        Hash = _entryHash,
                        Text = _buffer.ToString(),
                    };
                    _buffer = null;
                    _lineCount = 0;
                    return Outcome.Entry;
                }

                if (_lineCount >= _maxLines || _buffer.Length >= _maxChars)
                {
                    DroppedLines = _lineCount;
                    DroppedChars = _buffer.Length;
                    _buffer = null;
                    _lineCount = 0;
                    _skippingDroppedEntry = true;
                    return Outcome.CapDropped;
                }

                return Outcome.Buffered;
            }

            if (line.StartsWith(LogOpen) && !line.Contains(LogClose))
            {
                _buffer = new StringBuilder(line);
                _lineCount = 1;
                _entryStart = lineStart;
                _entryHash = lineHash;
                return Outcome.Buffered;
            }

            entry = new AssembledEntry
            {
                Offset = lineStart,
                Length = (int)(positionAfter - lineStart),
                Hash = lineHash,
                Text = line,
            };
            return Outcome.Entry;
        }
    }
}
