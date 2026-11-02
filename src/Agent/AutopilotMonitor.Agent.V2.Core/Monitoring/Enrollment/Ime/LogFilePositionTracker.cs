using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Tracks the IME log tracker's file read positions for incremental log file reading.
    /// The bookmarks survive agent restarts through the tracker's own state file
    /// (<see cref="GetAllPositions"/> / <see cref="RestorePosition"/>); the freshness timestamps do not.
    /// </summary>
    public class LogFilePositionTracker
    {
        private readonly Dictionary<string, FilePositionState> _positions
            = new Dictionary<string, FilePositionState>(StringComparer.OrdinalIgnoreCase);

        // Which file each path held when last checked (see Reconcile). Apart from _positions: a file
        // whose identity is known but which no pass looked at yet must still count as unseen for
        // HasSeen. A null value means the identity could not be read.
        private readonly Dictionary<string, string> _fileIds
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether this file was already observed in an earlier pass.
        /// <para>
        /// The offset calibration depends on this: growth measured against a size we saw before
        /// means the new bytes were written within one poll interval, so the last line of that
        /// pass is a valid "written now" anchor. On the FIRST sight of a file we read it from
        /// position 0 and its last line may be arbitrarily old — anchoring on that would measure
        /// the line's age instead of the writer's timezone offset.
        /// </para>
        /// </summary>
        public bool HasSeen(string filePath) => _positions.ContainsKey(filePath);

        /// <summary>Whether <see cref="Reconcile"/> already looked at this path.</summary>
        public bool HasIdentity(string filePath) => _fileIds.ContainsKey(filePath);

        /// <summary>The identity recorded for this path, or null.</summary>
        public string GetFileId(string filePath)
        {
            string id;
            return _fileIds.TryGetValue(filePath, out id) ? id : null;
        }

        /// <summary>Restores a persisted identity. <see cref="Reconcile"/> checks it against the disk on the next pass.</summary>
        public void RestoreFileId(string filePath, string fileId)
        {
            if (fileId != null) _fileIds[filePath] = fileId;
        }

        /// <summary>True when the file is smaller than its bookmark: it was rolled over or truncated.</summary>
        public bool IsBehindBookmark(string filePath, long currentFileSize)
        {
            FilePositionState state;
            return _positions.TryGetValue(filePath, out state) && currentFileSize < state.Position;
        }

        /// <summary>
        /// Brings the bookmarks in line with the files on disk. A bookmark belongs to a file, not to
        /// a name: IME rolls a log by renaming it, so a known file under a new path keeps its
        /// position and only the bytes behind it are still unread. The old path starts at 0 with
        /// whatever file it holds next, and keeps its freshness reference because that file was
        /// written since the last look. Returns the moves.
        /// <list type="bullet">
        ///   <item>A file that is still at its old path as well (a hard link) moves nothing.</item>
        ///   <item>A path that holds another file with no bookmark to take over starts at 0, even
        ///     when the new file is longer than the old position.</item>
        ///   <item>An unreadable identity (null) changes nothing, the path keeps its bookmark.</item>
        /// </list>
        /// </summary>
        public List<BookmarkMove> Reconcile(IDictionary<string, string> idByPath)
        {
            var moves = new List<BookmarkMove>();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in idByPath)
            {
                if (entry.Value == null || GetFileId(entry.Key) == entry.Value) continue;
                var holder = FindBookmarkLeftBehind(entry.Value, entry.Key, idByPath);
                if (holder != null && claimed.Add(holder))
                    moves.Add(new BookmarkMove(holder, entry.Key, _positions[holder].Position));
            }

            // In two steps: a path can be source and target at once (X.log becomes _X.log while
            // _X.log's own file was replaced).
            var movedStates = new List<FilePositionState>();
            foreach (var move in moves)
            {
                var state = _positions[move.From];
                movedStates.Add(state);
                _positions[move.From] = new FilePositionState { LastCheckedUtc = state.LastCheckedUtc };
                _fileIds.Remove(move.From);
            }
            for (var i = 0; i < moves.Count; i++)
            {
                _positions[moves[i].To] = movedStates[i];
                _fileIds[moves[i].To] = idByPath[moves[i].To];
            }

            foreach (var entry in idByPath)
            {
                if (entry.Value == null)
                {
                    if (!_fileIds.ContainsKey(entry.Key)) _fileIds[entry.Key] = null;
                    continue;
                }

                string known;
                FilePositionState state;
                if (_fileIds.TryGetValue(entry.Key, out known) && known != null && known != entry.Value
                    && _positions.TryGetValue(entry.Key, out state))
                {
                    state.Position = 0;
                    state.LastKnownSize = 0;
                }
                _fileIds[entry.Key] = entry.Value;
            }

            return moves;
        }

        private string FindBookmarkLeftBehind(string fileId, string newPath, IDictionary<string, string> idByPath)
        {
            foreach (var known in _fileIds)
            {
                if (known.Value != fileId || string.Equals(known.Key, newPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!_positions.ContainsKey(known.Key)) continue;

                string stillThere;
                if (idByPath.TryGetValue(known.Key, out stillThere) && stillThere == fileId) continue;
                return known.Key;
            }
            return null;
        }

        /// <summary>
        /// Gets the safe read position for a file, detecting rollover/truncation.
        /// If the current file size is smaller than the stored position, the file
        /// has been rotated - resets to 0 to read from beginning.
        /// Returns 0 if no position has been recorded yet.
        /// </summary>
        public long GetSafePosition(string filePath, long currentFileSize)
        {
            FilePositionState state;
            if (!_positions.TryGetValue(filePath, out state))
                return 0;

            // Detect rollover: file is smaller than our stored position
            if (currentFileSize < state.Position)
            {
                state.Position = 0;
                state.LastKnownSize = currentFileSize;
                return 0;
            }

            return state.Position;
        }

        /// <summary>
        /// Stores the current read position for a file after successful reading.
        /// </summary>
        public void SetPosition(string filePath, long position)
        {
            FilePositionState state;
            if (!_positions.TryGetValue(filePath, out state))
            {
                state = new FilePositionState();
                _positions[filePath] = state;
            }

            state.Position = position;
            state.LastKnownSize = position; // Position is always <= file size
            state.LastReadTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Records that the file was looked at in a poll pass — whether or not it had new data.
        /// Creates the entry on first sight (position 0), so a file first seen EMPTY counts as
        /// observed and its first content is provably fresh.
        /// <para>
        /// This timestamp is the freshness reference for per-line offset anchoring: bytes found
        /// in the NEXT pass were written after this instant, so
        /// <c>now - LastCheckedUtc</c> bounds the age of every line in that pass's chunk.
        /// The caller supplies the clock (the tracker's <c>UtcNowProvider</c>) so replay/test
        /// clocks stay consistent with the freshness comparison.
        /// </para>
        /// </summary>
        public void MarkChecked(string filePath, DateTime nowUtc)
        {
            FilePositionState state;
            if (!_positions.TryGetValue(filePath, out state))
            {
                state = new FilePositionState();
                _positions[filePath] = state;
            }

            state.LastCheckedUtc = nowUtc;
        }

        /// <summary>
        /// When the file was last looked at, or <see cref="DateTime.MinValue"/> when it never
        /// was in THIS process — including entries restored from persisted state, whose
        /// bookmark survives an agent restart but whose freshness deliberately does not
        /// (the restart gap can hold arbitrarily old backlog).
        /// </summary>
        public DateTime GetLastCheckedUtc(string filePath)
        {
            FilePositionState state;
            return _positions.TryGetValue(filePath, out state) ? state.LastCheckedUtc : DateTime.MinValue;
        }

        /// <summary>
        /// Gets the stored position for a file, or 0 if not tracked.
        /// Does not perform rollover detection.
        /// </summary>
        public long GetPosition(string filePath)
        {
            FilePositionState state;
            if (_positions.TryGetValue(filePath, out state))
                return state.Position;
            return 0;
        }

        /// <summary>
        /// Returns all tracked positions for state persistence.
        /// Keys are full file paths.
        /// </summary>
        public Dictionary<string, FilePositionState> GetAllPositions()
        {
            return new Dictionary<string, FilePositionState>(_positions, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Restores a previously persisted position for a file.
        /// Used on agent restart to continue reading from the last known position.
        /// <para>
        /// <see cref="FilePositionState.LastCheckedUtc"/> is deliberately NOT restored: the
        /// bookmark survives the restart, the freshness guarantee does not — the first pass
        /// after a restart reads the whole downtime backlog and must never anchor per-line
        /// offsets on it.
        /// </para>
        /// </summary>
        public void RestorePosition(string filePath, long position, long lastKnownSize)
        {
            _positions[filePath] = new FilePositionState
            {
                Position = position,
                LastKnownSize = lastKnownSize,
                LastReadTime = DateTime.UtcNow,
                LastCheckedUtc = DateTime.MinValue
            };
        }
    }

    /// <summary>One bookmark that followed its file to a new path.</summary>
    public sealed class BookmarkMove
    {
        public BookmarkMove(string from, string to, long position)
        {
            From = from;
            To = to;
            Position = position;
        }

        public string From { get; }
        public string To { get; }
        public long Position { get; }
    }

    public class FilePositionState
    {
        public long Position { get; set; }
        public long LastKnownSize { get; set; }
        public DateTime LastReadTime { get; set; }

        /// <summary>
        /// When this file was last looked at by a poll pass IN THIS PROCESS. In-memory only —
        /// never persisted, so a restart resets it (see <see cref="LogFilePositionTracker.RestorePosition"/>).
        /// </summary>
        public DateTime LastCheckedUtc { get; set; }
    }
}
