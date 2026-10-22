using System.Security.Principal;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Harness
{
    /// <summary>
    /// A fact that needs an elevated process, such as making the Administrators group the owner
    /// of a folder. Skipped in a non-elevated local run; the CI runners are elevated.
    /// </summary>
    public sealed class ElevatedFactAttribute : FactAttribute
    {
        public ElevatedFactAttribute()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    Skip = "needs an elevated process (assigning Administrators as the folder owner)";
            }
        }
    }
}
