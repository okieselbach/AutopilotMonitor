using System;
using System.Diagnostics;
using System.IO;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Harness
{
    /// <summary>
    /// Creates NTFS reparse points for guard tests through <c>cmd.exe /c mklink</c> — the stock
    /// System32 binary started as-is, nothing copied or renamed (EDR-neutral). Directory
    /// junctions need no privilege; file symlinks need SeCreateSymbolicLinkPrivilege or
    /// Developer Mode, so that helper reports failure instead of throwing.
    /// </summary>
    internal static class NtfsLinks
    {
        public static void CreateJunction(string link, string target)
        {
            var output = RunMklink($"/J \"{link}\" \"{target}\"");
            if (!Directory.Exists(link) || (File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
                throw new InvalidOperationException($"mklink /J failed for '{link}' -> '{target}': {output}");
        }

        public static bool TryCreateFileSymlink(string link, string target)
        {
            RunMklink($"\"{link}\" \"{target}\"");
            return File.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }

        /// <summary>Removes a junction or symlink itself, never the target's content.</summary>
        public static void RemoveLink(string link)
        {
            if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
            else if (File.Exists(link)) File.Delete(link);
        }

        private static string RunMklink(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/c mklink " + arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process == null) throw new InvalidOperationException("cmd.exe did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            return (stdout + stderr).Trim();
        }
    }
}
