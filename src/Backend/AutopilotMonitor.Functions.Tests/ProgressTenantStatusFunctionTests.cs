using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Progress;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The Progress Portal's tenant status: when an organization reads as unused (no session, signed up
/// longer than <see cref="ProgressTenantStatusFunction.UnusedAfter"/> ago), and that a failed read never
/// does.
/// </summary>
public class ProgressTenantStatusFunctionTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    private static TenantConfiguration Config(DateTime? onboardedAt)
        => new() { TenantId = TenantId, OnboardedAt = onboardedAt };

    // ── Policy catalog ───────────────────────────────────────────────────────

    [Fact]
    public void Route_is_AuthenticatedUser_scoped_to_the_JWT_tenant()
    {
        // Members without a role are the audience, so the tier must not require membership; the route
        // takes no tenant parameter, so no caller can ask about another organization.
        var entry = EndpointAccessPolicyCatalog.FindPolicy("GET", "progress/tenant-status");

        Assert.NotNull(entry);
        Assert.Equal(EndpointPolicy.AuthenticatedUser, entry!.Policy);
        Assert.Equal(TenantScoping.Jwt, entry.TenantScoping);
    }

    // ── Evaluate ─────────────────────────────────────────────────────────────

    [Fact]
    public void No_session_and_signed_up_a_month_ago_is_unused()
    {
        var signedUp = Now.AddDays(-30);

        var status = ProgressTenantStatusFunction.Evaluate(Config(signedUp), hasAnySession: false, Now);

        Assert.True(status.Success);
        Assert.True(status.Unused);
        Assert.Equal(signedUp, status.SignedUpAt);
    }

    [Fact]
    public void Any_session_means_in_use()
    {
        var status = ProgressTenantStatusFunction.Evaluate(Config(Now.AddDays(-300)), hasAnySession: true, Now);

        Assert.False(status.Unused);
    }

    [Fact]
    public void An_organization_still_being_set_up_is_not_unused()
    {
        var status = ProgressTenantStatusFunction.Evaluate(Config(Now.AddDays(-13)), hasAnySession: false, Now);

        Assert.False(status.Unused);
    }

    [Fact]
    public void The_window_closes_after_exactly_fourteen_days()
    {
        var status = ProgressTenantStatusFunction.Evaluate(
            Config(Now - ProgressTenantStatusFunction.UnusedAfter), hasAnySession: false, Now);

        Assert.True(status.Unused);
    }

    [Fact]
    public void A_row_without_a_signup_date_is_old_and_reports_no_date()
    {
        var status = ProgressTenantStatusFunction.Evaluate(Config(onboardedAt: null), hasAnySession: false, Now);

        Assert.True(status.Unused);
        Assert.Null(status.SignedUpAt);
    }

    [Fact]
    public void Without_a_configuration_row_nothing_is_unused()
    {
        var status = ProgressTenantStatusFunction.Evaluate(config: null, hasAnySession: false, Now);

        Assert.True(status.Success);
        Assert.False(status.Unused);
        Assert.Null(status.SignedUpAt);
    }

    // ── HTTP ─────────────────────────────────────────────────────────────────

    private static (ProgressTenantStatusFunction Sut, Mock<ISessionRepository> Sessions) Build(TenantConfiguration? row)
    {
        var configService = new Mock<TenantConfigurationService>(
            Mock.Of<IConfigRepository>(), Mock.Of<ILogger<TenantConfigurationService>>(), Mock.Of<IMemoryCache>())
        { CallBase = false };
        configService.Setup(s => s.GetConfigurationIfExistsAsync(TenantId)).ReturnsAsync(row);

        var sessions = new Mock<ISessionRepository>(MockBehavior.Strict);
        var sut = new ProgressTenantStatusFunction(
            Mock.Of<ILogger<ProgressTenantStatusFunction>>(), configService.Object, sessions.Object);
        return (sut, sessions);
    }

    [Fact]
    public async Task Answers_for_the_callers_own_tenant()
    {
        var (sut, sessions) = Build(Config(DateTime.UtcNow.AddDays(-60)));
        sessions.Setup(s => s.HasAnySessionStrictAsync(TenantId)).ReturnsAsync(false);
        var (req, _) = EndpointHarness.Request(TenantId);

        var response = await sut.GetTenantStatus(req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        response.Body.Position = 0;
        using var doc = JsonDocument.Parse(response.Body);
        Assert.True(doc.RootElement.GetProperty("unused").GetBoolean());
    }

    [Fact]
    public async Task A_failed_session_read_is_an_error_never_unused()
    {
        var (sut, sessions) = Build(Config(DateTime.UtcNow.AddDays(-60)));
        sessions.Setup(s => s.HasAnySessionStrictAsync(TenantId))
            .ThrowsAsync(new InvalidOperationException("storage unavailable"));
        var (req, _) = EndpointHarness.Request(TenantId);

        var response = await sut.GetTenantStatus(req);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
