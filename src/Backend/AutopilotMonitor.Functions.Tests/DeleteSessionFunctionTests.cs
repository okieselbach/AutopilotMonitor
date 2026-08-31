using System.Net;
using System.Reflection;
using AutopilotMonitor.Functions.Functions.Sessions;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Deletion;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// HTTP-layer tests for <see cref="DeleteSessionFunction"/>. Verifies the policy-catalog
/// registration, the public status-mapping / body-shape helpers, and the single pre-dispatch
/// kill-switch gate without touching <c>HttpRequestData</c>. Everything else (existence check,
/// lock-state mapping, recovery resume) is owned by the producer and covered by
/// <see cref="SessionDeletionProducer"/> tests.
/// </summary>
public class DeleteSessionFunctionTests
{
    private const string SessionId  = "22222222-2222-2222-2222-222222222222";
    private const string ManifestId = "01J0123456789ABCDEFGHIJKLM";

    // ── Policy catalog ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_route_is_registered_in_policy_catalog_as_TenantAdminOrGA()
    {
        // Memory feedback_route_policy_catalog: every HTTP route MUST be registered in
        // EndpointAccessPolicyCatalog. Unregistered routes fail-closed → 403. PR5 keeps the
        // legacy policy unchanged (V2 is a body/path-dispatch detail, not an authorization one).
        var entry = EndpointAccessPolicyCatalog.FindPolicy("DELETE", "sessions/" + SessionId);

        Assert.NotNull(entry);
        Assert.Equal(EndpointPolicy.TenantAdminOrGA, entry!.Policy);
    }

    // ── MapEnqueueOutcomeToStatus ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SessionDeletionEnqueueOutcome.Enqueued,         HttpStatusCode.Accepted)]
    [InlineData(SessionDeletionEnqueueOutcome.AlreadyInFlight,  HttpStatusCode.Conflict)]
    [InlineData(SessionDeletionEnqueueOutcome.Poisoned,         HttpStatusCode.Conflict)]
    [InlineData(SessionDeletionEnqueueOutcome.KillSwitchActive, HttpStatusCode.ServiceUnavailable)]
    [InlineData(SessionDeletionEnqueueOutcome.CasExhausted,     HttpStatusCode.ServiceUnavailable)]
    [InlineData(SessionDeletionEnqueueOutcome.SessionNotFound,  HttpStatusCode.NotFound)]
    public void MapEnqueueOutcomeToStatus_returns_expected_status(
        SessionDeletionEnqueueOutcome outcome, HttpStatusCode expected)
    {
        Assert.Equal(expected, DeleteSessionFunction.MapEnqueueOutcomeToStatus(outcome));
    }

    [Fact]
    public void MapEnqueueOutcomeToStatus_falls_back_to_500_on_unknown_outcome()
    {
        // Defensive: a new outcome value added later without updating the mapping must surface
        // as a 500 (visible bug) instead of silently masquerading as 200/202. Cast to bypass
        // the enum bounds check — represents a hypothetical future enum addition.
        var bogus = (SessionDeletionEnqueueOutcome)999;
        Assert.Equal(HttpStatusCode.InternalServerError, DeleteSessionFunction.MapEnqueueOutcomeToStatus(bogus));
    }

    // ── BuildV2ResponseBody ───────────────────────────────────────────────────────────────

    [Fact]
    public void BuildV2ResponseBody_Enqueued_carries_manifestId_and_queued_status()
    {
        var result = new SessionDeletionEnqueueResult
        {
            Outcome = SessionDeletionEnqueueOutcome.Enqueued,
            ManifestId = ManifestId,
        };

        var body = DeleteSessionFunction.BuildV2ResponseBody(result, SessionId);

        AssertProperty(body, "success", true);
        AssertProperty(body, "status", "queued");
        AssertProperty(body, "manifestId", ManifestId);
    }

    [Fact]
    public void BuildV2ResponseBody_AlreadyInFlight_carries_state_and_manifestId_with_hint()
    {
        var result = new SessionDeletionEnqueueResult
        {
            Outcome = SessionDeletionEnqueueOutcome.AlreadyInFlight,
            ManifestId = ManifestId,
            ExistingState = "Running",
        };

        var body = DeleteSessionFunction.BuildV2ResponseBody(result, SessionId);

        AssertProperty(body, "success", false);
        AssertProperty(body, "manifestId", ManifestId);
        AssertProperty(body, "deletionState", "Running");
        AssertProperty(body, "hint", "cascade_already_in_flight");
    }

    [Fact]
    public void BuildV2ResponseBody_Poisoned_hints_at_restore_endpoint()
    {
        // Plan §13: the only recovery path from Poisoned is POST /restore. The HTTP body
        // must say so explicitly so the UI/operator does not loop on the delete endpoint.
        var result = new SessionDeletionEnqueueResult
        {
            Outcome = SessionDeletionEnqueueOutcome.Poisoned,
            ManifestId = ManifestId,
            ExistingState = "Poisoned",
        };

        var body = DeleteSessionFunction.BuildV2ResponseBody(result, SessionId);

        AssertProperty(body, "hint", "cascade_poisoned_use_restore");
        AssertProperty(body, "manifestId", ManifestId);
        // The message string must mention the restore endpoint so the UI surfaces it verbatim.
        var message = GetProperty<string>(body, "message");
        Assert.Contains("/restore", message);
    }

    [Fact]
    public void BuildV2ResponseBody_KillSwitchActive_does_not_leak_manifestId()
    {
        // Race: kill-switch flipped between step-1 admin check and the producer's CAS read.
        // The producer never built a manifest, so the body must NOT carry a manifestId at all
        // (avoid the UI rendering a "track this cascade" link to a non-existent manifest).
        var result = new SessionDeletionEnqueueResult
        {
            Outcome = SessionDeletionEnqueueOutcome.KillSwitchActive,
        };

        var body = DeleteSessionFunction.BuildV2ResponseBody(result, SessionId);

        AssertProperty(body, "hint", "kill_switch_active");
        Assert.False(HasProperty(body, "manifestId"));
    }

    // ── EvaluateAdminDeleteGates (kill-switch short-circuit) ──────────────────────────────

    [Fact]
    public void Gates_killSwitch_active_short_circuits_to_503()
    {
        var gate = DeleteSessionFunction.EvaluateAdminDeleteGates(killSwitchActive: true);

        Assert.NotNull(gate);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, gate!.Value.Status);
        AssertProperty(gate.Value.Body, "hint", "kill_switch_active");
        // The kill-switch body must NOT carry a manifestId so the UI doesn't render a
        // "track this cascade" link for a request that was refused.
        Assert.False(HasProperty(gate.Value.Body, "manifestId"));
    }

    [Fact]
    public void Gates_killSwitch_inactive_returns_null_so_producer_is_invoked()
    {
        // The happy path: producer handles existence (404), lock-state (409), and recovery
        // resume (202 for Queued/Preparing+Snapshot). Function must NOT short-circuit those.
        var gate = DeleteSessionFunction.EvaluateAdminDeleteGates(killSwitchActive: false);

        Assert.Null(gate);
    }

    // ── Anonymous-object reflection helpers ───────────────────────────────────────────────

    // IgnoreCase: the Enqueued arm is a typed DTO (PascalCase properties, camelCased on the
    // wire by ApiJsonOptions); the error arms stay anonymous (camelCase). Both resolve here.
    private static void AssertProperty(object body, string propertyName, object? expected)
    {
        var prop = body.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        Assert.NotNull(prop);
        Assert.Equal(expected, prop!.GetValue(body));
    }

    private static T GetProperty<T>(object body, string propertyName)
    {
        var prop = body.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        Assert.NotNull(prop);
        var value = prop!.GetValue(body);
        return (T)value!;
    }

    private static bool HasProperty(object body, string propertyName) =>
        body.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance) != null;
}
