using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Keeps <c>%ProgramData%\AutopilotMonitor</c> and the Agent folder the SYSTEM task runs from
    /// out of reach of standard users. Left alone the folder inherits ProgramData's "Users may
    /// create files and folders" entry: a user who can add a file where the agent starts can plant
    /// code the next SYSTEM start loads. During the device phase of OOBE only SYSTEM is around, but
    /// the agent keeps running through the user phase and on the desktop until the enrollment
    /// ends, restarted by the boot task after every reboot; that is the window this closes. The
    /// payload catalog does not cover it: it is checked at install time, a file added later is
    /// never looked at.
    /// <para>
    /// The DACL is protected (nothing inherited from ProgramData): SYSTEM and Administrators full
    /// control, Users read and execute, all inherited by the subfolders. The owner is Administrators:
    /// the owner of a folder can always hand itself the rights back, so a folder a standard user
    /// created first stays theirs until the owner changes, whatever the DACL says. A junction or
    /// symbolic link is never protected or followed.
    /// </para>
    /// </summary>
    public static class AgentDirectoryAcl
    {
        private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        private static readonly SecurityIdentifier LocalSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier Administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier Users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        /// <summary>
        /// Applies the protected DACL and the owner to <paramref name="path"/> unless both are already
        /// in place. A reparse point is refused, not followed. Never throws: a folder the agent cannot
        /// protect must not crash the agent; the caller decides what the failure means.
        /// </summary>
        public static AgentDirectoryAclResult EnsureProtected(string path)
        {
            try
            {
                var directory = new DirectoryInfo(path);
                if (!directory.Exists)
                    return new AgentDirectoryAclResult(AgentDirectoryAclOutcome.Missing, null);
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    return new AgentDirectoryAclResult(AgentDirectoryAclOutcome.Failed, $"{path} is a junction or symbolic link, not a folder");

                if (IsProtectedAsExpected(directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)))
                    return new AgentDirectoryAclResult(AgentDirectoryAclOutcome.AlreadyProtected, null);

                directory.SetAccessControl(BuildProtectedSecurity());
                return new AgentDirectoryAclResult(AgentDirectoryAclOutcome.Applied, null);
            }
            catch (Exception ex)
            {
                return new AgentDirectoryAclResult(AgentDirectoryAclOutcome.Failed, ex.Message);
            }
        }

        /// <summary>
        /// Creates the folder with the protected DACL and owner in one step, so there is no moment
        /// in which it carries ProgramData's inherited entries.
        /// </summary>
        public static void CreateProtected(string path) => Directory.CreateDirectory(path, BuildProtectedSecurity());

        internal static DirectorySecurity BuildProtectedSecurity()
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
            security.SetOwner(Administrators);
            return security;
        }

        /// <summary>
        /// True when the owner is SYSTEM or Administrators and the DACL is protected and holds exactly
        /// the three entries above. Windows adds <see cref="FileSystemRights.Synchronize"/> when it
        /// stores an entry, so that bit is ignored.
        /// </summary>
        internal static bool IsProtectedAsExpected(DirectorySecurity security)
        {
            if (!(security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner)
                || !(owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
                return false;

            if (!security.AreAccessRulesProtected)
                return false;

            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToList();

            return rules.Count == 3
                && Has(rules, LocalSystem, FileSystemRights.FullControl)
                && Has(rules, Administrators, FileSystemRights.FullControl)
                && Has(rules, Users, FileSystemRights.ReadAndExecute);
        }

        private static bool Has(IEnumerable<FileSystemAccessRule> rules, SecurityIdentifier sid, FileSystemRights rights) =>
            rules.Any(r => r.AccessControlType == AccessControlType.Allow
                           && !r.IsInherited
                           && sid.Equals(r.IdentityReference)
                           && (r.FileSystemRights & ~FileSystemRights.Synchronize) == (rights & ~FileSystemRights.Synchronize)
                           && r.InheritanceFlags == Inherit
                           && r.PropagationFlags == PropagationFlags.None);
    }

    public enum AgentDirectoryAclOutcome
    {
        Applied,
        AlreadyProtected,
        Missing,
        Failed,
    }

    /// <summary>Outcome of <see cref="AgentDirectoryAcl.EnsureProtected"/>; <see cref="Error"/> only on failure.</summary>
    public sealed class AgentDirectoryAclResult
    {
        public AgentDirectoryAclResult(AgentDirectoryAclOutcome outcome, string error)
        {
            Outcome = outcome;
            Error = error;
        }

        public AgentDirectoryAclOutcome Outcome { get; }
        public string Error { get; }
    }
}
