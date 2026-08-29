using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using Microsoft.Win32.SafeHandles;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime
{
    /// <summary>
    /// A diagnostics source folder held open for one section build, and the only way the
    /// package reads bytes out of it.
    ///
    /// The path guards (<see cref="Telemetry.Gather.DiagnosticsPathGuards"/>) and the
    /// enumeration's reparse-point skip are check-then-act: they judge a path string, and the
    /// bytes are read from that string later. Under any folder a local user can write to, a
    /// subdirectory can become a junction between the check and the read, and the copy would
    /// follow it into whatever SYSTEM can read. This class puts both decisions on the handle
    /// that is actually read:
    ///
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="TryOpen"/> opens the folder once. The handle's final path
    ///     (<c>GetFinalPathNameByHandle</c> — every reparse point resolved) must equal the
    ///     validated lexical path, otherwise a junction or symlink sits somewhere in the chain
    ///     and the whole folder is rejected. The handle stays open, without a delete share,
    ///     until <see cref="Dispose"/>: NTFS refuses to rename or delete a directory while it
    ///     or anything beneath it is open, so the validated chain cannot be swapped underneath
    ///     the build either.
    ///   </item>
    ///   <item>
    ///     <see cref="TryOpenFile"/> opens each candidate with <c>FILE_FLAG_OPEN_REPARSE_POINT</c>
    ///     (a symlink at the final component is opened as itself and refused) and requires the
    ///     handle's final path to be exactly <c>&lt;canonical folder&gt;\&lt;relative path&gt;</c>.
    ///     A subdirectory that became a junction after enumeration resolves elsewhere and the
    ///     file is skipped. The stream handed back wraps that same validated handle.
    ///   </item>
    /// </list>
    ///
    /// Neither fact is re-derived from a path once the handle exists, so there is no window
    /// between validation and use. Hard links are out of scope: Windows refuses to create one
    /// to a file the caller cannot write.
    /// </summary>
    internal sealed class PinnedSourceFolder : IDisposable
    {
        public const string RejectMissing = "folder missing";
        public const string RejectNotDirectory = "not a directory";
        public const string RejectNotFile = "not a file";
        public const string RejectReparsePoint = "reparse point";
        public const string RejectResolvedElsewhere = "resolved outside validated folder";
        public const string RejectOutsideFolder = "outside pinned folder";

        private readonly SafeFileHandle _handle;

        /// <summary>Full lexical path the caller validated, without a trailing separator.</summary>
        public string LexicalPath { get; }

        /// <summary>
        /// Where the pinned handle really points; equal to <see cref="LexicalPath"/> up to
        /// case and 8.3 expansion, as verified by <see cref="TryOpen"/>.
        /// </summary>
        public string CanonicalPath { get; }

        private PinnedSourceFolder(SafeFileHandle handle, string lexicalPath, string canonicalPath)
        {
            _handle = handle;
            LexicalPath = lexicalPath;
            CanonicalPath = canonicalPath;
        }

        /// <summary>
        /// Pins <paramref name="folder"/>. Returns null with a rejection reason (one of the
        /// <c>Reject*</c> constants or an open/query error) when the folder is missing, is not
        /// a directory, is a reparse point itself, or is reached through one.
        /// </summary>
        public static PinnedSourceFolder TryOpen(string folder, out string rejection)
        {
            string lexical;
            try
            {
                lexical = TrimSeparators(Path.GetFullPath(folder));
            }
            catch (Exception ex)
            {
                rejection = $"invalid path: {ex.Message}";
                return null;
            }

            if (!Directory.Exists(lexical))
            {
                rejection = File.Exists(lexical) ? RejectNotDirectory : RejectMissing;
                return null;
            }

            SafeFileHandle handle = null;
            try
            {
                // FILE_LIST_DIRECTORY (a data access) is what makes this handle count for share
                // tracking: an attribute-only open would neither block nor be blocked, and a
                // rename could slip past the missing delete share.
                handle = FileNativeMethods.CreateFileW(
                    lexical,
                    FileNativeMethods.FILE_LIST_DIRECTORY | FileNativeMethods.FILE_READ_ATTRIBUTES,
                    FileNativeMethods.FILE_SHARE_READ | FileNativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    FileNativeMethods.OPEN_EXISTING,
                    FileNativeMethods.FILE_FLAG_BACKUP_SEMANTICS | FileNativeMethods.FILE_FLAG_OPEN_REPARSE_POINT,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    rejection = "open failed: " + DescribeLastError();
                    return null;
                }

                if (!FileNativeMethods.GetFileInformationByHandle(handle, out var info))
                {
                    rejection = "attributes unavailable: " + DescribeLastError();
                    return null;
                }
                if ((info.FileAttributes & FileNativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                {
                    rejection = RejectReparsePoint;
                    return null;
                }
                if ((info.FileAttributes & FileNativeMethods.FILE_ATTRIBUTE_DIRECTORY) == 0)
                {
                    rejection = RejectNotDirectory;
                    return null;
                }

                var canonical = TryGetFinalPath(handle, out var finalPathError);
                if (canonical == null)
                {
                    rejection = "final path unavailable: " + finalPathError;
                    return null;
                }

                // The lexical path may carry 8.3 components (%TEMP%-style expansions); a final
                // path never does. Expand before comparing so a short name is not mistaken for
                // a reparse point — a failed expansion keeps the raw string and fails closed.
                var expected = TryGetLongPath(lexical) ?? lexical;
                if (!PathEquals(canonical, expected))
                {
                    rejection = RejectResolvedElsewhere;
                    return null;
                }

                var pinned = new PinnedSourceFolder(handle, lexical, canonical);
                handle = null; // owned by the instance from here on
                rejection = null;
                return pinned;
            }
            catch (Exception ex)
            {
                rejection = "open failed: " + ex.Message;
                return null;
            }
            finally
            {
                handle?.Dispose();
            }
        }

        /// <summary>
        /// Opens <paramref name="candidatePath"/> (a lexical descendant of the pinned folder, as
        /// produced by enumeration) for reading. Returns null with a rejection reason when the
        /// candidate lies outside the folder, is a reparse point, is not a file, or — the race
        /// this class exists for — its handle resolves anywhere other than
        /// <c>&lt;canonical folder&gt;\&lt;relative path&gt;</c>.
        /// </summary>
        public SourceFile TryOpenFile(string candidatePath, out string rejection)
        {
            if (!TryGetRelativePath(candidatePath, out var relative))
            {
                rejection = RejectOutsideFolder;
                return null;
            }
            var expected = CanonicalPath + Path.DirectorySeparatorChar + relative;

            SafeFileHandle handle = null;
            try
            {
                handle = FileNativeMethods.CreateFileW(
                    candidatePath,
                    FileNativeMethods.GENERIC_READ,
                    FileNativeMethods.FILE_SHARE_READ | FileNativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    FileNativeMethods.OPEN_EXISTING,
                    FileNativeMethods.FILE_FLAG_OPEN_REPARSE_POINT | FileNativeMethods.FILE_FLAG_SEQUENTIAL_SCAN,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    rejection = "open failed: " + DescribeLastError();
                    return null;
                }

                if (!FileNativeMethods.GetFileInformationByHandle(handle, out var info))
                {
                    rejection = "attributes unavailable: " + DescribeLastError();
                    return null;
                }
                if ((info.FileAttributes & FileNativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                {
                    rejection = RejectReparsePoint;
                    return null;
                }
                if ((info.FileAttributes & FileNativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0)
                {
                    rejection = RejectNotFile;
                    return null;
                }

                var finalPath = TryGetFinalPath(handle, out var finalPathError);
                if (finalPath == null)
                {
                    rejection = "final path unavailable: " + finalPathError;
                    return null;
                }
                if (!PathEquals(finalPath, expected))
                {
                    rejection = RejectResolvedElsewhere;
                    return null;
                }

                var length = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
                var stream = new FileStream(handle, FileAccess.Read, 81920);
                handle = null; // owned by the stream from here on
                rejection = null;
                return new SourceFile(stream, length);
            }
            catch (Exception ex)
            {
                rejection = "open failed: " + ex.Message;
                return null;
            }
            finally
            {
                handle?.Dispose();
            }
        }

        public void Dispose() => _handle.Dispose();

        private bool TryGetRelativePath(string candidatePath, out string relative)
        {
            relative = null;
            string full;
            try
            {
                full = Path.GetFullPath(candidatePath);
            }
            catch
            {
                return false;
            }

            if (full.Length <= LexicalPath.Length + 1) return false;
            if (!full.StartsWith(LexicalPath, StringComparison.OrdinalIgnoreCase)) return false;
            if (full[LexicalPath.Length] != Path.DirectorySeparatorChar) return false;

            relative = full.Substring(LexicalPath.Length + 1);
            return relative.Length > 0 && relative[relative.Length - 1] != Path.DirectorySeparatorChar;
        }

        /// <summary>Final (reparse-free, long-name) DOS path of an open handle, or null with the Win32 error.</summary>
        private static string TryGetFinalPath(SafeFileHandle handle, out string error)
        {
            var buffer = new StringBuilder(512);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var length = FileNativeMethods.GetFinalPathNameByHandleW(
                    handle, buffer, (uint)buffer.Capacity, FileNativeMethods.FINAL_PATH_NORMALIZED_DOS);
                if (length == 0)
                {
                    error = DescribeLastError();
                    return null;
                }
                if (length < buffer.Capacity)
                {
                    error = null;
                    return StripExtendedPrefix(buffer.ToString());
                }
                buffer.EnsureCapacity((int)length + 1);
            }
            error = "path longer than the retry buffer";
            return null;
        }

        /// <summary>Long-name form of a path (8.3 components expanded), or null when Windows cannot resolve it.</summary>
        internal static string TryGetLongPath(string path)
        {
            var buffer = new StringBuilder(512);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var length = FileNativeMethods.GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
                if (length == 0) return null;
                if (length < buffer.Capacity) return buffer.ToString();
                buffer.EnsureCapacity((int)length + 1);
            }
            return null;
        }

        private static string StripExtendedPrefix(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(8);
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path.Substring(4);
            return path;
        }

        private static string TrimSeparators(string path) =>
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static bool PathEquals(string a, string b) =>
            string.Equals(TrimSeparators(a), TrimSeparators(b), StringComparison.OrdinalIgnoreCase);

        private static string DescribeLastError()
        {
            var code = Marshal.GetLastWin32Error();
            return $"{new Win32Exception(code).Message} (win32 {code})";
        }
    }

    /// <summary>
    /// A readable source for one archive entry: the stream and the length the caps are charged
    /// with. From <see cref="PinnedSourceFolder.TryOpenFile"/> the stream is the validated
    /// handle itself.
    /// </summary>
    internal sealed class SourceFile : IDisposable
    {
        public Stream Stream { get; }
        public long Length { get; }

        public SourceFile(Stream stream, long length)
        {
            Stream = stream;
            Length = length;
        }

        /// <summary>
        /// A file the agent wrote itself — an event-log export under an unguessable name in
        /// SYSTEM's temp folder — never a tenant-configured location, so a plain open is enough.
        /// </summary>
        public static SourceFile OpenAgentOwned(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new SourceFile(stream, stream.Length);
        }

        public void Dispose() => Stream.Dispose();
    }
}
