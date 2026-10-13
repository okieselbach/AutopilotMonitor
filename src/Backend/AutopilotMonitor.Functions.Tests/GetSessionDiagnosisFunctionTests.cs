using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Sessions;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// GET sessions/{sessionId}/diagnosis: a malformed id is rejected before any read, an unknown
/// session is a 404, and the verdict rests on the strict event read — a storage error is an error,
/// never a verdict built from no evidence.
/// </summary>
public class GetSessionDiagnosisFunctionTests
{
    private const string TenantId = "00000000-0000-4000-8000-000000000002";
    private const string SessionId = "00000000-0000-4000-8000-000000000001";

    private static (GetSessionDiagnosisFunction Function, Mock<ISessionRepository> Repo) Build()
    {
        var repo = new Mock<ISessionRepository>();
        return (new GetSessionDiagnosisFunction(NullLogger<GetSessionDiagnosisFunction>.Instance, repo.Object), repo);
    }

    [Fact]
    public async Task A_session_id_that_is_no_guid_is_a_bad_request_without_a_read()
    {
        var (function, repo) = Build();
        var (req, _) = EndpointHarness.Request(TenantId);

        var response = await function.Run(req, "not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task An_unknown_session_is_not_found_and_its_events_are_never_read()
    {
        var (function, repo) = Build();
        repo.Setup(r => r.GetSessionAsync(TenantId, SessionId)).ReturnsAsync((SessionSummary?)null);
        var (req, _) = EndpointHarness.Request(TenantId);

        var response = await function.Run(req, SessionId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        repo.Verify(r => r.GetSessionEventsStrictAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task The_headline_comes_from_the_row_and_the_strict_event_read()
    {
        var (function, repo) = Build();
        repo.Setup(r => r.GetSessionAsync(TenantId, SessionId)).ReturnsAsync(new SessionSummary
        {
            SessionId = SessionId,
            TenantId = TenantId,
            Status = SessionStatus.Failed,
            VerdictPath = VerdictPaths.AgentFailed,
            FailureReason = "enrollment_failed: esp_terminal_failure (0x80070005)",
        });
        repo.Setup(r => r.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(new List<EnrollmentEvent>
        {
            new()
            {
                EventId = "evt-1", EventType = "enrollment_failed", Sequence = 1, Timestamp = new DateTime(2026, 10, 13, 14, 0, 0, DateTimeKind.Utc),
                Data = new Dictionary<string, object> { ["reason"] = "esp_terminal_failure", ["category"] = "DeviceSetup", ["failedSubcategory"] = "Certificates", ["errorCode"] = "0x80070005" },
            },
        });
        var (req, _) = EndpointHarness.Request(TenantId);

        var response = await function.Run(req, SessionId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        response.Body.Position = 0;
        using var doc = JsonDocument.Parse(response.Body);
        var headline = doc.RootElement.GetProperty("headline");
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(SessionId, doc.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(DiagnosisOutcomes.Failed, headline.GetProperty("outcome").GetString());
        Assert.Equal(DiagnosisCauses.EspStepFailure, headline.GetProperty("cause").GetString());
        Assert.Equal("Certificates", headline.GetProperty("failedStep").GetProperty("subcategory").GetString());
    }

    [Fact]
    public async Task A_storage_error_on_the_events_is_a_server_error()
    {
        var (function, repo) = Build();
        repo.Setup(r => r.GetSessionAsync(TenantId, SessionId)).ReturnsAsync(new SessionSummary
        {
            SessionId = SessionId, TenantId = TenantId, Status = SessionStatus.Failed, VerdictPath = VerdictPaths.AgentFailed, FailureReason = "x",
        });
        repo.Setup(r => r.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ThrowsAsync(new InvalidOperationException("storage unavailable"));
        var (req, _) = EndpointHarness.Request(TenantId);

        var response = await function.Run(req, SessionId);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
