using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Authorization/validation matrix for <see cref="AppHomingService.EvaluateManualFlip"/> — the
/// pure decision behind <c>POST config/{tenantId}/app-homing</c>. Contract: tenant admins may
/// only flip TO primary, without force, with the self-service flag on, and only after a
/// successful consent probe; Global Admins may flip both directions and force past the probe.
/// </summary>
public class AppHomingDecisionTests
{
    private static readonly AppHomingProbeResult ProbeOk = new(Succeeded: true, IsTransient: false);
    private static readonly AppHomingProbeResult ProbeFail = new(Succeeded: false, IsTransient: false);
    private static readonly AppHomingProbeResult ProbeTransient = new(Succeeded: false, IsTransient: true);

    private static AppHomingDecision Evaluate(
        bool isGlobalAdmin = false,
        bool selfServiceEnabled = true,
        bool legacyConfigured = true,
        bool currentlyPrimary = false,
        bool targetPrimary = true,
        bool force = false,
        AppHomingProbeResult? probe = null)
        => AppHomingService.EvaluateManualFlip(
            isGlobalAdmin, selfServiceEnabled, legacyConfigured, currentlyPrimary, targetPrimary, force, probe);

    [Fact]
    public void ParallelWindowInactive_denies_for_everyone()
    {
        var decision = Evaluate(isGlobalAdmin: true, legacyConfigured: false, force: true);
        Assert.Equal(AppHomingDecisionKind.Deny, decision.Kind);
        Assert.Equal("parallel-window-inactive", decision.ReasonCode);
        Assert.Equal(409, decision.StatusCode);
    }

    [Theory]
    [InlineData(false, false)] // legacy-homed, target legacy
    [InlineData(true, true)]   // primary-homed, target primary
    public void AlreadyAtTarget_is_a_noop(bool currentlyPrimary, bool targetPrimary)
    {
        var decision = Evaluate(currentlyPrimary: currentlyPrimary, targetPrimary: targetPrimary);
        Assert.Equal(AppHomingDecisionKind.AllowNoOp, decision.Kind);
    }

    [Fact]
    public void NonGa_revert_to_legacy_is_denied()
    {
        var decision = Evaluate(currentlyPrimary: true, targetPrimary: false);
        Assert.Equal(AppHomingDecisionKind.Deny, decision.Kind);
        Assert.Equal("revert-is-ga-only", decision.ReasonCode);
        Assert.Equal(403, decision.StatusCode);
    }

    [Fact]
    public void NonGa_force_is_denied()
    {
        var decision = Evaluate(force: true);
        Assert.Equal(AppHomingDecisionKind.Deny, decision.Kind);
        Assert.Equal("force-is-ga-only", decision.ReasonCode);
        Assert.Equal(403, decision.StatusCode);
    }

    [Fact]
    public void NonGa_with_flag_off_is_denied()
    {
        // The kill switch: turning the admin-config flag off must stop tenant-admin flips
        // immediately, while the GA path below stays open (operator lever survives).
        var decision = Evaluate(selfServiceEnabled: false);
        Assert.Equal(AppHomingDecisionKind.Deny, decision.Kind);
        Assert.Equal("self-service-disabled", decision.ReasonCode);
        Assert.Equal(409, decision.StatusCode);
    }

    [Fact]
    public void Ga_with_flag_off_still_requires_only_the_probe()
    {
        var decision = Evaluate(isGlobalAdmin: true, selfServiceEnabled: false);
        Assert.Equal(AppHomingDecisionKind.RequireProbe, decision.Kind);
    }

    [Fact]
    public void FlipToPrimary_without_probe_requires_probe()
    {
        var decision = Evaluate();
        Assert.Equal(AppHomingDecisionKind.RequireProbe, decision.Kind);
    }

    [Fact]
    public void FlipToPrimary_with_successful_probe_is_allowed()
    {
        var decision = Evaluate(probe: ProbeOk);
        Assert.Equal(AppHomingDecisionKind.Allow, decision.Kind);
    }

    [Fact]
    public void FlipToPrimary_with_failed_probe_is_denied()
    {
        var decision = Evaluate(probe: ProbeFail);
        Assert.Equal(AppHomingDecisionKind.Deny, decision.Kind);
        Assert.Equal("probe-failed", decision.ReasonCode);
        Assert.Equal(409, decision.StatusCode);
    }

    [Fact]
    public void FlipToPrimary_with_transient_probe_is_denied_as_retryable()
    {
        var decision = Evaluate(probe: ProbeTransient);
        Assert.Equal(AppHomingDecisionKind.Deny, decision.Kind);
        Assert.Equal("probe-transient", decision.ReasonCode);
        Assert.Equal(503, decision.StatusCode);
    }

    [Fact]
    public void Ga_flip_without_force_also_requires_probe()
    {
        var decision = Evaluate(isGlobalAdmin: true);
        Assert.Equal(AppHomingDecisionKind.RequireProbe, decision.Kind);
    }

    [Fact]
    public void Ga_force_skips_the_probe()
    {
        var decision = Evaluate(isGlobalAdmin: true, force: true);
        Assert.Equal(AppHomingDecisionKind.Allow, decision.Kind);
    }

    [Fact]
    public void Ga_revert_to_legacy_needs_no_probe()
    {
        // The legacy app is consented by construction — probing it would be meaningless.
        var decision = Evaluate(isGlobalAdmin: true, currentlyPrimary: true, targetPrimary: false);
        Assert.Equal(AppHomingDecisionKind.Allow, decision.Kind);
    }
}
