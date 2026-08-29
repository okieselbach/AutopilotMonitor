#nullable enable
using System;
using AutopilotMonitor.Agent.V2.Core.Termination;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Termination
{
    /// <summary>
    /// Plan §F3 (debrief 7dd4e593) — `Path.GetTempPath()` resolves to
    /// <c>C:\WINDOWS\SystemTemp\</c> when the agent runs as SYSTEM. Standard users have no
    /// read/execute access there, so the SummaryDialog launched into the user session
    /// failed to start with a generic "This application could not be started" MessageBox.
    /// The launcher must place its temp directory under <c>%ProgramData%\AutopilotMonitor-Summary\</c>
    /// instead (V1 parity), which is world-readable + executable. Single flat directory
    /// (no per-launch GUID subdir) matches V1; the launcher wipes it on every launch.
    /// </summary>
    public sealed class SummaryDialogLauncherTests
    {
        [Fact]
        public void ResolveSummaryTempDir_returns_path_under_program_data()
        {
            var path = SummaryDialogLauncher.ResolveSummaryTempDir();
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            Assert.StartsWith(programData, path, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(SummaryDialogLauncher.SummaryTempRootEnvVar, path, StringComparison.Ordinal);
        }

        [Fact]
        public void ResolveSummaryTempDir_does_not_use_systemtemp_or_user_temp()
        {
            // Regression guard: the broken implementation used Path.GetTempPath() which
            // resolves to C:\WINDOWS\SystemTemp\ for SYSTEM. The new implementation must
            // never produce such a path even when the test process happens to run as SYSTEM.
            var path = SummaryDialogLauncher.ResolveSummaryTempDir();

            Assert.DoesNotContain(@"\WINDOWS\SystemTemp", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\AppData\Local\Temp", path, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ResolveSummaryTempDir_is_flat_no_per_launch_guid_subdir()
        {
            // V1 parity: a single flat directory wiped on every launch. The previous V2
            // implementation appended a per-launch Guid.NewGuid() subdir which (a) created
            // a leftover directory per session if --cleanup didn't fire and (b) diverged
            // from the V1 lifecycle that the dialog and ACL-grant helper both assume.
            var a = SummaryDialogLauncher.ResolveSummaryTempDir();
            var b = SummaryDialogLauncher.ResolveSummaryTempDir();

            Assert.Equal(a, b);
        }

        // The branding URL is tenant config; the dialog parses arguments last-occurrence-wins, so
        // an embedded quote must never terminate the argument and inject extra flags (CWE-88).

        private const string BaseArgs = "--status-file \"C:\\x\\final-status.json\" --timeout 30 --cleanup";

        [Fact]
        public void BuildDialogArguments_omits_branding_when_empty()
        {
            var args = SummaryDialogLauncher.BuildDialogArguments(@"C:\x\final-status.json", 30, null, null);

            Assert.Equal(BaseArgs, args);
        }

        [Fact]
        public void BuildDialogArguments_appends_clean_https_url_as_single_quoted_argument()
        {
            var args = SummaryDialogLauncher.BuildDialogArguments(@"C:\x\final-status.json", 30, "https://cdn.example/logo.png?v=2", null);

            Assert.Equal(BaseArgs + " --branding-url \"https://cdn.example/logo.png?v=2\"", args);
        }

        [Theory]
        [InlineData("x\" --status-file \"C:\\some\\file\" --timeout \"0")]
        [InlineData("https://cdn.example/logo.png\" --cleanup")]
        [InlineData("https://cdn.example/logo.png --timeout 0")]
        [InlineData("https://cdn.example/lo'go.png")]
        [InlineData("https://cdn.example/logo.png\r\n")]
        [InlineData("http://cdn.example/logo.png")]
        [InlineData("file:///C:/Windows/x.png")]
        [InlineData("cdn.example/logo.png")]
        public void BuildDialogArguments_drops_unsafe_branding_url(string url)
        {
            var args = SummaryDialogLauncher.BuildDialogArguments(@"C:\x\final-status.json", 30, url, null);

            Assert.Equal(BaseArgs, args);
        }
    }
}
