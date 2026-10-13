using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Keeps <c>%ProgramData%\AutopilotMonitor</c> out of reach of standard users. Left alone the
    /// folder inherits ProgramData's "Users may create files and folders" entry, and the SYSTEM
    /// agent and its boot task start from the Agent subfolder: a user who can add a file there
    /// can plant code the next SYSTEM start loads. During the device phase of OOBE only SYSTEM is
    /// around, but the agent keeps running through the user phase and on the desktop until the
    /// enrollment ends, restarted by the boot task after every reboot; that is the window this
    /// closes. The payload catalog does not cover it: it is checked at install time, a file
    /// added later is never looked at.
    /// <para>
    /// The DACL is protected (nothing inherited from ProgramData): SYSTEM and Administrators full
    /// control, Users read and execute, all inherited by the subfolders. Explicit entries on a
    /// child stay. The summary dialog's temp folder lives outside this tree anyway.
    /// </para>
    /// </summary>
    public static class AgentDirectoryAcl
    {
        private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        private static readonly SecurityIdentifier LocalSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier Administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier Users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        /// <summary>
        /// Applies the protected DACL to <paramref name="path"/> unless it is already in place.
        /// Never throws: a folder the agent cannot protect must not stop the agent.
        /// </summary>
        public static AgentDirectoryAclResult EnsureProtected(string path)
        {
            try
            {
                var directory = new DirectoryInfo(path);
                if (!directory.Exists)
                    return new AgentDirectoryAclResult(AgentDirectoryAclOutcome.Missing, null);

                if (IsProtectedAsExpected(directory.GetAccessControl(AccessControlSections.Access)))
                    return new AgentDirectoryAclResult(AgentDirectoryAclOutcome.AlreadyProtected, null);

                directory.SetAccessControl(BuildProtectedSecurity());
                return new AgentDirectoryAclResult(AgentDirectoryAclOutcome.Applied, null);
            }
            catch (Exception ex)
            {
                return new AgentDirectoryAclResult(AgentDirectoryAclOutcome.Failed, ex.Message);
            }
        }

        internal static DirectorySecurity BuildProtectedSecurity()
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
            return security;
        }

        /// <summary>
        /// True when the DACL is protected and holds exactly the three entries above. Windows adds
        /// <see cref="FileSystemRights.Synchronize"/> when it stores an entry, so that bit is ignored.
        /// </summary>
        internal static bool IsProtectedAsExpected(DirectorySecurity security)
        {
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
