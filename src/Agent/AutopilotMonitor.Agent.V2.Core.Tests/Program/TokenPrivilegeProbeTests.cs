using AutopilotMonitor.Agent.V2;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Environment-stable pins for the token probe. Whether SeTimeZonePrivilege itself is
    /// held depends on the test runner's token, so it is NOT asserted here — only the two
    /// contracts that never vary: a privilege present in every Windows token reports held,
    /// and an unknown privilege name fails soft to "held" (the probe must never flip the
    /// launch ordering because of its own defect).
    /// </summary>
    public sealed class TokenPrivilegeProbeTests
    {
        [Fact]
        public void SeChangeNotifyPrivilege_is_held_in_every_token()
        {
            Assert.True(TokenPrivilegeProbe.IsPrivilegeHeld("SeChangeNotifyPrivilege"));
        }

        [Fact]
        public void Unknown_privilege_name_fails_soft_to_held()
        {
            Assert.True(TokenPrivilegeProbe.IsPrivilegeHeld("SeDefinitelyNotARealPrivilege"));
        }
    }
}
