using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The ops events behind a customer submission are the only signal an alert channel gets. They
/// name ids and the portal path, never the submitter or a comment; the submitter stays in the
/// row's user id, which the dispatch never forwards.
/// </summary>
public class OpsEventSubmissionSignalsTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string Submitter = "someone@contoso.invalid";

    private readonly List<OpsEventEntry> _events = new();
    private readonly OpsEventService _sut;

    public OpsEventSubmissionSignalsTests()
    {
        var opsRepo = new Mock<IOpsEventRepository>();
        opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
            .Callback<OpsEventEntry>(_events.Add)
            .Returns(Task.CompletedTask);

        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()))
        { CallBase = false };

        _sut = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance,
            TestNotifications.InertOpsAlertDispatch(adminConfig.Object));
    }

    private OpsEventEntry AssertSingleSignal(string eventType, string portalPath)
    {
        var entry = Assert.Single(_events);
        Assert.Equal(eventType, entry.EventType);
        Assert.Equal(OpsEventSeverity.Info, entry.Severity);
        Assert.Equal(TenantId, entry.TenantId);
        Assert.Contains(portalPath, entry.Details!);

        Assert.Equal(Submitter, entry.UserId);
        Assert.DoesNotContain(Submitter, entry.Message);
        Assert.DoesNotContain(Submitter, entry.Details!);
        return entry;
    }

    [Fact]
    public async Task TenantSignup_names_the_tenant_page()
    {
        await _sut.RecordTenantSignupAsync(TenantId, Submitter);

        AssertSingleSignal(OpsEventTypes.TenantSignup, $"/admin/tenants/management?tenantId={TenantId}");
    }

    [Fact]
    public async Task SessionReport_names_the_session_and_the_report_only()
    {
        await _sut.RecordSessionReportSubmittedAsync(TenantId, Submitter, "session-1", "report-1");

        var entry = AssertSingleSignal(OpsEventTypes.SessionReportSubmitted, "/admin/reports/session-reports?reportId=report-1");
        Assert.Contains("session-1", entry.Details!);
    }

    [Fact]
    public async Task DiagFilesReport_names_the_report_only()
    {
        await _sut.RecordDiagFilesReportSubmittedAsync(TenantId, Submitter, "report-2");

        AssertSingleSignal(OpsEventTypes.DiagFilesReportSubmitted, "/admin/reports/session-reports?reportId=report-2");
    }

    [Fact]
    public async Task RuleSubmission_names_the_submission_and_the_rule_only()
    {
        await _sut.RecordRuleSubmissionReceivedAsync(TenantId, Submitter, "abc123def456", "analyze", "ANALYZE-X-1");

        var entry = AssertSingleSignal(OpsEventTypes.RuleSubmissionReceived, "/admin/reports/rule-submissions?submissionId=abc123def456");
        Assert.Contains("ANALYZE-X-1", entry.Details!);
    }
}
