using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// A bookmark belongs to a file, not to a name: IME rolls a log by renaming it, so the identity
    /// (volume plus file index) tells what happened to the bytes behind the bookmark.
    /// </summary>
    public sealed class LogFilePositionTrackerTests
    {
        private const string Live = @"C:\Logs\AppWorkload.log";
        private const string Archive = @"C:\Logs\AppWorkload-20260910-193000.log";
        private const string Underscore = @"C:\Logs\_AppWorkload.log";

        private static readonly DateTime Checked = new DateTime(2026, 9, 10, 19, 30, 0, DateTimeKind.Utc);

        private static Dictionary<string, string> Ids(params (string Path, string Id)[] entries)
        {
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries) ids[entry.Path] = entry.Id;
            return ids;
        }

        private static LogFilePositionTracker TrackerWithBookmark(string path, string id, long position)
        {
            var tracker = new LogFilePositionTracker();
            tracker.Reconcile(Ids((path, id)));
            tracker.MarkChecked(path, Checked);
            tracker.SetPosition(path, position);
            return tracker;
        }

        [Fact]
        public void Renamed_file_keeps_its_bookmark_and_the_old_path_starts_over()
        {
            var tracker = TrackerWithBookmark(Live, "A", 3000);

            var moves = tracker.Reconcile(Ids((Archive, "A"), (Live, "B")));

            var move = Assert.Single(moves);
            Assert.Equal(Live, move.From);
            Assert.Equal(Archive, move.To);
            Assert.Equal(3000, move.Position);
            Assert.Equal(3000, tracker.GetPosition(Archive));
            Assert.Equal("A", tracker.GetFileId(Archive));
            Assert.Equal(0, tracker.GetPosition(Live));
            Assert.Equal("B", tracker.GetFileId(Live));
        }

        [Fact]
        public void Both_paths_keep_the_freshness_reference()
        {
            var tracker = TrackerWithBookmark(Live, "A", 3000);

            tracker.Reconcile(Ids((Archive, "A"), (Live, "B")));

            Assert.True(tracker.HasSeen(Archive));
            Assert.Equal(Checked, tracker.GetLastCheckedUtc(Archive));
            Assert.True(tracker.HasSeen(Live));
            Assert.Equal(Checked, tracker.GetLastCheckedUtc(Live));
        }

        [Fact]
        public void Renamed_file_whose_old_path_is_not_recreated_yet_keeps_its_bookmark()
        {
            var tracker = TrackerWithBookmark(Live, "A", 3000);

            var moves = tracker.Reconcile(Ids((Archive, "A")));

            Assert.Single(moves);
            Assert.Equal(3000, tracker.GetPosition(Archive));
            Assert.Equal(0, tracker.GetPosition(Live));
            Assert.False(tracker.HasIdentity(Live));
        }

        [Fact]
        public void Second_name_for_the_same_file_moves_nothing()
        {
            var tracker = TrackerWithBookmark(Live, "A", 3000);

            var moves = tracker.Reconcile(Ids((Live, "A"), (Archive, "A")));

            Assert.Empty(moves);
            Assert.Equal(3000, tracker.GetPosition(Live));
            Assert.False(tracker.HasSeen(Archive));
        }

        [Fact]
        public void Unknown_file_stays_unseen_so_its_first_pass_is_not_taken_for_fresh_lines()
        {
            var tracker = TrackerWithBookmark(Live, "A", 3000);

            var moves = tracker.Reconcile(Ids((Live, "A"), (Archive, "X")));

            Assert.Empty(moves);
            Assert.True(tracker.HasIdentity(Archive));
            Assert.False(tracker.HasSeen(Archive));
            Assert.Equal(0, tracker.GetPosition(Archive));
        }

        [Fact]
        public void Path_holding_another_file_with_no_one_to_take_over_starts_at_zero()
        {
            var tracker = TrackerWithBookmark(Live, "A", 3000);

            var moves = tracker.Reconcile(Ids((Live, "B")));

            Assert.Empty(moves);
            // GetSafePosition alone would keep 3000 here when the new file is longer: lost lines.
            Assert.Equal(0, tracker.GetSafePosition(Live, 5000));
            Assert.Equal("B", tracker.GetFileId(Live));
        }

        [Fact]
        public void Unreadable_identity_changes_nothing()
        {
            var tracker = TrackerWithBookmark(Live, "A", 3000);

            var moves = tracker.Reconcile(Ids((Live, null!), (Archive, null!)));

            Assert.Empty(moves);
            Assert.Equal(3000, tracker.GetPosition(Live));
            Assert.Equal("A", tracker.GetFileId(Live));
            Assert.True(tracker.HasIdentity(Archive));
            Assert.Null(tracker.GetFileId(Archive));
        }

        [Fact]
        public void Single_slot_archive_is_replaced_by_the_next_rollover()
        {
            // LogMaxHistory <= 1: IME deletes _X.log and renames X.log to it.
            var tracker = TrackerWithBookmark(Underscore, "A", 9000);
            tracker.Reconcile(Ids((Underscore, "A"), (Live, "B")));
            tracker.SetPosition(Live, 3000);

            var moves = tracker.Reconcile(Ids((Underscore, "B"), (Live, "C")));

            var move = Assert.Single(moves);
            Assert.Equal(Live, move.From);
            Assert.Equal(Underscore, move.To);
            Assert.Equal(3000, tracker.GetPosition(Underscore));
            Assert.Equal(0, tracker.GetPosition(Live));
        }

        [Fact]
        public void A_renamed_file_can_leave_the_path_it_moved_into()
        {
            var tracker = TrackerWithBookmark(Live, "A", 3000);
            tracker.Reconcile(Ids((Live, "A"), (Underscore, "B")));
            tracker.SetPosition(Underscore, 700);

            var moves = tracker.Reconcile(Ids((Underscore, "A"), (Archive, "B"), (Live, "C")));

            Assert.Equal(2, moves.Count);
            Assert.Equal(3000, tracker.GetPosition(Underscore));
            Assert.Equal(700, tracker.GetPosition(Archive));
            Assert.Equal(0, tracker.GetPosition(Live));
        }

        [Fact]
        public void Behind_bookmark_only_when_smaller_than_the_position()
        {
            var tracker = TrackerWithBookmark(Live, "A", 3000);

            Assert.True(tracker.IsBehindBookmark(Live, 2999));
            Assert.False(tracker.IsBehindBookmark(Live, 3000));
            Assert.False(tracker.IsBehindBookmark(Archive, 0));
        }

        [Fact]
        public void Restored_identity_is_used_to_find_a_rollover_during_the_downtime()
        {
            var tracker = new LogFilePositionTracker();
            tracker.RestorePosition(Live, 3000, 3000);
            tracker.RestoreFileId(Live, "A");

            var moves = tracker.Reconcile(Ids((Archive, "A"), (Live, "B")));

            Assert.Single(moves);
            Assert.Equal(3000, tracker.GetPosition(Archive));
            Assert.Equal(0, tracker.GetPosition(Live));
        }
    }
}
