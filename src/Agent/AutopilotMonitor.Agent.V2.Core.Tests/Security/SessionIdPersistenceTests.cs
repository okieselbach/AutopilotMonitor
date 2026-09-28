using System;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    public sealed class SessionIdPersistenceTests
    {
        [Fact]
        public void GetOrCreate_without_existing_file_creates_a_new_guid()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);

            var id = sut.GetOrCreate();

            Assert.True(Guid.TryParse(id, out _));
            Assert.True(File.Exists(Path.Combine(tmp.Path, "session.id")));
            Assert.Equal(id, File.ReadAllText(Path.Combine(tmp.Path, "session.id")));
        }

        [Fact]
        public void GetOrCreate_returns_same_id_on_repeated_calls()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);

            var first = sut.GetOrCreate();
            var second = sut.GetOrCreate();
            var third = sut.GetOrCreate();

            Assert.Equal(first, second);
            Assert.Equal(first, third);
        }

        [Fact]
        public void GetOrCreate_new_instance_resumes_persisted_session()
        {
            using var tmp = new TempDirectory();
            var first = new SessionIdPersistence(tmp.Path).GetOrCreate();
            var resumed = new SessionIdPersistence(tmp.Path).GetOrCreate();

            Assert.Equal(first, resumed);
        }

        [Fact]
        public void GetOrCreate_regenerates_when_file_content_is_corrupt()
        {
            using var tmp = new TempDirectory();
            File.WriteAllText(Path.Combine(tmp.Path, "session.id"), "not-a-guid");

            var id = new SessionIdPersistence(tmp.Path).GetOrCreate();

            Assert.True(Guid.TryParse(id, out _));
            Assert.NotEqual("not-a-guid", id);
        }

        [Fact]
        public void Delete_clears_persisted_session_so_next_call_regenerates()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);
            var first = sut.GetOrCreate();

            sut.Delete();
            Assert.False(File.Exists(Path.Combine(tmp.Path, "session.id")));

            var second = sut.GetOrCreate();
            Assert.NotEqual(first, second);
        }

        [Fact]
        public void Rotate_writes_a_new_id_and_keeps_the_whiteglove_marker()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);
            var first = sut.GetOrCreate();
            sut.SaveWhiteGloveComplete();
            var createdBefore = File.ReadAllText(Path.Combine(tmp.Path, "session.created"));

            var rotated = sut.Rotate();

            Assert.True(Guid.TryParse(rotated, out _));
            Assert.NotEqual(first, rotated);
            Assert.Equal(rotated, File.ReadAllText(Path.Combine(tmp.Path, "session.id")));
            Assert.Equal(rotated, new SessionIdPersistence(tmp.Path).GetOrCreate());
            Assert.True(sut.IsWhiteGloveResume(), "Rotate must not clear whiteglove.complete (unlike Delete)");
            Assert.True(File.Exists(Path.Combine(tmp.Path, "session.created")));
            Assert.NotEqual(createdBefore, File.ReadAllText(Path.Combine(tmp.Path, "session.created")));
        }

        [Fact]
        public void Ctor_creates_data_directory_when_missing()
        {
            using var tmp = new TempDirectory();
            var nested = Path.Combine(tmp.Path, "nested", "state");
            Assert.False(Directory.Exists(nested));

            var sut = new SessionIdPersistence(nested);
            var id = sut.GetOrCreate();

            Assert.True(Directory.Exists(nested));
            Assert.True(Guid.TryParse(id, out _));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Ctor_rejects_null_empty_or_whitespace_data_directory(string? dataDirectory)
        {
            Assert.Throws<ArgumentNullException>(() => new SessionIdPersistence(dataDirectory!));
        }

        [Fact]
        public void GetOrCreate_with_logger_emits_resume_debug_on_second_call()
        {
            using var tmp = new TempDirectory();
            var logDir = Path.Combine(tmp.Path, "logs");
            var logger = new AgentLogger(logDir, AgentLogLevel.Debug);

            var first = new SessionIdPersistence(tmp.Path).GetOrCreate(logger);
            var second = new SessionIdPersistence(tmp.Path).GetOrCreate(logger);

            Assert.Equal(first, second);
        }

        // --------------------------------------------------------------- M4.6.α additions

        [Fact]
        public void GetOrCreate_also_writes_session_created_timestamp()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);

            sut.GetOrCreate();

            var createdPath = Path.Combine(tmp.Path, "session.created");
            Assert.True(File.Exists(createdPath));
            var parsed = sut.LoadSessionCreatedAt();
            Assert.NotNull(parsed);
            Assert.Equal(DateTimeKind.Utc, parsed!.Value.Kind);
        }

        [Theory]
        // null content sentinel → do NOT write the file (tests the "missing file" path).
        // Any other value is written verbatim; LoadSessionCreatedAt must return null unless
        // the content round-trips through DateTime.TryParse RoundtripKind.
        [InlineData(null)]           // file missing
        [InlineData("not-a-date")]   // unparseable garbage
        [InlineData("")]             // empty file (NEW coverage — Trim → "" → TryParse fails)
        [InlineData("   ")]          // whitespace only (NEW coverage — Trim → "" → TryParse fails)
        [InlineData("2026-13-45")]   // out-of-range date components (NEW coverage)
        public void LoadSessionCreatedAt_returns_null_when_file_missing_or_unparseable(string? fileContent)
        {
            using var tmp = new TempDirectory();
            if (fileContent != null)
                File.WriteAllText(Path.Combine(tmp.Path, "session.created"), fileContent);

            Assert.Null(new SessionIdPersistence(tmp.Path).LoadSessionCreatedAt());
        }

        [Fact]
        public void GetOrCreate_initialises_missing_session_created_on_recovery()
        {
            using var tmp = new TempDirectory();

            // Simulate a session.id written by an older persistence without a companion timestamp.
            var existingId = Guid.NewGuid().ToString();
            File.WriteAllText(Path.Combine(tmp.Path, "session.id"), existingId);

            var sut = new SessionIdPersistence(tmp.Path);
            var resumed = sut.GetOrCreate();

            Assert.Equal(existingId, resumed);
            Assert.True(File.Exists(Path.Combine(tmp.Path, "session.created")));
            Assert.NotNull(sut.LoadSessionCreatedAt());
        }

        [Fact]
        public void SaveSessionCreatedAt_persists_utc_and_roundtrips()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);

            var now = new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc);
            sut.SaveSessionCreatedAt(now);

            var loaded = sut.LoadSessionCreatedAt();
            Assert.Equal(now, loaded);
        }

        [Fact]
        public void SessionExists_reflects_session_id_presence()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);

            Assert.False(sut.SessionExists());

            sut.GetOrCreate();
            Assert.True(sut.SessionExists());

            sut.Delete();
            Assert.False(sut.SessionExists());
        }

        [Fact]
        public void IsWhiteGloveResume_returns_true_when_marker_present()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);

            Assert.False(sut.IsWhiteGloveResume());

            File.WriteAllText(Path.Combine(tmp.Path, "whiteglove.complete"), "1");
            Assert.True(sut.IsWhiteGloveResume());
        }

        [Fact]
        public void Delete_clears_session_id_created_timestamp_and_whiteglove_marker()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);
            sut.GetOrCreate();
            File.WriteAllText(Path.Combine(tmp.Path, "whiteglove.complete"), "1");

            sut.Delete();

            Assert.False(File.Exists(Path.Combine(tmp.Path, "session.id")));
            Assert.False(File.Exists(Path.Combine(tmp.Path, "session.created")));
            Assert.False(File.Exists(Path.Combine(tmp.Path, "whiteglove.complete")));
        }

        // ============================================================ failed-registration record

        private static RegistrationFailureSummary FailedRun(DateTime at, string outcome = "Failed", bool? linkUpAtEnd = false) =>
            new RegistrationFailureSummary
            {
                FailedRuns = 1,
                FirstFailedAtUtc = at,
                LastFailedAtUtc = at,
                Outcome = outcome,
                LastError = "No such host is known.",
                NetworkLinkUpAtStart = false,
                NetworkLinkUpAtEnd = linkUpAtEnd,
                AttemptWindowSeconds = 91.3,
                ConfigFetchOutcome = "UsedDefaults",
            };

        [Fact]
        public void RecordRegistrationFailure_counts_runs_keeps_the_first_time_and_describes_the_latest_run()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);
            var first = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);
            var second = new DateTime(2026, 9, 12, 7, 30, 0, DateTimeKind.Utc);

            Assert.Null(sut.LoadRegistrationFailure());
            sut.RecordRegistrationFailure(FailedRun(first));
            sut.RecordRegistrationFailure(FailedRun(second, outcome: "AuthFailed", linkUpAtEnd: true));

            // A fresh instance reads it back from disk, as the next boot's emergency break does.
            var record = new SessionIdPersistence(tmp.Path).LoadRegistrationFailure();
            Assert.NotNull(record);
            Assert.Equal(2, record!.FailedRuns);
            Assert.Equal(first, record.FirstFailedAtUtc);
            Assert.Equal(second, record.LastFailedAtUtc);
            Assert.Equal("AuthFailed", record.Outcome);
            Assert.True(record.NetworkLinkUpAtEnd);
            Assert.Equal(91.3, record.AttemptWindowSeconds);
            Assert.Equal("UsedDefaults", record.ConfigFetchOutcome);
        }

        [Fact]
        public void Registration_failure_record_is_cleared_by_success_delete_and_rotate()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);
            sut.GetOrCreate();
            var at = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);

            sut.RecordRegistrationFailure(FailedRun(at));
            sut.ClearRegistrationFailure();
            Assert.Null(sut.LoadRegistrationFailure());

            sut.RecordRegistrationFailure(FailedRun(at));
            sut.Rotate();
            Assert.Null(sut.LoadRegistrationFailure());

            sut.RecordRegistrationFailure(FailedRun(at));
            sut.Delete();
            Assert.Null(sut.LoadRegistrationFailure());
            Assert.False(File.Exists(Path.Combine(tmp.Path, "registration-failure.json")));
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("{\"FailedRuns\":0}")]
        public void LoadRegistrationFailure_reports_nothing_for_a_corrupt_or_empty_record(string content)
        {
            using var tmp = new TempDirectory();
            File.WriteAllText(Path.Combine(tmp.Path, "registration-failure.json"), content);

            Assert.Null(new SessionIdPersistence(tmp.Path).LoadRegistrationFailure());
        }
    }
}
