using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Interop
{
    /// <summary>
    /// One member of a local group as returned by <c>NetLocalGroupGetMembers</c> level 2.
    /// <see cref="DomainAndName"/> is <c>DOMAIN\name</c> for resolvable members; for members
    /// whose SID cannot be resolved on the device (Entra role SIDs on an Entra-joined device,
    /// deleted accounts) it falls back to the SID string so the member is still visible.
    /// </summary>
    internal sealed class LocalGroupMember
    {
        public string Sid { get; set; }
        public string DomainAndName { get; set; }
        public int SidUsage { get; set; }

        /// <summary>Domain part of <see cref="DomainAndName"/> (empty when there is none).</summary>
        public string Domain
        {
            get
            {
                var idx = (DomainAndName ?? string.Empty).IndexOf('\\');
                return idx > 0 ? DomainAndName.Substring(0, idx) : string.Empty;
            }
        }

        /// <summary>Account part of <see cref="DomainAndName"/> (the whole string when there is no domain).</summary>
        public string Name
        {
            get
            {
                var value = DomainAndName ?? string.Empty;
                var idx = value.IndexOf('\\');
                return idx >= 0 && idx < value.Length - 1 ? value.Substring(idx + 1) : value;
            }
        }
    }

    /// <summary>Result of <see cref="LocalGroupNativeMethods.GetAdministratorsMembers"/>.</summary>
    internal sealed class LocalGroupMembersResult
    {
        public string GroupName { get; set; }
        public List<LocalGroupMember> Members { get; set; } = new List<LocalGroupMember>();

        /// <summary>NET_API_STATUS of the enumeration; 0 = success.</summary>
        public int ErrorCode { get; set; }

        public bool Succeeded => ErrorCode == 0;
    }

    /// <summary>
    /// Win32 P/Invoke declarations for local group membership queries
    /// (<c>NetLocalGroupGetMembers</c>). Used by <c>LocalAdminAnalyzer</c> to enumerate the
    /// members of the built-in Administrators group directly from the SAM — independent of
    /// WMI and of account enabled/disabled state, so a dormant (disabled) admin account is
    /// still reported. The group is addressed by its well-known SID (S-1-5-32-544) so the
    /// localized group name ("Administratoren", "Administrateurs") does not matter.
    /// </summary>
    internal static class LocalGroupNativeMethods
    {
        private const uint MAX_PREFERRED_LENGTH = 0xFFFFFFFF;

        /// <summary>LOCALGROUP_MEMBERS_INFO_2 — SID, SID usage and DOMAIN\name of a member.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct LOCALGROUP_MEMBERS_INFO_2
        {
            public IntPtr lgrmi2_sid;
            public int lgrmi2_sidusage;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lgrmi2_domainandname;
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetLocalGroupGetMembers(
            string servername,
            string localgroupname,
            int level,
            out IntPtr bufptr,
            uint prefmaxlen,
            out int entriesread,
            out int totalentries,
            IntPtr resumehandle);

        [DllImport("netapi32.dll")]
        private static extern int NetApiBufferFree(IntPtr buffer);

        /// <summary>
        /// Resolves the localized name of the built-in Administrators group from its well-known
        /// SID. Falls back to the English name when the translation is unavailable.
        /// </summary>
        public static string ResolveAdministratorsGroupName()
        {
            try
            {
                var sid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var account = (NTAccount)sid.Translate(typeof(NTAccount));
                var value = account.Value ?? string.Empty;          // "BUILTIN\Administrators"
                var idx = value.IndexOf('\\');
                var name = idx >= 0 && idx < value.Length - 1 ? value.Substring(idx + 1) : value;
                return string.IsNullOrEmpty(name) ? "Administrators" : name;
            }
            catch
            {
                return "Administrators";
            }
        }

        /// <summary>
        /// Enumerates the members of the built-in Administrators group on the local machine.
        /// Never throws: a failed enumeration returns an empty member list with the native
        /// error code in <see cref="LocalGroupMembersResult.ErrorCode"/> so the caller can log it.
        /// </summary>
        public static LocalGroupMembersResult GetAdministratorsMembers()
        {
            var result = new LocalGroupMembersResult { GroupName = ResolveAdministratorsGroupName() };
            var buffer = IntPtr.Zero;

            try
            {
                var status = NetLocalGroupGetMembers(
                    null, result.GroupName, 2, out buffer, MAX_PREFERRED_LENGTH,
                    out var entriesRead, out _, IntPtr.Zero);

                if (status != 0)
                {
                    result.ErrorCode = status;
                    return result;
                }

                var entrySize = Marshal.SizeOf(typeof(LOCALGROUP_MEMBERS_INFO_2));
                for (var i = 0; i < entriesRead; i++)
                {
                    var entryPtr = new IntPtr(buffer.ToInt64() + (long)i * entrySize);
                    var entry = (LOCALGROUP_MEMBERS_INFO_2)Marshal.PtrToStructure(entryPtr, typeof(LOCALGROUP_MEMBERS_INFO_2));

                    string sid = null;
                    try
                    {
                        if (entry.lgrmi2_sid != IntPtr.Zero)
                            sid = new SecurityIdentifier(entry.lgrmi2_sid).Value;
                    }
                    catch
                    {
                        // Malformed SID — keep the member with whatever name the API returned.
                    }

                    var domainAndName = entry.lgrmi2_domainandname;
                    if (string.IsNullOrEmpty(domainAndName))
                        domainAndName = sid ?? string.Empty;

                    result.Members.Add(new LocalGroupMember
                    {
                        Sid           = sid,
                        DomainAndName = domainAndName,
                        SidUsage      = entry.lgrmi2_sidusage
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                // Marshalling failure or missing export — report as a generic failure code
                // (HRESULT when available) so the analyzer logs it instead of reporting an empty group as fact.
                result.ErrorCode = ex.HResult != 0 ? ex.HResult : -1;
                result.Members.Clear();
                return result;
            }
            finally
            {
                if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
            }
        }
    }
}
