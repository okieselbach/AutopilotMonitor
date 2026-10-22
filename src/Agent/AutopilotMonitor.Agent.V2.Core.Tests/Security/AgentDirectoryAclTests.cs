using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    /// <summary>
    /// The agent folders keep standard users from adding files where the SYSTEM agent starts from.
    /// Works on a temp folder the test owns; as owner, or as an elevated administrator, it can
    /// always hand itself the rights back to clean up. Tests that assign the Administrators group
    /// as owner need an elevated process and skip without one.
    /// </summary>
    public sealed class AgentDirectoryAclTests : IDisposable
    {
        private const FileSystemRights WriteRights =
            FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes
            | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        private static readonly SecurityIdentifier Users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        private static readonly SecurityIdentifier Administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

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

        private static SecurityIdentifier OwnerOf(string path) =>
            (SecurityIdentifier)new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));

        [Fact]
        public void The_protected_security_inherits_nothing_gives_users_read_only_and_names_administrators_as_owner()
        {
            var security = AgentDirectoryAcl.BuildProtectedSecurity();

            Assert.True(security.AreAccessRulesProtected);
            var users = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Single(r => Users.Equals(r.IdentityReference));
            // An allow rule always carries Synchronize; .NET adds it on construction.
            Assert.Equal(FileSystemRights.ReadAndExecute, users.FileSystemRights & ~FileSystemRights.Synchronize);
            Assert.Equal((FileSystemRights)0, users.FileSystemRights & WriteRights);
            Assert.Equal(Administrators, security.GetOwner(typeof(SecurityIdentifier)));
        }

        [ElevatedFact]
        public void A_folder_is_protected_once_and_then_left_alone()
        {
            var first = AgentDirectoryAcl.EnsureProtected(_root);
            var second = AgentDirectoryAcl.EnsureProtected(_root);

            Assert.Equal(AgentDirectoryAclOutcome.Applied, first.Outcome);
            Assert.Equal(AgentDirectoryAclOutcome.AlreadyProtected, second.Outcome);
            Assert.True(AgentDirectoryAcl.IsProtectedAsExpected(new DirectoryInfo(_root).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)));
            Assert.Equal((FileSystemRights)0, UsersRights(_root) & WriteRights);
            Assert.Equal(Administrators, OwnerOf(_root));
        }

        // The owner can always hand itself the rights back, so a folder another account created
        // first is theirs until the owner changes, whatever its DACL says.
        [ElevatedFact]
        public void A_folder_owned_by_another_account_gets_administrators_as_owner()
        {
            var directory = new DirectoryInfo(_root);
            var mine = directory.GetAccessControl(AccessControlSections.Owner);
            mine.SetOwner(WindowsIdentity.GetCurrent().User!);
            directory.SetAccessControl(mine);
            Assert.NotEqual(Administrators, OwnerOf(_root));

            var result = AgentDirectoryAcl.EnsureProtected(_root);

            Assert.Equal(AgentDirectoryAclOutcome.Applied, result.Outcome);
            Assert.Equal(Administrators, OwnerOf(_root));
        }

        [ElevatedFact]
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
        public void A_dacl_under_another_owner_is_not_taken_as_protected()
        {
            var mine = AgentDirectoryAcl.BuildProtectedSecurity();
            mine.SetOwner(WindowsIdentity.GetCurrent().User!);

            Assert.False(AgentDirectoryAcl.IsProtectedAsExpected(mine));
        }

        [Fact]
        public void A_junction_is_refused_and_its_target_left_alone()
        {
            var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
            var link = Path.Combine(_root, "link");
            NtfsLinks.CreateJunction(link, target);
            try
            {
                var result = AgentDirectoryAcl.EnsureProtected(link);

                Assert.Equal(AgentDirectoryAclOutcome.Failed, result.Outcome);
                Assert.Contains("junction", result.Error);
                Assert.False(new DirectoryInfo(target).GetAccessControl().AreAccessRulesProtected);
            }
            finally
            {
                NtfsLinks.RemoveLink(link);
            }
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
