using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Identity facts about one process named <c>IntuneManagementExtension</c>, as observed by
    /// <see cref="ImeProcessWatcher"/> before it decides whether to attach.
    /// </summary>
    public readonly struct ImeProcessCandidate
    {
        public ImeProcessCandidate(int pid, int sessionId, string imagePath, DateTime startTimeUtc)
        {
            Pid = pid;
            SessionId = sessionId;
            ImagePath = imagePath;
            StartTimeUtc = startTimeUtc;
        }

        public int Pid { get; }
        /// <summary>Terminal-services session; the IME service runs in session 0. -1 = unknown.</summary>
        public int SessionId { get; }
        /// <summary>Full image path (Win32 form), or null when it could not be resolved.</summary>
        public string ImagePath { get; }
        public DateTime StartTimeUtc { get; }
    }

    /// <summary>
    /// Process-name matching is not a protected namespace: during AccountSetup / Device Preparation
    /// a standard user session is live and can start any binary renamed to
    /// <c>IntuneManagementExtension.exe</c> (CWE-807 — the same class as the agent instance guard).
    /// A watcher that attaches to the first name match would (a) report a forged
    /// <c>ime_process_exited</c> Warning into the tenant timeline and (b) stop discovering the real
    /// service. This helper establishes identity by properties a standard user cannot forge:
    /// the process runs in session 0 (services only) AND its image lives under the IME install
    /// root (<c>%ProgramFiles(x86)%\Microsoft Intune Management Extension\</c>, admin-writable only).
    /// </summary>
    public static class ImeProcessIdentity
    {
        public const string InstallFolderName = "Microsoft Intune Management Extension";

        /// <summary>
        /// Install roots the IME image may live under. IME is a 32-bit service, so on x64/ARM64 it is
        /// <c>%ProgramFiles(x86)%</c>; <c>%ProgramFiles%</c> is included for completeness (x86 OS,
        /// or a 32-bit agent host whose ProgramFiles already resolves to the (x86) folder).
        /// </summary>
        public static IReadOnlyList<string> DefaultTrustedRoots()
        {
            var roots = new List<string>(2);
            foreach (var env in new[] { "ProgramFiles(x86)", "ProgramW6432", "ProgramFiles" })
            {
                var basePath = Environment.GetEnvironmentVariable(env);
                if (string.IsNullOrEmpty(basePath)) continue;
                var root = Path.Combine(basePath, InstallFolderName);
                if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                    roots.Add(root);
            }
            return roots;
        }

        /// <summary>
        /// True when the candidate is trustworthy as the real IME service process: session 0 and an
        /// image path directly under one of <paramref name="trustedRoots"/>. Unknown session
        /// (-1) or an unresolvable image path is NOT trusted — a false negative only delays
        /// attach by one discovery tick, a false positive mutes the signal for the session.
        /// Path comparison is segment-aware (<c>...Extension\</c> prefix), so a sibling folder
        /// like <c>Microsoft Intune Management Extension2\</c> does not pass.
        /// </summary>
        public static bool IsTrusted(ImeProcessCandidate candidate, IReadOnlyList<string> trustedRoots)
        {
            if (candidate.SessionId != 0) return false;
            if (string.IsNullOrEmpty(candidate.ImagePath) || trustedRoots == null) return false;

            string full;
            try { full = Path.GetFullPath(candidate.ImagePath); }
            catch { return false; }

            // Only the exact on-disk name — a differently-cased extension or a trailing-space
            // variant would still be a name match for GetProcessesByName but is not the service.
            if (!string.Equals(Path.GetFileName(full), "IntuneManagementExtension.exe", StringComparison.OrdinalIgnoreCase))
                return false;

            foreach (var root in trustedRoots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string rootFull;
                try { rootFull = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar; }
                catch { continue; }
                if (full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Picks the candidate to attach to: the oldest trusted process. The service instance is
        /// started by the SCM long before any transient tool could appear under the same trusted
        /// root, and when IME restarts the old instance is already gone from the list.
        /// Returns null when nothing is trusted.
        /// </summary>
        public static ImeProcessCandidate? SelectPreferred(IEnumerable<ImeProcessCandidate> candidates, IReadOnlyList<string> trustedRoots)
        {
            ImeProcessCandidate? best = null;
            foreach (var c in candidates)
            {
                if (!IsTrusted(c, trustedRoots)) continue;
                if (best == null || c.StartTimeUtc < best.Value.StartTimeUtc)
                    best = c;
            }
            return best;
        }

        /// <summary>
        /// Reads the identity facts of a live process. Uses <c>QueryFullProcessImageName</c> so the
        /// path resolves across bitness (IME is 32-bit; the agent may be 64-bit) — <c>MainModule</c>
        /// throws in that case. Any failure yields an untrusted candidate (session -1 / null path),
        /// never an exception.
        /// </summary>
        public static ImeProcessCandidate Probe(Process process)
        {
            int pid = -1, session = -1;
            string path = null;
            DateTime start = DateTime.MaxValue;

            try { pid = process.Id; } catch { }
            try { session = process.SessionId; } catch { }
            try { start = process.StartTime.ToUniversalTime(); } catch { }
            try { path = QueryImagePath(pid); } catch { }
            if (path == null)
            {
                try { path = process.MainModule?.FileName; } catch { }
            }

            return new ImeProcessCandidate(pid, session, path, start);
        }

        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

        private static string QueryImagePath(int pid)
        {
            if (pid <= 0) return null;
            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(handle, 0, sb, ref size) && size > 0 ? sb.ToString(0, size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}
