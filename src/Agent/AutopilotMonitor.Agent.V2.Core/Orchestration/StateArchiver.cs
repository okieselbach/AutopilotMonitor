#nullable enable
using System;
using System.IO;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Archives the reducer-state segment files (snapshot.json, signal-log.jsonl,
    /// journal.jsonl) into a timestamped <c>.part1-&lt;ts&gt;/</c> subfolder so the
    /// WhiteGlove Part-2 resume path can start from a fresh decision-engine state
    /// while preserving the Part-1 segments for diagnostics.
    /// <para>
    /// <b>Not archived:</b> <c>event-sequence.json</c>. Part 2 reuses the same SessionId
    /// as Part 1, and the backend stores events under an <c>(SessionId, Sequence)</c>
    /// ordering that the Inspector + the Web WhiteGlove split-detection
    /// (<c>useSessionDerivedData.ts</c>: <c>splitSequence = resumed.sequence - 1</c>)
    /// treat as session-wide. Resetting the counter at Part 2 would collide Part-1 and
    /// Part-2 events under identical sequence values and break the split. The counter
    /// stays in place so it continues monotonically across the resume boundary.
    /// </para>
    /// <para>
    /// Used exclusively by <see cref="EnrollmentOrchestrator"/> when it detects a
    /// persisted snapshot in <c>SessionStage.WhiteGloveSealed</c>. The archive-and-reset
    /// pattern is V1-symmetric to <c>SessionPersistence.ClearWhiteGloveComplete</c>:
    /// V1 simply deletes the marker file, V2 moves the reducer state aside (since V2
    /// owns a persistent decision-engine state that V1 did not have).
    /// </para>
    /// <para>
    /// Failure mode: if the move throws (FS permission denied, file locked by an
    /// external scanner, target directory exists), the exception propagates so the
    /// orchestrator's Start() fails fast rather than continuing with a half-archived
    /// mixed-state directory. The caller is responsible for surfacing the failure.
    /// </para>
    /// </summary>
    public static class StateArchiver
    {
        /// <summary>
        /// Reducer-state segment files moved into the <c>.part1-&lt;ts&gt;/</c> bucket on
        /// archive. <c>event-sequence.json</c> is intentionally NOT in this list — see the
        /// class summary.
        /// </summary>
        private static readonly string[] KnownSegmentFiles = new[]
        {
            "snapshot.json",
            "signal-log.jsonl",
            "journal.jsonl",
        };

        /// <summary>
        /// Move all existing state-segment files in <paramref name="stateDirectory"/>
        /// to a timestamped <c>.part1-&lt;utc-timestamp&gt;/</c> subfolder. No-op when
        /// the state directory does not exist; no-op (without creating the bucket) when
        /// none of the known segment files exist.
        /// </summary>
        /// <param name="stateDirectory">The state directory holding the four segment files.</param>
        /// <param name="reason">Free-text reason recorded as <c>reason.txt</c> alongside the archived files.</param>
        /// <param name="utcNow">Clock for the timestamped bucket name; injected for deterministic tests.</param>
        /// <param name="logger">Optional logger for INFO-level breadcrumb after a successful archive.</param>
        /// <returns>The full path of the created bucket, or <c>null</c> if there was nothing to archive.</returns>
        public static string? ArchiveStateFolder(
            string stateDirectory,
            string reason,
            Func<DateTime> utcNow,
            AgentLogger? logger = null)
        {
            if (string.IsNullOrEmpty(stateDirectory))
                throw new ArgumentException("stateDirectory is mandatory.", nameof(stateDirectory));
            if (utcNow == null) throw new ArgumentNullException(nameof(utcNow));

            if (!Directory.Exists(stateDirectory)) return null;

            // Decide whether anything would actually move before allocating a bucket — avoids
            // creating an empty .part1-<ts> directory on a fresh first-boot state folder.
            var hasAny = false;
            foreach (var name in KnownSegmentFiles)
            {
                if (File.Exists(Path.Combine(stateDirectory, name)))
                {
                    hasAny = true;
                    break;
                }
            }
            if (!hasAny) return null;

            var stamp = utcNow().ToString("yyyyMMdd'T'HHmmssfff'Z'");
            var bucket = Path.Combine(stateDirectory, ".part1-" + stamp);
            Directory.CreateDirectory(bucket);

            foreach (var name in KnownSegmentFiles)
            {
                var src = Path.Combine(stateDirectory, name);
                if (File.Exists(src))
                {
                    // Move semantics: throws on collision. Two archives within the same
                    // millisecond are vanishingly unlikely (the orchestrator only archives
                    // once per Start), and a collision is a real bug worth surfacing.
                    File.Move(src, Path.Combine(bucket, name));
                }
            }

            File.WriteAllText(Path.Combine(bucket, "reason.txt"), reason ?? string.Empty, Encoding.UTF8);

            logger?.Info(
                $"StateArchiver: archived state segments to '{Path.GetFileName(bucket)}' (reason={reason}).");

            return bucket;
        }
    }
}
