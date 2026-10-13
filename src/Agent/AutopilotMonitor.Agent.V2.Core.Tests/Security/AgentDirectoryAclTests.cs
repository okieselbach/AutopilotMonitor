using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using AutopilotMonitor.Agent.V2.Core.Security;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    /// <summary>
    /// The agent data directory keeps standard users from adding files where the SYSTEM agent
    /// starts from. Works on a temp folder the test owns; as owner it can always hand itself the
    /// rights back to clean up.
    /// </summary>
    public sealed class AgentDirectoryAclTests : IDisposable
    {
        private const FileSystemRights WriteRights =
            FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes
            | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        private static readonly SecurityIdentifier Users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        private readonly string _root = Path.Combine(Path.GetTempPath(), "apm-acl-" + Guid.NewGuid().ToString("N"));

        public AgentDirectoryAclTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try
            {
                var me = WindowsIdentity.GetCurrent().User!;
                var directory = new DirectoryInfo(_root);
                var security = directory.GetAccessControl();
                security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                directory.SetAccessControl(security);
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // best effort: a leftover temp folder must not fail the run
            }
        }

        private static FileSystemRights UsersRights(string path) =>
            new DirectoryInfo(path).GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Where(r => r.AccessControlType == AccessControlType.Allow && Users.Equals(r.IdentityReference))
                .Aggregate((FileSystemRights)0, (all, r) => all | r.FileSystemRights);

        [Fact]
        public void The_protected_security_inherits_nothing_and_gives_users_read_only()
        {
            var security = AgentDirectoryAcl.BuildProtectedSecurity();

            Assert.True(security.AreAccessRulesProtected);
            var users = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Single(r => Users.Equals(r.IdentityReference));
            // An allow rule always carries Synchronize; .NET adds it on construction.
            Assert.Equal(FileSystemRights.ReadAndExecute, users.FileSystemRights & ~FileSystemRights.Synchronize);
            Assert.Equal((FileSystemRights)0, users.FileSystemRights & WriteRights);
        }

        [Fact]
        public void A_folder_is_protected_once_and_then_left_alone()
        {
            var first = AgentDirectoryAcl.EnsureProtected(_root);
            var second = AgentDirectoryAcl.EnsureProtected(_root);

            Assert.Equal(AgentDirectoryAclOutcome.Applied, first.Outcome);
            Assert.Equal(AgentDirectoryAclOutcome.AlreadyProtected, second.Outcome);
            Assert.True(AgentDirectoryAcl.IsProtectedAsExpected(new DirectoryInfo(_root).GetAccessControl(AccessControlSections.Access)));
            Assert.Equal((FileSystemRights)0, UsersRights(_root) & WriteRights);
        }

        [Fact]
        public void Existing_subfolders_take_over_the_protection()
        {
            // Install mode protects the base after Agent, Spool and Logs already exist.
            var agent = Directory.CreateDirectory(Path.Combine(_root, "Agent")).FullName;

            AgentDirectoryAcl.EnsureProtected(_root);

            Assert.Equal((FileSystemRights)0, UsersRights(agent) & WriteRights);
            Assert.NotEqual((FileSystemRights)0, UsersRights(agent) & FileSystemRights.ReadAndExecute);
        }

        [Fact]
        public void An_unprotected_or_widened_dacl_is_not_taken_as_protected()
        {
            var unprotected = AgentDirectoryAcl.BuildProtectedSecurity();
            unprotected.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            Assert.False(AgentDirectoryAcl.IsProtectedAsExpected(unprotected));

            var widened = AgentDirectoryAcl.BuildProtectedSecurity();
            widened.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Assert.False(AgentDirectoryAcl.IsProtectedAsExpected(widened));
        }

        [Fact]
        public void A_missing_folder_is_reported_not_created()
        {
            var missing = Path.Combine(_root, "does-not-exist");

            Assert.Equal(AgentDirectoryAclOutcome.Missing, AgentDirectoryAcl.EnsureProtected(missing).Outcome);
            Assert.False(Directory.Exists(missing));
        }
    }
}
