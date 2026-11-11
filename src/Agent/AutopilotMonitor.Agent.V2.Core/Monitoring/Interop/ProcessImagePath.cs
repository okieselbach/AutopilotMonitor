using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Interop
{
    /// <summary>
    /// Full image path of another process through <c>QueryFullProcessImageName</c>. It needs only
    /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> and resolves across bitness, where
    /// <c>Process.MainModule</c> throws.
    /// </summary>
    internal static class ProcessImagePath
    {
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

        /// <summary>The path, or <c>null</c> when the process cannot be opened or queried.</summary>
        public static string Query(int pid)
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
