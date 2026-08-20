using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Tracks file read positions for incremental log file reading.
    /// In-memory only - resets on agent restart, which is acceptable since
    /// we start fresh per enrollment session.
    /// </summary>
    public class LogFilePositionTracker
    {
        private readonly Dictionary<string, FilePositionState> _positions
            = new Dictionary<string, FilePositionState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the safe read position for a file, detecting rollover/truncation.
        /// If the current file size is smaller than the stored position, the file
        /// has been rotated - resets to 0 to read from beginning.
        /// Returns 0 if no position has been recorded yet.
        /// </summary>
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
        /// </summary>
        public void RestorePosition(string filePath, long position, long lastKnownSize)
        {
            _positions[filePath] = new FilePositionState
            {
                Position = position,
                LastKnownSize = lastKnownSize,
                LastReadTime = DateTime.UtcNow
            };
        }
    }

    public class FilePositionState
    {
        public long Position { get; set; }
        public long LastKnownSize { get; set; }
        public DateTime LastReadTime { get; set; }
    }
}
