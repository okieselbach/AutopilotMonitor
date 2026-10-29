using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Feedback;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The rating bubble's answer route, driven through <c>ProcessSubmitAsync</c> (the HTTP shell only
/// reads the body and maps the validation error). Pins that a recorded rating, and nothing else,
/// raises <c>FeedbackRatingReceived</c>, the Global Admin bell and the Telegram ping, and that the
/// alert carries neither the comment nor the sender.
/// </summary>
public sealed class FeedbackFunctionSubmitTests
{
    private const string TenantId = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";
    private const string NormalizedTenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Upn = "alice@contoso.invalid";
    private const string DisplayName = "Alice (Contoso)";
    private const string Comment = "The timeline filter could remember my last choice.";
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private const string FeedbackId = "0123456789ab";
    private static readonly string EntryPath = Constants.PortalUserFeedbackEntryPath(FeedbackId);

    [Fact]
    public async Task Rating_IsStored_RaisesTheEvent_RingsTheBell_AndPings()
    {
        var h = new Harness();

        var error = await h.Submit(4, "  " + Comment + "\n");

        Assert.Null(error);
        var row = Assert.Single(h.Repo.Written);
        Assert.True(row.Submitted);
        Assert.Equal(4, row.Rating);
        Assert.Equal(Comment, row.Comment);
        Assert.Equal(Now, row.InteractedAt);

        var ops = Assert.Single(h.OpsEvents);
        Assert.Equal(OpsEventTypes.FeedbackRatingReceived, ops.EventType);
        Assert.Equal(OpsEventCategory.Tenant, ops.Category);
        Assert.Equal(OpsEventSeverity.Info, ops.Severity);
        Assert.Equal(NormalizedTenantId, ops.TenantId);
        Assert.Equal(Upn, ops.UserId);
        Assert.Equal($"Feedback rating 4/5 received from contoso.invalid ({NormalizedTenantId})", ops.Message);

        using var details = JsonDocument.Parse(ops.Details!);
        Assert.Equal(4, details.RootElement.GetProperty("rating").GetInt32());
        Assert.Equal(Comment.Length, details.RootElement.GetProperty("commentLength").GetInt32());
        Assert.Equal("contoso.invalid", details.RootElement.GetProperty("domainName").GetString());
        Assert.Equal(FeedbackId, details.RootElement.GetProperty("feedbackId").GetString());
        Assert.Equal("/admin/reports/user-feedback?feedbackId=0123456789ab", details.RootElement.GetProperty("portalPath").GetString());

        h.Bell.Verify(b => b.CreateNotificationAsync(
            "feedback", "New Rating", $"{Upn} (contoso.invalid) · 4/5", EntryPath), Times.Once);
        h.Telegram.Verify(t => t.SendFeedbackAsync(TenantId, Upn, DisplayName, 4, Comment), Times.Once);
    }

    [Fact]
    public async Task TheAlert_CarriesNeitherCommentNorSender()
    {
        // Channels bound to FeedbackRatingReceived (Telegram, Slack, webhooks, e-mail) receive message
        // and details; the comment is read in the portal. Personal data must never ride along.
        var h = new Harness();

        await h.Submit(2, Comment);

        var ops = Assert.Single(h.OpsEvents);
        foreach (var leaked in new[] { Comment, Upn, DisplayName })
        {
            Assert.DoesNotContain(leaked, ops.Message);
            Assert.DoesNotContain(leaked, ops.Details ?? string.Empty);
        }
    }

    [Fact]
    public async Task Dismissal_IsCounted_ButAnnouncesNothing()
    {
        var h = new Harness();

        var error = await h.Submit(null, null, dismissed: true);

        Assert.Null(error);
        var row = Assert.Single(h.Repo.Written);
        Assert.False(row.Submitted);
        Assert.Equal(1, row.DismissCount);
        Assert.Empty(h.OpsEvents);
        h.AssertNothingAnnounced();
    }

    [Fact]
    public async Task AnswerOnARatedRow_ChangesNothing_AndAnnouncesNothing()
    {
        // A second tab answering after the rating: the row is final, so nothing is written or pinged twice.
        var h = new Harness(current: new FeedbackEntry
        {
            Upn = Upn, TenantId = TenantId, DisplayName = DisplayName,
            Rating = 5, Submitted = true, InteractedAt = Now.AddMinutes(-5),
        });

        var error = await h.Submit(1, "second tab");

        Assert.Null(error);
        Assert.Empty(h.Repo.Written);
        Assert.Empty(h.OpsEvents);
        h.AssertNothingAnnounced();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(null)]
    public async Task InvalidRating_IsRejected_BeforeAnythingIsWritten(int? rating)
    {
        var h = new Harness();

        var error = await h.Submit(rating, Comment);

        Assert.Equal("Rating must be between 1 and 5", error);
        Assert.False(h.Repo.UpdateCalled);
        Assert.Empty(h.OpsEvents);
        h.AssertNothingAnnounced();
    }

    [Fact]
    public async Task UnknownTenant_KeepsTheGuidLabel_AndAnEmptyCommentCountsZero()
    {
        var h = new Harness(configExists: false);

        await h.Submit(5, "   ");

        var ops = Assert.Single(h.OpsEvents);
        Assert.Equal($"Feedback rating 5/5 received from {NormalizedTenantId}", ops.Message);
        using var details = JsonDocument.Parse(ops.Details!);
        Assert.Equal(0, details.RootElement.GetProperty("commentLength").GetInt32());
        h.Bell.Verify(b => b.CreateNotificationAsync(
            "feedback", "New Rating", $"{Upn} ({NormalizedTenantId}) · 5/5", EntryPath), Times.Once);
    }

    [Fact]
    public async Task OpsEventStorageOutage_DoesNotFailTheRating()
    {
        var h = new Harness(opsEventThrows: true);

        var error = await h.Submit(3, Comment);

        Assert.Null(error);
        Assert.Single(h.Repo.Written);
        Assert.Empty(h.OpsEvents);
        h.Bell.Verify(b => b.CreateNotificationAsync(
            "feedback", "New Rating", It.IsAny<string>(), EntryPath), Times.Once);
        h.Telegram.Verify(t => t.SendFeedbackAsync(TenantId, Upn, DisplayName, 3, Comment), Times.Once);
    }

    [Fact]
    public async Task OverlongComment_IsCutToTheLimit_AndTheAlertCountsTheStoredLength()
    {
        var max = Constants.SubmissionLimits.FeedbackTextMaxChars;
        var h = new Harness();

        await h.Submit(4, new string('x', max + 50));

        var row = Assert.Single(h.Repo.Written);
        Assert.Equal(max, row.Comment!.Length);
        using var details = JsonDocument.Parse(Assert.Single(h.OpsEvents).Details!);
        Assert.Equal(max, details.RootElement.GetProperty("commentLength").GetInt32());
    }

    private sealed class Harness
    {
        public FeedbackFunction Sut { get; }
        public FakeFeedbackRepository Repo { get; }
        public List<OpsEventEntry> OpsEvents { get; } = new();
        public Mock<GlobalNotificationService> Bell { get; }
        public Mock<TelegramNotificationService> Telegram { get; }

        public Harness(FeedbackEntry? current = null, bool configExists = true, bool opsEventThrows = false)
        {
            Repo = new FakeFeedbackRepository(current);
            var configRepo = Mock.Of<IConfigRepository>();
            var cache = new MemoryCache(new MemoryCacheOptions());

            var tenantConfig = new Mock<TenantConfigurationService>(
                configRepo, Mock.Of<ILogger<TenantConfigurationService>>(), Mock.Of<IMemoryCache>())
            { CallBase = false };
            var config = TenantConfiguration.CreateDefault(NormalizedTenantId);
            config.DomainName = "contoso.invalid";
            tenantConfig.Setup(c => c.TryGetConfigurationAsync(NormalizedTenantId))
                .ReturnsAsync((config, configExists));

            Bell = new Mock<GlobalNotificationService>(
                Mock.Of<INotificationRepository>(), new FakeSignalRNotificationService(),
                Mock.Of<ILogger<GlobalNotificationService>>())
            { CallBase = false };
            Telegram = new Mock<TelegramNotificationService>(
                new HttpClient(), configRepo, Mock.Of<ILogger<TelegramNotificationService>>())
            { CallBase = false };

            var adminConfig = new Mock<AdminConfigurationService>(
                configRepo, NullLogger<AdminConfigurationService>.Instance, cache)
            { CallBase = false };
            var tenantAdmins = new Mock<TenantAdminsService>(
                Mock.Of<IAdminRepository>(), cache, Mock.Of<ILogger<TenantAdminsService>>())
            { CallBase = false };

            Sut = new FeedbackFunction(
                NullLogger<FeedbackFunction>.Instance, tenantConfig.Object, adminConfig.Object, tenantAdmins.Object,
                Telegram.Object, Mock.Of<ISessionRepository>(), Repo,
                BuildOpsEventService(adminConfig.Object, opsEventThrows), Bell.Object);
        }

        public Task<string?> Submit(int? rating, string? comment, bool dismissed = false)
            => Sut.ProcessSubmitAsync(TenantId, Upn, DisplayName,
                new FeedbackRequest { Rating = rating, Comment = comment, Dismissed = dismissed }, Now);

        public void AssertNothingAnnounced()
        {
            Bell.Verify(b => b.CreateNotificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
            Telegram.Verify(t => t.SendFeedbackAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
        }

        /// <summary>A real OpsEventService over a capturing repository, so the actual record method runs.</summary>
        private OpsEventService BuildOpsEventService(AdminConfigurationService adminConfig, bool opsEventThrows)
        {
            var opsRepo = new Mock<IOpsEventRepository>();
            opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
                .Callback<OpsEventEntry>(e =>
                {
                    if (opsEventThrows)
                        throw new InvalidOperationException("simulated OpsEvent storage outage");
                    OpsEvents.Add(e);
                })
                .Returns(Task.CompletedTask);

            return new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance,
                TestNotifications.InertOpsAlertDispatch(adminConfig));
        }
    }

    /// <summary>
    /// In-memory feedback repo holding one person's bubble row; only the in-app members are expected. Like the table
    /// repository it keeps the stored row's id and mints <see cref="FeedbackId"/> for a new row.
    /// </summary>
    private sealed class FakeFeedbackRepository : IFeedbackRepository
    {
        private FeedbackEntry? _current;

        public FakeFeedbackRepository(FeedbackEntry? current) => _current = current;

        public List<FeedbackEntry> Written { get; } = new();
        public bool UpdateCalled { get; private set; }

        public Task<FeedbackEntry?> UpdateInAppFeedbackAsync(string upn, Func<FeedbackEntry?, FeedbackEntry?> decide)
        {
            UpdateCalled = true;
            var next = decide(_current);
            if (next != null)
            {
                next.Type = FeedbackEntryType.InApp;
                next.FeedbackId = _current?.FeedbackId ?? FeedbackId;
                _current = next;
                Written.Add(next);
            }
            return Task.FromResult(next);
        }

        public Task<FeedbackEntry?> GetInAppFeedbackAsync(string upn) => throw new NotSupportedException();
        public Task<FeedbackEntry?> GetOffboardingFeedbackAsync(string historyRowKey) => throw new NotSupportedException();
        public Task SaveOffboardingFeedbackAsync(FeedbackEntry entry) => throw new NotSupportedException();
        public Task SaveGeneralFeedbackAsync(FeedbackEntry entry) => throw new NotSupportedException();
        public Task<int> CountGeneralFeedbackSinceAsync(string upn, DateTime sinceUtc) => throw new NotSupportedException();
        public Task<List<FeedbackEntry>> GetAllAsync() => throw new NotSupportedException();
    }
}
