using System;
using System.IO;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Resolved absolute paths to Windows system binaries.
    ///
    /// Rationale: launching processes by bare name (e.g. "cmd.exe", "tzutil") lets
    /// Windows walk the PATH to find them. As SYSTEM this is a DLL/binary-hijacking
    /// vector — an attacker able to plant a file in any PATH entry ahead of System32
    /// gets code execution with SYSTEM privileges. Always use these resolved paths.
    ///
    /// <see cref="Environment.SystemDirectory"/> respects WOW64 redirection
    /// automatically:
    ///   - 64-bit process on any Windows → C:\Windows\System32
    ///   - 32-bit process on 64-bit Windows → C:\Windows\SysWOW64
    /// The agent ships as AnyCPU/net48 and runs 64-bit on 64-bit Windows, so this
    /// resolves to the real System32. It also honors non-default %SystemRoot%
    /// (rare, but seen in custom OEM images on drives other than C:).
    ///
    /// All listed binaries are in-box on every supported Windows version
    /// (Windows 7 / 8.1 / 10 / 11 / Server 2012R2 – 2025).
    /// </summary>
    public static class SystemPaths
    {
        private static readonly string System32 = Environment.SystemDirectory;

        /// <summary>C:\Windows\System32\cmd.exe</summary>
        public static readonly string Cmd = Path.Combine(System32, "cmd.exe");

        /// <summary>C:\Windows\System32\shutdown.exe</summary>
        public static readonly string Shutdown = Path.Combine(System32, "shutdown.exe");

        /// <summary>C:\Windows\System32\tzutil.exe</summary>
        public static readonly string TzUtil = Path.Combine(System32, "tzutil.exe");

        /// <summary>C:\Windows\System32\netsh.exe</summary>
        public static readonly string Netsh = Path.Combine(System32, "netsh.exe");

        /// <summary>C:\Windows\System32\schtasks.exe</summary>
        public static readonly string Schtasks = Path.Combine(System32, "schtasks.exe");

        /// <summary>
        /// C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe
        /// Note: the "v1.0" folder is historical — it hosts every in-box PowerShell
        /// version including 5.1 on Windows 10/11. Do not "fix" it to v5.1.
        /// </summary>
        public static readonly string PowerShell = Path.Combine(System32, "WindowsPowerShell", "v1.0", "powershell.exe");
    }
}
