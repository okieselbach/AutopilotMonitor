using System;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Gather
{
    /// <summary>
    /// A configured diagnostics entry is resolved to the folder and pattern that get
    /// enumerated, and the guard judges exactly that folder. The cases below pin the
    /// property that matters: no spelling of an allowed entry ever selects its parent.
    /// </summary>
    public sealed class DiagnosticsPathGuardsTests
    {
        private const string TestProfile = @"C:\Users\TestUser";

        private static readonly Func<string, bool> IsDirectory = _ => true;
        private static readonly Func<string, bool> IsNotDirectory = _ => false;

        private static DiagnosticsPathGuards.CollectionTarget Resolve(
            string path,
            bool includeSubfolders = false,
            bool unrestrictedMode = false,
            string? userProfilePath = null,
            Func<string, bool>? directoryExists = null)
        {
            Assert.True(DiagnosticsPathGuards.TryResolveCollectionTarget(
                path, includeSubfolders, unrestrictedMode, userProfilePath!, directoryExists ?? IsDirectory, out var target));
            return target;
        }

        private static void AssertRefused(
            string path,
            bool unrestrictedMode = false,
            string? userProfilePath = null,
            Func<string, bool>? directoryExists = null)
        {
            Assert.False(DiagnosticsPathGuards.TryResolveCollectionTarget(
                path, true, unrestrictedMode, userProfilePath!, directoryExists ?? IsDirectory, out var target));
            Assert.Null(target);
        }

        // ------------------------------------------------------------ folder entries

        [Theory]
        [InlineData(@"C:\Windows\Logs")]
        [InlineData(@"C:\Windows\Logs\")]
        [InlineData(@"C:\Windows\SetupDiag")]
        [InlineData(@"C:\ProgramData\AutopilotMonitor")]
        [InlineData(@"C:\ProgramData\Microsoft\DiagnosticLogCSP")]
        public void Folder_entry_collects_from_itself_never_from_its_parent(string configured)
        {
            var target = Resolve(configured, includeSubfolders: true);

            Assert.Equal(configured.TrimEnd('\\'), target.Folder, ignoreCase: true);
            Assert.Equal("*", target.Pattern);
            Assert.True(target.Recurse);
        }

        [Fact]
        public void Trailing_separator_means_folder_even_when_the_folder_does_not_exist_yet()
        {
            var target = Resolve(@"C:\Windows\Logs\NotThereYet\", directoryExists: IsNotDirectory);

            Assert.Equal(@"C:\Windows\Logs\NotThereYet", target.Folder, ignoreCase: true);
            Assert.Equal("*", target.Pattern);
        }

        [Fact]
        public void Folder_name_with_a_dot_is_still_a_folder()
        {
            var target = Resolve(@"C:\Windows\Logs\vendor.v2");

            Assert.Equal(@"C:\Windows\Logs\vendor.v2", target.Folder, ignoreCase: true);
            Assert.Equal("*", target.Pattern);
        }

        // ------------------------------------------------------------ wildcard entries

        [Fact]
        public void Wildcard_entry_collects_the_pattern_from_its_validated_parent()
        {
            var target = Resolve(@"C:\Windows\Panther\*.log", includeSubfolders: true);

            Assert.Equal(@"C:\Windows\Panther", target.Folder, ignoreCase: true);
            Assert.Equal("*.log", target.Pattern);
            Assert.True(target.Recurse);
        }

        [Fact]
        public void Wildcard_beside_an_allowlisted_file_is_refused()
        {
            // Only ReportingEvents.log is on the allowlist, not the folder it lives in.
            AssertRefused(@"C:\Windows\SoftwareDistribution\*.log");
        }

        // ------------------------------------------------------------ file entries

        [Fact]
        public void File_entry_never_recurses_because_its_folder_was_not_validated()
        {
            var target = Resolve(
                @"C:\Windows\SoftwareDistribution\ReportingEvents.log",
                includeSubfolders: true,
                directoryExists: IsNotDirectory);

            Assert.Equal(@"C:\Windows\SoftwareDistribution", target.Folder, ignoreCase: true);
            Assert.Equal("ReportingEvents.log", target.Pattern);
            Assert.False(target.Recurse);
        }

        [Fact]
        public void Missing_path_without_a_dot_is_one_file_name_not_a_wildcard_on_the_parent()
        {
            var target = Resolve(@"C:\Windows\Logs\nodot", includeSubfolders: true, directoryExists: IsNotDirectory);

            Assert.Equal(@"C:\Windows\Logs", target.Folder, ignoreCase: true);
            Assert.Equal("nodot", target.Pattern);
            Assert.False(target.Recurse);
        }

        // ------------------------------------------------------------ refusals

        [Theory]
        [InlineData(@"C:\Windows")]
        [InlineData(@"C:\ProgramData")]
        [InlineData(@"C:\ProgramData\Microsoft")]
        [InlineData(@"C:\Windows\Logs\..\System32")]
        [InlineData(@"C:\Windows\LogsEvil")]
        [InlineData("")]
        public void Paths_outside_the_allowlist_are_refused(string configured)
        {
            AssertRefused(configured);
        }

        [Theory]
        [InlineData(@"C:\Users")]
        [InlineData(@"C:\Users\TestUser\Documents")]
        [InlineData(@"C:\Windows\System32\config")]
        [InlineData(@"C:\Windows\System32\config\*")]
        public void Hard_blocks_hold_in_unrestricted_mode(string configured)
        {
            AssertRefused(configured, unrestrictedMode: true);
        }

        // ------------------------------------------------------------ user-profile token

        [Fact]
        public void Expanded_token_path_outside_AppData_is_refused_even_in_unrestricted_mode()
        {
            // The service expands %LOGGED_ON_USER_PROFILE% before the guard sees the path; an
            // unexpanded token is not under C:\Users lexically and used to slip past the block.
            AssertRefused(TestProfile + @"\Documents", unrestrictedMode: true, userProfilePath: TestProfile);
            AssertRefused(TestProfile + @"\Documents\*.txt", unrestrictedMode: true, userProfilePath: TestProfile);
        }

        [Fact]
        public void Expanded_token_path_on_the_profile_allowlist_is_admitted_in_restricted_mode()
        {
            var target = Resolve(
                TestProfile + @"\AppData\Local\RealmJoin\Logs\*.log",
                userProfilePath: TestProfile);

            Assert.Equal(TestProfile + @"\AppData\Local\RealmJoin\Logs", target.Folder, ignoreCase: true);
            Assert.Equal("*.log", target.Pattern);
        }

        [Fact]
        public void Profile_path_without_the_token_stays_blocked()
        {
            AssertRefused(TestProfile + @"\AppData\Local\RealmJoin\Logs\*.log", userProfilePath: null);
        }

        // ------------------------------------------------------------ recursion guard

        [Theory]
        [InlineData(@"C:\Users")]
        [InlineData(@"C:\Users\TestUser")]
        [InlineData(@"C:\Windows\System32\config")]
        [InlineData(@"C:\Windows\System32\config\RegBack")]
        public void Recursion_never_enters_a_hard_blocked_subtree_in_unrestricted_mode(string directory)
        {
            Assert.False(DiagnosticsPathGuards.IsDirectoryEnterable(directory, unrestrictedMode: true, userProfilePath: null));
        }

        [Fact]
        public void Recursion_enters_ordinary_subfolders()
        {
            Assert.True(DiagnosticsPathGuards.IsDirectoryEnterable(@"C:\Windows\System32\drivers", unrestrictedMode: true, userProfilePath: null));
            Assert.True(DiagnosticsPathGuards.IsDirectoryEnterable(@"C:\Windows\Logs\CBS", unrestrictedMode: false, userProfilePath: null));
            Assert.False(DiagnosticsPathGuards.IsDirectoryEnterable(@"C:\Windows\System32\drivers", unrestrictedMode: false, userProfilePath: null));
        }
    }
}
