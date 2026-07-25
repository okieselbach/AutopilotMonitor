using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Interop
{
    /// <summary>
    /// Reads the UEFI Secure Boot signature databases <c>db</c> and <c>KEK</c> directly from
    /// firmware via <c>GetFirmwareEnvironmentVariableExW</c> and checks which Microsoft
    /// signing certificates they contain. This is the authoritative source for the 2023
    /// certificate rollout state — the <c>SecureBoot\Servicing</c> registry key only reflects
    /// whether the *Windows Update* rollout ran and verified, and lags behind when the
    /// certificate reached the firmware another way (OEM factory image, WinCS, manual).
    /// <para>
    /// The certificate subject CNs are stored as ASCII bytes inside the DER-encoded
    /// signature lists, so a plain byte-substring search is sufficient — the same check
    /// Microsoft documents via <c>Get-SecureBootUEFI</c> (which wraps this exact API).
    /// </para>
    /// <para>
    /// <b>Contract:</b> observational only — never feed this into decision-engine logic.
    /// <see cref="Read"/> never throws. Reading firmware variables requires
    /// <c>SeSystemEnvironmentPrivilege</c>, which SYSTEM holds but must be enabled on the
    /// process token first; the previous privilege state is restored after the read.
    /// On legacy BIOS the API fails with <c>ERROR_INVALID_FUNCTION</c> → status
    /// <c>not_uefi</c>; every failure maps to a status string instead of an exception.
    /// </para>
    /// </summary>
    internal static class UefiSecureBootCertReader
    {
        public const string StatusOk = "ok";
        public const string StatusNotUefi = "not_uefi";
        public const string StatusVariableNotFound = "variable_not_found";
        public const string StatusPrivilegeDenied = "privilege_denied";

        // EFI_IMAGE_SECURITY_DATABASE_GUID — namespace of the "db" variable.
        private const string DbVariableGuid = "{d719b2cb-3d3a-4596-a3bc-dad00e67656f}";
        // EFI_GLOBAL_VARIABLE — namespace of the "KEK" variable.
        private const string KekVariableGuid = "{8be4df61-93ca-11d2-aa0d-00e098032b8c}";

        // Subject CNs searched for in db. 2023 set per Microsoft's June-2026 expiry guidance;
        // 2011 set kept for context (which legacy CAs are still present).
        internal const string CertWindowsUefiCa2023 = "Windows UEFI CA 2023";
        internal const string CertMicrosoftUefiCa2023 = "Microsoft UEFI CA 2023";
        internal const string CertMicrosoftOptionRomCa2023 = "Microsoft Option ROM UEFI CA 2023";
        internal const string CertWindowsProductionPca2011 = "Microsoft Windows Production PCA 2011";
        internal const string CertMicrosoftUefiCa2011 = "Microsoft Corporation UEFI CA 2011";
        // Subject CNs searched for in KEK.
        internal const string CertMicrosoftKek2kCa2023 = "Microsoft Corporation KEK 2K CA 2023";
        internal const string CertMicrosoftKekCa2011 = "Microsoft Corporation KEK CA 2011";

        private const int ErrorInvalidFunction = 1;      // legacy BIOS / no UEFI
        private const int ErrorInsufficientBuffer = 122;
        private const int ErrorEnvvarNotFound = 203;
        private const int ErrorPrivilegeNotHeld = 1314;
        private const int ErrorNotAllAssigned = 1300;

        private const int InitialBufferSize = 64 * 1024;
        private const int RetryBufferSize = 256 * 1024;

        // SeSystemEnvironmentPrivilege is adjusted on the PROCESS token, so enable → read →
        // restore is a process-wide side effect and must run as one atomic sequence. Without
        // this gate a second reader would observe "already enabled", correctly skip its own
        // restore, and then have the privilege disabled underneath it when the first reader
        // finishes — a spurious privilege_denied on a machine that actually holds it.
        private static readonly object FirmwareReadGate = new object();

        /// <summary>
        /// Reads db and KEK and evaluates the certificate presence flags. Never throws;
        /// per-variable failures surface as <see cref="UefiSecureBootCertSnapshot.DbStatus"/> /
        /// <see cref="UefiSecureBootCertSnapshot.KekStatus"/>. Certificate flags are only
        /// meaningful when the corresponding status is <see cref="StatusOk"/>.
        /// </summary>
        public static UefiSecureBootCertSnapshot Read()
        {
            var snapshot = new UefiSecureBootCertSnapshot();
            try
            {
                // The critical section covers the whole privileged window, not just the
                // adjustment — see FirmwareReadGate. Only synchronous kernel calls and byte
                // scans run inside it, so it cannot block on anything else the agent does.
                lock (FirmwareReadGate)
                using (var privilege = SystemEnvironmentPrivilege.TryEnable())
                {
                    if (!privilege.Enabled)
                    {
                        snapshot.DbStatus = StatusPrivilegeDenied;
                        snapshot.KekStatus = StatusPrivilegeDenied;
                        return snapshot;
                    }

                    byte[] db;
                    snapshot.DbStatus = TryReadVariable("db", DbVariableGuid, out db);
                    if (snapshot.DbStatus == StatusOk)
                    {
                        snapshot.DbHasWindowsUefiCa2023 = ContainsAscii(db, CertWindowsUefiCa2023);
                        snapshot.DbHasMicrosoftUefiCa2023 = ContainsAscii(db, CertMicrosoftUefiCa2023);
                        snapshot.DbHasMicrosoftOptionRomCa2023 = ContainsAscii(db, CertMicrosoftOptionRomCa2023);
                        snapshot.DbHasWindowsProductionPca2011 = ContainsAscii(db, CertWindowsProductionPca2011);
                        snapshot.DbHasMicrosoftUefiCa2011 = ContainsAscii(db, CertMicrosoftUefiCa2011);
                    }

                    byte[] kek;
                    snapshot.KekStatus = TryReadVariable("KEK", KekVariableGuid, out kek);
                    if (snapshot.KekStatus == StatusOk)
                    {
                        snapshot.KekHasMicrosoftKek2kCa2023 = ContainsAscii(kek, CertMicrosoftKek2kCa2023);
                        snapshot.KekHasMicrosoftKekCa2011 = ContainsAscii(kek, CertMicrosoftKekCa2011);
                    }
                }
            }
            catch (Exception ex)
            {
                var status = "error_" + ex.GetType().Name;
                if (snapshot.DbStatus == null) snapshot.DbStatus = status;
                if (snapshot.KekStatus == null) snapshot.KekStatus = status;
            }
            return snapshot;
        }

        /// <summary>
        /// Reads one firmware variable into <paramref name="bytes"/> (trimmed to the actual
        /// length). Returns a status string; <paramref name="bytes"/> is null unless the
        /// status is <see cref="StatusOk"/>.
        /// </summary>
        private static string TryReadVariable(string name, string guid, out byte[] bytes)
        {
            bytes = null;
            var buffer = new byte[InitialBufferSize];
            var length = NativeMethods.GetFirmwareEnvironmentVariableExW(name, guid, buffer, (uint)buffer.Length, IntPtr.Zero);
            if (length == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorInsufficientBuffer)
                {
                    buffer = new byte[RetryBufferSize];
                    length = NativeMethods.GetFirmwareEnvironmentVariableExW(name, guid, buffer, (uint)buffer.Length, IntPtr.Zero);
                    if (length == 0)
                        error = Marshal.GetLastWin32Error();
                }

                if (length == 0)
                {
                    switch (error)
                    {
                        case ErrorInvalidFunction: return StatusNotUefi;
                        case ErrorEnvvarNotFound: return StatusVariableNotFound;
                        case ErrorPrivilegeNotHeld: return StatusPrivilegeDenied;
                        default: return "error_" + error;
                    }
                }
            }

            bytes = new byte[length];
            Array.Copy(buffer, bytes, (int)length);
            return StatusOk;
        }

        /// <summary>
        /// Byte-level ASCII substring search (ordinal, case-sensitive). The signature lists
        /// are at most a few hundred KB and read once per agent start — a naive scan is fine.
        /// </summary>
        internal static bool ContainsAscii(byte[] haystack, string needle)
        {
            if (haystack == null || string.IsNullOrEmpty(needle) || needle.Length > haystack.Length)
                return false;

            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var j = 0;
                while (j < needle.Length && haystack[i + j] == (byte)needle[j])
                    j++;
                if (j == needle.Length)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Enables <c>SeSystemEnvironmentPrivilege</c> on the process token for the lifetime
        /// of the instance and restores the previous privilege state on dispose. SYSTEM holds
        /// the privilege (default-disabled); without elevation <see cref="Enabled"/> stays
        /// false and the caller reports <see cref="StatusPrivilegeDenied"/>.
        /// <para>
        /// <b>Crash-safety.</b> This is the repo's only privilege-adjustment code, so the
        /// ownership rules are spelled out: the token is held in a <see cref="SafeHandle"/>
        /// assigned by the P/Invoke marshaler, so the CLR owns it from the moment
        /// <c>OpenProcessToken</c> returns — it is released by the critical finalizer even if
        /// this instance never reaches the caller's <c>using</c> (async exception between the
        /// call and the assignment). <see cref="Dispose"/> is idempotent, never throws, and
        /// releases the handle in a <c>finally</c> so a failed restore cannot leak it. The
        /// restore obligation is recorded immediately after the successful adjustment, so no
        /// throw in between can strand the process with the privilege left enabled.
        /// </para>
        /// </summary>
        private sealed class SystemEnvironmentPrivilege : IDisposable
        {
            public bool Enabled { get; private set; }

            private SafeTokenHandle _token;
            private NativeMethods.TOKEN_PRIVILEGES _previousState;
            private bool _restoreNeeded;
            private bool _disposed;

            public static SystemEnvironmentPrivilege TryEnable()
            {
                var instance = new SystemEnvironmentPrivilege();
                try
                {
                    SafeTokenHandle token;
                    if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(),
                            NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY, out token))
                    {
                        // Failed open still yields a (closed/invalid) SafeHandle instance.
                        if (token != null) token.Dispose();
                        return instance;
                    }

                    instance._token = token;

                    NativeMethods.LUID luid;
                    if (!NativeMethods.LookupPrivilegeValueW(null, "SeSystemEnvironmentPrivilege", out luid))
                        return instance;

                    var newState = new NativeMethods.TOKEN_PRIVILEGES
                    {
                        PrivilegeCount = 1,
                        Luid = luid,
                        Attributes = NativeMethods.SE_PRIVILEGE_ENABLED
                    };

                    NativeMethods.TOKEN_PRIVILEGES previousState;
                    uint returnLength;
                    if (!NativeMethods.AdjustTokenPrivileges(token, false, ref newState,
                            (uint)Marshal.SizeOf(typeof(NativeMethods.TOKEN_PRIVILEGES)),
                            out previousState, out returnLength))
                        return instance;

                    // AdjustTokenPrivileges returns TRUE even when the privilege is not held —
                    // the real verdict is ERROR_NOT_ALL_ASSIGNED in the last error. Nothing was
                    // adjusted in that case, so there is nothing to restore.
                    if (Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
                        return instance;

                    // The token is now modified: record the restore obligation before anything
                    // else can throw. Restore only when the privilege was not already enabled.
                    instance._previousState = previousState;
                    instance._restoreNeeded = previousState.PrivilegeCount > 0
                        && (previousState.Attributes & NativeMethods.SE_PRIVILEGE_ENABLED) == 0;
                    instance.Enabled = true;
                }
                catch
                {
                    // Fail-soft: Enabled stays false. Whatever was acquired before the failure
                    // is already tracked on the instance and released by Dispose.
                }
                return instance;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                Enabled = false;

                var token = _token;
                _token = null;
                if (token == null) return;

                try
                {
                    if (_restoreNeeded)
                    {
                        _restoreNeeded = false;
                        NativeMethods.TOKEN_PRIVILEGES ignored;
                        uint returnLength;
                        NativeMethods.AdjustTokenPrivileges(token, false, ref _previousState,
                            (uint)Marshal.SizeOf(typeof(NativeMethods.TOKEN_PRIVILEGES)),
                            out ignored, out returnLength);
                    }
                }
                catch
                {
                    // Best effort — the process token privilege state is not load-bearing for
                    // anything else the agent does, and Dispose must never throw.
                }
                finally
                {
                    token.Dispose();
                }
            }
        }

        /// <summary>
        /// Owns a process-token handle. <c>ReleaseHandle</c> runs on the finalizer thread when
        /// <see cref="SystemEnvironmentPrivilege.Dispose"/> is skipped, so it must not throw —
        /// a single <c>CloseHandle</c> P/Invoke satisfies that.
        /// </summary>
        private sealed class SafeTokenHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeTokenHandle() : base(true) { }

            protected override bool ReleaseHandle()
            {
                return NativeMethods.CloseHandle(handle);
            }
        }

        private static class NativeMethods
        {
            public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
            public const uint TOKEN_QUERY = 0x0008;
            public const uint SE_PRIVILEGE_ENABLED = 0x0002;

            [StructLayout(LayoutKind.Sequential)]
            public struct LUID
            {
                public uint LowPart;
                public int HighPart;
            }

            // Single-privilege variant of the variable-length native struct — sufficient here
            // because exactly one privilege is adjusted per call.
            [StructLayout(LayoutKind.Sequential)]
            public struct TOKEN_PRIVILEGES
            {
                public uint PrivilegeCount;
                public LUID Luid;
                public uint Attributes;
            }

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern uint GetFirmwareEnvironmentVariableExW(
                string lpName, string lpGuid, byte[] pBuffer, uint nSize, IntPtr pdwAttributes);

            [DllImport("kernel32.dll")]
            public static extern IntPtr GetCurrentProcess();

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeTokenHandle tokenHandle);

            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool LookupPrivilegeValueW(string lpSystemName, string lpName, out LUID lpLuid);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool AdjustTokenPrivileges(SafeTokenHandle tokenHandle, bool disableAllPrivileges,
                ref TOKEN_PRIVILEGES newState, uint bufferLength, out TOKEN_PRIVILEGES previousState, out uint returnLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr handle);
        }
    }

    /// <summary>
    /// Result of one firmware read. Certificate flags are only meaningful when the
    /// corresponding status equals <see cref="UefiSecureBootCertReader.StatusOk"/>.
    /// </summary>
    internal sealed class UefiSecureBootCertSnapshot
    {
        public string DbStatus { get; set; }
        public string KekStatus { get; set; }

        public bool DbHasWindowsUefiCa2023 { get; set; }
        public bool DbHasMicrosoftUefiCa2023 { get; set; }
        public bool DbHasMicrosoftOptionRomCa2023 { get; set; }
        public bool DbHasWindowsProductionPca2011 { get; set; }
        public bool DbHasMicrosoftUefiCa2011 { get; set; }

        public bool KekHasMicrosoftKek2kCa2023 { get; set; }
        public bool KekHasMicrosoftKekCa2011 { get; set; }
    }
}
