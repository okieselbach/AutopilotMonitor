using System.Runtime.CompilerServices;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;

// Polyfill — net48 does not ship ModuleInitializerAttribute. The C# 9+ compiler will still
// invoke any method tagged with an attribute named exactly
// System.Runtime.CompilerServices.ModuleInitializerAttribute at module load time, regardless
// of where the type is declared. So defining it here in our own assembly is enough.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}

namespace AutopilotMonitor.Agent.V2.Core.Tests.Harness
{
    /// <summary>
    /// Assembly-wide test bootstrap. Codex PR-1-pass-1 Hoch fix side-effect — the orchestrator
    /// now posts <c>EspConfigDetected</c> synchronously at <c>Start</c> time by reading the
    /// live <c>HKLM\SOFTWARE\Microsoft\Enrollments\{guid}\FirstSync</c> registry values. On a
    /// dev machine with real Autopilot enrollment state that registry read returns real bools
    /// (not <c>(null, null)</c>), which inflates signal-log / transition / spool item counts
    /// in any test that starts a full orchestrator and asserts on exact counts.
    /// <para>
    /// This initializer forces the probe to return <c>(null, null)</c> for every test in the
    /// assembly, so the bootstrap is deterministically a no-op. Tests that specifically want
    /// to exercise the bootstrap (e.g. <c>EnrollmentOrchestratorEspConfigBootstrapTests</c>)
    /// wrap their test bodies in a <see cref="EspSkipConfigurationProbe.ScopedOverride"/> that
    /// re-enables the real probe path (or a deterministic stand-in) for the scope of that
    /// single test and restores the assembly default on dispose.
    /// </para>
    /// </summary>
    internal static class TestAssemblyInit
    {
        [ModuleInitializer]
        internal static void DisableEspSkipProbeByDefault()
        {
            EspSkipConfigurationProbe.TestOverride = _ => (null, null);
            EspSkipConfigurationProbe.FullTestOverride = _ => EspFirstSyncSnapshot.Empty;
        }
    }
}
