using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Apps;
using AutopilotMonitor.Functions.Services.Apps;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The diagnosis page's fleet line: one app's outcomes in the tenant's other enrollments of the last
/// week, classified like the apps analytics view, without the asking session and without another app
/// of the same name.
/// </summary>
public class AppFleetContextTests
{
    private const string TenantId = "00000000-0000-4000-8000-0000000000b1";
    private const string NotesId = "00000000-0000-4000-8000-0000000000a1";
    private const string OtherAppId = "00000000-0000-4000-8000-0000000000a9";
    private const string AskingSession = "00000000-0000-4000-8000-00000000000a";

    private static AppInstallSummary Row(
        string session, string status, string terminal = "", string code = "", string appId = NotesId,
        bool collision = false, double hoursAgo = 2, int? exitCode = null, string message = "") => new()
    {
        TenantId = TenantId,
        SessionId = session,
        AppName = "Contoso Notes",
        AppId = appId,
        Status = status,
        TerminalState = terminal,
        FailureCode = code,
        FailureMessage = message,
        ExitCode = exitCode,
        AppIdCollision = collision,
        StartedAt = DateTime.UtcNow.AddHours(-hoursAgo),
    };

    private static GetAppFleetContextResponse Build(IEnumerable<AppInstallSummary> rows, string? appId = NotesId, string? exclude = AskingSession)
        => AppsAnalyticsHelper.BuildFleetContextResponse(rows, "Contoso Notes", AppInstallSources.Ime, appId, exclude, DateTime.UtcNow.AddDays(-7));

    [Fact]
    public void Counts_the_other_enrollments_like_the_analytics_view()
    {
        var context = Build(new[]
        {
            Row("s1", "Succeeded", "Installed"),
            Row("s2", "Succeeded"), // legacy row without a terminal state: an install
            Row("s3", "Succeeded", "Skipped"),
            Row("s4", "Succeeded", "Postponed"),
            Row("s5", "Failed", "Error", "0x80d03805"),
            Row("s6", "Failed", "Error", "0x80d03805"),
            Row("s7", "Failed", "Error", "esp_apps_timeout"),
            Row("s8", "Incomplete"),
            Row("s9", "InProgress"),
        });

        Assert.Equal(
            (2, 2, 3, 1, 1, 0),
            (context.Installed, context.Skipped, context.Failed, context.Incomplete, context.InProgress, context.CollisionExcluded));
        Assert.Equal(new[] { ("0x80d03805", 2), ("esp_apps_timeout", 1) }, context.TopFailureCodes.Select(c => (c.Code, c.Count)));
        Assert.Equal((7, "Contoso Notes", "ime"), (context.WindowDays, context.AppName, context.Source));
    }

    [Fact]
    public void Leaves_out_the_asking_session_another_app_and_older_rows_and_counts_collisions()
    {
        var context = Build(new[]
        {
            Row(AskingSession.ToUpperInvariant(), "Failed", code: "0x80d03805"),
            Row("s1", "Failed", code: "0x80d03805", appId: OtherAppId),
            Row("s2", "Succeeded", "Installed", appId: ""), // no app id recorded: same name, kept
            Row("s3", "Succeeded", "Installed", appId: NotesId.ToUpperInvariant()),
            Row("s4", "Failed", code: "0x87d1041c", collision: true),
            Row("s5", "Failed", code: "0x87d1041c", hoursAgo: 24 * 8),
        });

        Assert.Equal((2, 0, 1), (context.Installed, context.Failed, context.CollisionExcluded));
        Assert.Empty(context.TopFailureCodes);
    }

    [Fact]
    public void Without_an_app_id_every_row_of_the_name_counts()
    {
        var context = Build(new[] { Row("s1", "Failed", code: "0x1", appId: OtherAppId), Row("s2", "Succeeded", "Installed") }, appId: null, exclude: null);

        Assert.Equal((1, 1), (context.Installed, context.Failed));
    }

    [Fact]
    public void Keeps_the_three_most_frequent_codes_with_their_exit_code_and_message()
    {
        var context = Build(new[]
        {
            Row("s1", "Failed", code: "0x1"),
            Row("s2", "Failed", code: "0x2", exitCode: 1603, message: "Fatal error during installation"),
            Row("s3", "Failed", code: "0x2"),
            Row("s4", "Failed", code: "0x3"),
            Row("s5", "Failed", code: "0x3"),
            Row("s6", "Failed", code: "0x3"),
            Row("s7", "Failed", code: "0x4"),
        });

        Assert.Equal(new[] { "0x3", "0x2", "0x1" }, context.TopFailureCodes.Select(c => c.Code));
        var second = context.TopFailureCodes[1];
        Assert.Equal((1603, "Fatal error during installation"), (second.ExitCode, second.SampleMessage));
    }

    [Fact]
    public async Task Reads_the_rows_once_per_app_and_minute_for_every_caller()
    {
        var repo = new Mock<IMetricsRepository>();
        repo.Setup(r => r.GetAppsDashboardSummariesAsync(It.IsAny<DateTime>(), TenantId))
            .ReturnsAsync(() => new List<AppInstallSummary>
            {
                Row(AskingSession, "Failed", code: "0x80d03805"),
                Row("s1", "Failed", code: "0x80d03805"),
                Row("s2", "Succeeded", "Installed"),
            });
        var service = new AppFleetContextService(repo.Object);

        var asking = await service.GetAsync(TenantId, "Contoso Notes", AppInstallSources.Ime, NotesId, AskingSession);
        var another = await service.GetAsync(TenantId, "contoso notes", AppInstallSources.Ime, NotesId, "s1");

        Assert.Equal((1, 1), (asking.Failed, asking.Installed));
        Assert.Equal((1, 1), (another.Failed, another.Installed));
        repo.Verify(r => r.GetAppsDashboardSummariesAsync(It.IsAny<DateTime>(), TenantId), Times.Once);
        repo.Verify(r => r.GetAppsDashboardSummariesAsync(
            It.Is<DateTime>(d => d <= DateTime.UtcNow.AddDays(-7) && d > DateTime.UtcNow.AddDays(-7).AddMinutes(-2)), TenantId), Times.Once);

        await service.GetAsync(TenantId, "Contoso Agent", AppInstallSources.Ime, null, AskingSession);
        repo.Verify(r => r.GetAppsDashboardSummariesAsync(It.IsAny<DateTime>(), TenantId), Times.Exactly(2));
    }

    [Theory]
    [InlineData("?source=ime")]
    [InlineData("?appName=%20")]
    [InlineData("?appName=Contoso+Notes&source=bogus")]
    [InlineData("?appName=Contoso+Notes&excludeSessionId=not-a-guid")]
    public async Task A_request_without_a_name_or_with_a_bad_parameter_is_a_bad_request(string query)
    {
        var repo = new Mock<IMetricsRepository>();
        var function = Function(repo.Object);

        var res = await function.Run(EndpointHarness.Request(TenantId, queryString: query).Req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_target_tenant_that_is_no_guid_is_a_bad_request()
    {
        var repo = new Mock<IMetricsRepository>();

        var res = await Function(repo.Object).Run(EndpointHarness.Request("not-a-guid", queryString: "?appName=Contoso+Notes").Req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Answers_for_the_target_tenant_of_the_request()
    {
        var repo = new Mock<IMetricsRepository>();
        repo.Setup(r => r.GetAppsDashboardSummariesAsync(It.IsAny<DateTime>(), TenantId))
            .ReturnsAsync(new List<AppInstallSummary> { Row("s1", "Failed", code: "0x80d03805"), Row(AskingSession, "Failed", code: "0x80d03805") });

        var res = await Function(repo.Object).Run(EndpointHarness.Request(
            TenantId, queryString: $"?appName=Contoso+Notes&appId={NotesId}&excludeSessionId={AskingSession}&tenantId={TenantId}").Req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        res.Body.Position = 0;
        using var doc = JsonDocument.Parse(res.Body);
        Assert.Equal(1, doc.RootElement.GetProperty("failed").GetInt32());
        Assert.Equal("0x80d03805", doc.RootElement.GetProperty("topFailureCodes")[0].GetProperty("code").GetString());
    }

    private static GetAppFleetContextFunction Function(IMetricsRepository repo)
        => new(NullLogger<GetAppFleetContextFunction>.Instance, new AppFleetContextService(repo));
}
