#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Runtime
{
    /// <summary>
    /// PinnedSourceFolder is the diagnostics packager's answer to the enumerate-then-open race:
    /// the folder and every file are judged on the handle that is read, never on the path
    /// string. Junctions come from the stock mklink (no privilege needed); the file-symlink
    /// case degrades to a no-op on hosts without the symlink privilege.
    /// </summary>
    public sealed class PinnedSourceFolderTests
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathNameW(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

        private static string? ShortPathOf(string path)
        {
            var sb = new StringBuilder(1024);
            var len = GetShortPathNameW(path, sb, (uint)sb.Capacity);
            return len == 0 || len >= sb.Capacity ? null : sb.ToString();
        }

        private static string LongPathOf(string path) => PinnedSourceFolder.TryGetLongPath(path) ?? path;

        private static bool SamePath(string a, string b) =>
            string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        [Fact]
        public void TryOpen_pins_a_real_folder_and_reports_where_it_points()
        {
            using var tmp = new TempDirectory();
            var folder = Path.Combine(tmp.Path, "Logs");
            Directory.CreateDirectory(folder);

            using var pinned = PinnedSourceFolder.TryOpen(folder + "\\", out var rejection);

            Assert.Null(rejection);
            Assert.NotNull(pinned);
            Assert.Equal(folder, pinned!.LexicalPath);
            Assert.True(SamePath(LongPathOf(folder), pinned.CanonicalPath), $"canonical '{pinned.CanonicalPath}' vs '{folder}'");
        }

        [Fact]
        public void TryOpen_accepts_a_short_name_path_and_canonicalizes_it()
        {
            using var tmp = new TempDirectory();
            var folder = Path.Combine(tmp.Path, "LongFolderNameForShortNames");
            Directory.CreateDirectory(folder);
            // 8.3 generation may be disabled on the volume; then the short form IS the long form
            // and this degrades to the plain case (CI runners hand out C:\Users\RUNNER~1\...).
            var shortForm = ShortPathOf(folder) ?? folder;

            using var pinned = PinnedSourceFolder.TryOpen(shortForm, out var rejection);

            Assert.Null(rejection);
            Assert.NotNull(pinned);
            Assert.True(SamePath(LongPathOf(folder), pinned!.CanonicalPath), $"canonical '{pinned.CanonicalPath}' vs '{folder}' (opened as '{shortForm}')");
        }

        [Fact]
        public void TryOpen_reports_a_missing_folder()
        {
            using var tmp = new TempDirectory();

            var pinned = PinnedSourceFolder.TryOpen(Path.Combine(tmp.Path, "nope"), out var rejection);

            Assert.Null(pinned);
            Assert.Equal(PinnedSourceFolder.RejectMissing, rejection);
        }

        [Fact]
        public void TryOpen_rejects_a_folder_that_is_a_junction()
        {
            using var tmp = new TempDirectory();
            using var target = new TempDirectory();
            var link = Path.Combine(tmp.Path, "link");
            NtfsLinks.CreateJunction(link, target.Path);
            try
            {
                var pinned = PinnedSourceFolder.TryOpen(link, out var rejection);

                Assert.Null(pinned);
                Assert.Equal(PinnedSourceFolder.RejectReparsePoint, rejection);
            }
            finally
            {
                NtfsLinks.RemoveLink(link);
            }
        }

        [Fact]
        public void TryOpen_rejects_a_folder_reached_through_a_junction()
        {
            using var tmp = new TempDirectory();
            using var target = new TempDirectory();
            Directory.CreateDirectory(Path.Combine(target.Path, "Logs"));
            var link = Path.Combine(tmp.Path, "link");
            NtfsLinks.CreateJunction(link, target.Path);
            try
            {
                // link\Logs is a real directory reached THROUGH a junction: its own attributes are
                // clean, only the handle's final path gives it away.
                var pinned = PinnedSourceFolder.TryOpen(Path.Combine(link, "Logs"), out var rejection);

                Assert.Null(pinned);
                Assert.Equal(PinnedSourceFolder.RejectResolvedElsewhere, rejection);
            }
            finally
            {
                NtfsLinks.RemoveLink(link);
            }
        }

        [Fact]
        public void TryOpenFile_returns_the_bytes_of_a_real_file()
        {
            using var tmp = new TempDirectory();
            var sub = Path.Combine(tmp.Path, "sub");
            Directory.CreateDirectory(sub);
            var file = Path.Combine(sub, "a.log");
            File.WriteAllText(file, "hello");

            using var pinned = PinnedSourceFolder.TryOpen(tmp.Path, out _);
            Assert.NotNull(pinned);
            using var source = pinned!.TryOpenFile(file, out var rejection);

            Assert.Null(rejection);
            Assert.NotNull(source);
            Assert.Equal(5, source!.Length);
            using var reader = new StreamReader(source.Stream, Encoding.UTF8);
            Assert.Equal("hello", reader.ReadToEnd());
        }

        [Fact]
        public void TryOpenFile_rejects_a_file_reached_through_a_junction_created_after_the_pin()
        {
            using var tmp = new TempDirectory();
            using var outside = new TempDirectory();
            var sub = Path.Combine(tmp.Path, "sub");
            Directory.CreateDirectory(sub);
            var file = Path.Combine(sub, "x.log");
            File.WriteAllText(file, "harmless");
            File.WriteAllText(Path.Combine(outside.Path, "x.log"), "outside");

            using var pinned = PinnedSourceFolder.TryOpen(tmp.Path, out _);
            Assert.NotNull(pinned);
            // Enumeration would have listed sub\x.log by now — the attacker swaps sub for a junction.
            Directory.Delete(sub, recursive: true);
            NtfsLinks.CreateJunction(sub, outside.Path);
            try
            {
                var source = pinned!.TryOpenFile(file, out var rejection);

                Assert.Null(source);
                Assert.Equal(PinnedSourceFolder.RejectResolvedElsewhere, rejection);
            }
            finally
            {
                NtfsLinks.RemoveLink(sub);
            }
        }

        [Fact]
        public void TryOpenFile_rejects_a_symlink_at_the_final_component()
        {
            using var tmp = new TempDirectory();
            using var outside = new TempDirectory();
            var targetFile = Path.Combine(outside.Path, "secret.log");
            File.WriteAllText(targetFile, "outside");
            var link = Path.Combine(tmp.Path, "x.log");
            if (!NtfsLinks.TryCreateFileSymlink(link, targetFile))
                return; // no SeCreateSymbolicLinkPrivilege / Developer Mode on this host — nothing to assert
            try
            {
                using var pinned = PinnedSourceFolder.TryOpen(tmp.Path, out _);
                Assert.NotNull(pinned);

                var source = pinned!.TryOpenFile(link, out var rejection);

                Assert.Null(source);
                Assert.Equal(PinnedSourceFolder.RejectReparsePoint, rejection);
            }
            finally
            {
                NtfsLinks.RemoveLink(link);
            }
        }

        [Fact]
        public void TryOpenFile_refuses_a_candidate_outside_the_pinned_folder()
        {
            using var tmp = new TempDirectory();
            using var other = new TempDirectory();
            var elsewhere = Path.Combine(other.Path, "x.log");
            File.WriteAllText(elsewhere, "x");

            using var pinned = PinnedSourceFolder.TryOpen(tmp.Path, out _);
            Assert.NotNull(pinned);

            var source = pinned!.TryOpenFile(elsewhere, out var rejection);

            Assert.Null(source);
            Assert.Equal(PinnedSourceFolder.RejectOutsideFolder, rejection);
        }

        [Fact]
        public void Pinned_folder_and_its_ancestors_cannot_be_renamed_while_pinned()
        {
            using var tmp = new TempDirectory();
            var parent = Path.Combine(tmp.Path, "parent");
            var folder = Path.Combine(parent, "Logs");
            Directory.CreateDirectory(folder);

            using (var pinned = PinnedSourceFolder.TryOpen(folder, out _))
            {
                Assert.NotNull(pinned);
                Assert.ThrowsAny<Exception>(() => Directory.Move(folder, Path.Combine(parent, "Logs2")));
                Assert.ThrowsAny<Exception>(() => Directory.Move(parent, Path.Combine(tmp.Path, "parent2")));
                Assert.True(Directory.Exists(folder));
            }

            // Released: the same rename now succeeds.
            Directory.Move(parent, Path.Combine(tmp.Path, "parent2"));
            Assert.True(Directory.Exists(Path.Combine(tmp.Path, "parent2", "Logs")));
        }
    }
}
