using System.Net;
using AutopilotMonitor.Functions.Functions.Sessions;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
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
    // The success arm is SessionDeletionQueuedResponse; every rejected arm is the typed error body
    // SessionDeletionRejectedResponse (envelope prefix error/code/correlationId + lock diagnostics).
    // The correlation id is stamped by the writer, so the builder leaves it empty here.

    [Fact]
    public void BuildV2ResponseBody_Enqueued_carries_manifestId_and_queued_status()
    {
        var result = new SessionDeletionEnqueueResult
        {
            Outcome = SessionDeletionEnqueueOutcome.Enqueued,
            ManifestId = ManifestId,
        };

        var body = Assert.IsType<SessionDeletionQueuedResponse>(DeleteSessionFunction.BuildV2ResponseBody(result, SessionId));

        Assert.True(body.Success);
        Assert.Equal("queued", body.Status);
        Assert.Equal(ManifestId, body.ManifestId);
    }

    [Fact]
    public void BuildV2ResponseBody_AlreadyInFlight_carries_state_and_manifestId_with_code()
    {
        var result = new SessionDeletionEnqueueResult
        {
            Outcome = SessionDeletionEnqueueOutcome.AlreadyInFlight,
            ManifestId = ManifestId,
            ExistingState = "Running",
        };

        var body = Rejected(DeleteSessionFunction.BuildV2ResponseBody(result, SessionId));

        Assert.Equal(Constants.ApiErrorCodes.CascadeAlreadyInFlight, body.Code);
        Assert.Equal(ManifestId, body.ManifestId);
        Assert.Equal("Running", body.DeletionState);
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

        var body = Rejected(DeleteSessionFunction.BuildV2ResponseBody(result, SessionId));

        Assert.Equal(Constants.ApiErrorCodes.CascadePoisonedUseRestore, body.Code);
        Assert.Equal(ManifestId, body.ManifestId);
        // The message string must mention the restore endpoint so the UI surfaces it verbatim.
        Assert.Contains("/restore", body.Error);
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

        var body = Rejected(DeleteSessionFunction.BuildV2ResponseBody(result, SessionId));

        Assert.Equal(Constants.ApiErrorCodes.KillSwitchActive, body.Code);
        Assert.Null(body.ManifestId);
        Assert.DoesNotContain("manifestId", TestWire.Serialize(body));
    }

    [Fact]
    public void BuildV2ResponseBody_rejected_arms_serialize_with_the_envelope_prefix()
    {
        var result = new SessionDeletionEnqueueResult
        {
            Outcome = SessionDeletionEnqueueOutcome.SessionNotFound,
        };

        var body = Rejected(DeleteSessionFunction.BuildV2ResponseBody(result, SessionId));
        body.CorrelationId = "cid-1";

        Assert.Equal(
            $"{{\"error\":\"Session {SessionId} not found\",\"code\":\"NotFound\",\"correlationId\":\"cid-1\"}}",
            TestWire.Serialize(body));
    }

    // ── EvaluateAdminDeleteGates (kill-switch short-circuit) ──────────────────────────────

    [Fact]
    public void Gates_killSwitch_active_short_circuits_to_503()
    {
        var gate = DeleteSessionFunction.EvaluateAdminDeleteGates(killSwitchActive: true);

        Assert.NotNull(gate);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, gate!.Value.Status);
        Assert.Equal(Constants.ApiErrorCodes.KillSwitchActive, gate.Value.Body.Code);
        // The kill-switch body must NOT carry a manifestId so the UI doesn't render a
        // "track this cascade" link for a request that was refused.
        Assert.Null(gate.Value.Body.ManifestId);
    }

    [Fact]
    public void Gates_killSwitch_inactive_returns_null_so_producer_is_invoked()
    {
        // The happy path: producer handles existence (404), lock-state (409), and recovery
        // resume (202 for Queued/Preparing+Snapshot). Function must NOT short-circuit those.
        var gate = DeleteSessionFunction.EvaluateAdminDeleteGates(killSwitchActive: false);

        Assert.Null(gate);
    }

    private static SessionDeletionRejectedResponse Rejected(IApiResponse body)
        => Assert.IsType<SessionDeletionRejectedResponse>(body);
}
