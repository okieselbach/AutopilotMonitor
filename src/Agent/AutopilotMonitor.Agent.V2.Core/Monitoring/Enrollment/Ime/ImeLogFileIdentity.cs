using System;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Which file a path holds right now: volume serial plus file index. IME rolls a log with
    /// <c>File.Move</c>, so the identity stays with the bytes when the name changes.
    /// </summary>
    internal static class ImeLogFileIdentity
    {
        /// <summary>
        /// Null when the file cannot be opened. An attribute-only handle with full sharing, so it
        /// can never make IME's rename of the log fail.
        /// </summary>
        public static string TryRead(string path)
        {
            using (var handle = FileNativeMethods.CreateFileW(
                path,
                FileNativeMethods.FILE_READ_ATTRIBUTES,
                FileNativeMethods.FILE_SHARE_READ | FileNativeMethods.FILE_SHARE_WRITE | FileNativeMethods.FILE_SHARE_DELETE,
                IntPtr.Zero,
                FileNativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero))
            {
                if (handle.IsInvalid) return null;
                if (!FileNativeMethods.GetFileInformationByHandle(handle, out var info)) return null;
                return $"{info.VolumeSerialNumber:X8}-{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
            }
        }
    }
}
