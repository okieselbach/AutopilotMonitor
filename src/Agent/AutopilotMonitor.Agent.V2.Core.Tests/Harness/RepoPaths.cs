using System;
using System.IO;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Harness
{
    /// <summary>Repository paths for tests that read or write checked-in files (guards, fixtures).</summary>
    internal static class RepoPaths
    {
        /// <summary>Walks up from the test binary to the directory holding <c>AutopilotMonitor.sln</c>.</summary>
        public static string Root()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12; i++)
            {
                if (File.Exists(Path.Combine(dir, "AutopilotMonitor.sln"))) return dir;
                var parent = Directory.GetParent(dir)?.FullName;
                if (parent == null || parent == dir) break;
                dir = parent;
            }
            throw new DirectoryNotFoundException(
                "Could not locate repo root (AutopilotMonitor.sln) from " + AppContext.BaseDirectory);
        }

        /// <summary><c>tests/fixtures/&lt;name&gt;</c> under the repo root.</summary>
        public static string Fixtures(string name) => Path.Combine(Root(), "tests", "fixtures", name);
    }
}
