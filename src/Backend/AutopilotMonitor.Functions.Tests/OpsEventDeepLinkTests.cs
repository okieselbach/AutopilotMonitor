using System.Net;
using System.Security.Claims;
using System.Text.Json;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Every ops alert links to exactly the event that raised it: the save assigns the id, the
/// dispatch carries it into an "Open in the portal" action on every path, and the ops-events
/// endpoint resolves it with one point read. Before this an ops push had no link at all.
/// </summary>
public class OpsEventDeepLinkTests
{
    private const string RowKey = "2516103215872799164";
    private static readonly string SecurityId = OpsEventCategory.Security + "_" + RowKey;

    // ── The id shape ──────────────────────────────────────────────────────

    [Fact]
    public void OpsEventId_round_trips_every_category()
    {
        foreach (var category in OpsEventCategory.All)
        {
            Assert.True(OpsEventId.TryParse(OpsEventId.Format(category, RowKey), out var c, out var rk));
            Assert.Equal(category, c);
            Assert.Equal(RowKey, rk);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Security")]                        // no RowKey
    [InlineData("_2516103215872799164")]            // no category
    [InlineData("Unknown_2516103215872799164")]     // not a category
    [InlineData("security_2516103215872799164")]    // PartitionKeys are case-sensitive
    [InlineData("Security_251610321587279916")]     // 18 digits
    [InlineData("Security_25161032158727991640")]   // 20 digits
    [InlineData("Security_25161032158727991x4")]    // not a digit
    [InlineData("Security_' or 'a' eq 'a")]         // never reaches a query
    public void OpsEventId_rejects_anything_that_is_not_an_id(string? id)
        => Assert.False(OpsEventId.TryParse(id, out _, out _));

    [Fact]
    public void Read_rows_carry_the_same_id_the_link_uses()
    {
        var entry = TableOpsEventRepository.MapToEntry(new TableEntity(OpsEventCategory.Security, RowKey));

        Assert.Equal(SecurityId, entry.Id);
    }

    [Fact]
    public void The_portal_link_carries_the_id_only()
        => Assert.Equal(Constants.PortalBaseUrl + "/admin/ops/?eventId=" + SecurityId, Constants.PortalOpsEventUrl(SecurityId));

    // ── GET global/ops-events?id= ─────────────────────────────────────────

    [Fact]
    public async Task Id_query_is_a_point_read_that_ignores_the_list_filters()
    {
        var repo = new Mock<IOpsEventRepository>();
        repo.Setup(r => r.GetOpsEventAsync(SecurityId))
            .ReturnsAsync(new OpsEventEntry { Id = SecurityId, Category = OpsEventCategory.Security, EventType = "DeviceBlocked" });

        var (status, body) = await RunAsync(repo, $"?id={SecurityId}&category=Tenant&pageSize=5");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body.GetProperty("count").GetInt32());
        Assert.Equal(SecurityId, body.GetProperty("events")[0].GetProperty("id").GetString());
        repo.Verify(r => r.GetOpsEventsPageAsync(It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<OpsEventQueryFilters?>()), Times.Never);
    }

    [Fact]
    public async Task Id_query_for_a_gone_event_is_an_empty_list_not_an_error()
    {
        var repo = new Mock<IOpsEventRepository>();

        var (status, body) = await RunAsync(repo, $"?id={SecurityId}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, body.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Malformed_id_is_a_400_and_never_reaches_storage()
    {
        var repo = new Mock<IOpsEventRepository>();

        var (status, _) = await RunAsync(repo, "?id=Security_x");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        repo.Verify(r => r.GetOpsEventAsync(It.IsAny<string>()), Times.Never);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> RunAsync(Mock<IOpsEventRepository> repo, string query)
    {
        const string callerTenant = "00000000-0000-0000-0000-000000000001";
        var (req, context) = EndpointHarness.Request(callerTenant, queryString: query);
        context.Items["ClaimsPrincipal"] = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tid", callerTenant) }, "test"));
        var response = await new GetOpsEventsFunction(NullLogger<GetOpsEventsFunction>.Instance, repo.Object).Run(req);
        response.Body.Position = 0;
        using var doc = JsonDocument.Parse(response.Body);
        return (response.StatusCode, doc.RootElement.Clone());
    }

    // ── The alert carries the link ────────────────────────────────────────

    [Fact]
    public async Task Both_the_plain_and_the_payload_send_link_to_the_event()
    {
        var h = new OpsNotificationChannelsTests.DispatchHarness(
            new[]
            {
                new OpsAlertRule { EventType = "DeviceBlocked", MinSeverity = OpsEventSeverity.Info, NotifyChannelIds = new List<string> { "push" } },
                new OpsAlertRule { EventType = "DeviceBlocked", MinSeverity = OpsEventSeverity.Info, NotifyChannelIds = new List<string> { "hook" }, IncludePayload = true },
            },
            new[]
            {
                new NotificationChannel { Id = "push", Name = "push", ProviderType = 20, Url = "https://push.example.invalid/hook", Enabled = true },
                new NotificationChannel { Id = "hook", Name = "hook", ProviderType = 20, Url = "https://hook.example.invalid/hook", Enabled = true },
            });

        await h.Sut.DispatchAsync(OpsEventCategory.Security, "DeviceBlocked", OpsEventSeverity.Warning,
            "Device blocked", "11111111-1111-1111-1111-111111111111", """{"serialNumber":"SYNTH-0001"}""", SecurityId);

        Assert.Equal(2, h.Sends.Count);
        Assert.All(h.Sends, s =>
        {
            var action = Assert.Single(s.Alert.Actions);
            Assert.Equal("openUrl", action.Type);
            Assert.Equal(Constants.PortalOpsEventUrl(SecurityId), action.Url);
        });
    }

    [Fact]
    public void An_alert_without_an_id_has_no_action()
        => Assert.Empty(OpsAlertDispatchService.BuildAlert(OpsEventCategory.Security, "DeviceBlocked",
            OpsEventSeverity.Warning, "m", null, null, opsEventId: null).Actions);

    [Fact]
    public async Task The_service_dispatches_the_id_the_save_assigned()
    {
        var repo = new Mock<IOpsEventRepository>();
        repo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
            .Callback<OpsEventEntry>(e => e.Id = OpsEventId.Format(e.Category, RowKey))
            .Returns(Task.CompletedTask);
        var h = new OpsNotificationChannelsTests.DispatchHarness(
            new[] { new OpsAlertRule { EventType = OpsEventTypes.TenantSignup, MinSeverity = OpsEventSeverity.Info, NotifyChannelIds = new List<string> { "push" } } },
            new[] { new NotificationChannel { Id = "push", Name = "push", ProviderType = 20, Url = "https://push.example.invalid/hook", Enabled = true } });
        var service = new OpsEventService(repo.Object, NullLogger<OpsEventService>.Instance, h.Sut);

        await service.RecordTenantSignupAsync("11111111-1111-1111-1111-111111111111", "admin@contoso.invalid");

        // The dispatch is fire-and-forget behind the save; the mocks complete synchronously,
        // the short wait only guards against a scheduler hop.
        for (var i = 0; i < 50 && h.Sends.Count == 0; i++)
            await Task.Delay(10);
        var send = Assert.Single(h.Sends);
        Assert.Equal(Constants.PortalOpsEventUrl(OpsEventCategory.Tenant + "_" + RowKey), Assert.Single(send.Alert.Actions).Url);
    }

    [Fact]
    public async Task Tenant_signup_points_its_related_page_at_the_tenant()
    {
        OpsEventEntry? saved = null;
        var repo = new Mock<IOpsEventRepository>();
        repo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
            .Callback<OpsEventEntry>(e => saved = e)
            .Returns(Task.CompletedTask);
        var h = new OpsNotificationChannelsTests.DispatchHarness(Array.Empty<OpsAlertRule>(), Array.Empty<NotificationChannel>());
        var service = new OpsEventService(repo.Object, NullLogger<OpsEventService>.Instance, h.Sut);

        await service.RecordTenantSignupAsync("11111111-1111-1111-1111-111111111111", "admin@contoso.invalid");

        using var details = JsonDocument.Parse(saved!.Details!);
        // The bare /admin/tenants redirects and drops the query; the section path keeps it.
        Assert.Equal("/admin/tenants/management?tenantId=11111111-1111-1111-1111-111111111111",
            details.RootElement.GetProperty("portalPath").GetString());
    }
}
