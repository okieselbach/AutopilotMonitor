using System.Linq;
using System.Reflection;
using AutopilotMonitor.Functions.Functions.Bootstrap;
using AutopilotMonitor.Functions.Functions.Sessions;
using AutopilotMonitor.Functions.Services.Deletion;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Structural wiring tests for the cascade-delete guard on the register-session surface
/// (Codex-followup F2/F3 + bootstrap follow-up). The guard lives in the shared core
/// <see cref="RegisterSessionFunction.ProcessRegisterAsync"/> so BOTH HTTP entry points hit
/// it before <c>StoreSessionAsync</c>:
/// <list type="number">
///   <item><see cref="RegisterSessionFunction"/> — cert-auth <c>/agent/register-session</c></item>
///   <item><see cref="BootstrapRegisterSessionFunction"/> — bootstrap-code-authed wrapper</item>
/// </list>
/// <para>
/// These tests pin the structural contract (DI field present, bootstrap delegates to the shared
/// core, the shared core is reachable from the wrapper's assembly via <c>internal</c> accessibility).
/// The Guard's own behaviour is covered by <see cref="SessionDeletionGuardTests"/>.
/// </para>
/// </summary>
public class RegisterSessionGuardWiringTests
{
    [Fact]
    public void RegisterSessionFunction_has_SessionDeletionGuard_dependency_injected()
    {
        // Guard MUST be in the constructor — DI container fail-fasts on missing service, so a
        // refactor that drops the dependency would surface here as a compile error or here as
        // a missing-field assertion before reaching production.
        var ctor = typeof(RegisterSessionFunction).GetConstructors().Single();
        Assert.Contains(ctor.GetParameters(), p => p.ParameterType == typeof(SessionDeletionGuard));

        var field = typeof(RegisterSessionFunction)
            .GetField("_deletionGuard", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Equal(typeof(SessionDeletionGuard), field!.FieldType);
    }

    [Fact]
    public void ProcessRegisterAsync_is_internal_so_bootstrap_wrapper_can_reach_it()
    {
        // The bootstrap wrapper calls _inner.ProcessRegisterAsync directly. Tightening the
        // accessibility (private) would silently break that delegation; the build would fail
        // but only on a refactor — pin the contract here.
        var method = typeof(RegisterSessionFunction)
            .GetMethod("ProcessRegisterAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.True(method!.IsAssembly, "ProcessRegisterAsync must be `internal` so the bootstrap wrapper can invoke the shared core (and the guard inside it).");
    }

    [Fact]
    public void BootstrapRegisterSessionFunction_delegates_to_RegisterSessionFunction()
    {
        // The bootstrap path MUST flow through the shared core (where the guard lives) — a
        // refactor that copy-pastes the register logic into Bootstrap directly would re-open
        // the original Codex finding ("bootstrap-registration umgeht den Delete-Guard").
        var innerField = typeof(BootstrapRegisterSessionFunction)
            .GetField("_inner", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(innerField);
        Assert.Equal(typeof(RegisterSessionFunction), innerField!.FieldType);
    }
}
