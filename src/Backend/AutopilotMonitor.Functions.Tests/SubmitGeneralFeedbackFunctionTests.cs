using System;
using System.Collections.Generic;
using System.Net.Http;
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
/// The help-menu feedback route, driven through <c>ProcessSubmitAsync</c> (the HTTP shell only
/// reads the body and maps outcomes). Pins the validation, the per-user daily cap, the stored row,
/// and that the alert carries neither the text nor the sender.
/// </summary>
public sealed class SubmitGeneralFeedbackFunctionTests
{
    private const string TenantId = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";
    private const string NormalizedTenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Upn = "alice@contoso.invalid";
    private const string DisplayName = "Alice (Contoso)";
    private const string Text = "The timeline filter could remember my last choice.";
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HappyPath_StoresTheRow_RaisesTheEvent_AndRingsTheBell()
    {
        var h = new Harness();

        var result = await h.Submit("   " + Text + "\n", " alice.support@contoso.invalid ");

        Assert.Equal(GeneralFeedbackOutcome.Ok, result.Outcome);
        var row = Assert.Single(h.Repo.Saved);
        Assert.Equal(NormalizedTenantId, row.TenantId);
        Assert.Equal(Upn, row.Upn);
        Assert.Equal(DisplayName, row.DisplayName);
        Assert.Equal("contoso.invalid", row.DomainName);
        Assert.Equal(Text, row.Comment);
        Assert.Equal("alice.support@contoso.invalid", row.ContactEmail);
        Assert.Equal(Now, row.InteractedAt);
        Assert.Equal(Now.AddHours(-24), h.Repo.CountedSince);

        var ops = Assert.Single(h.OpsEvents);
        Assert.Equal(OpsEventTypes.FeedbackReceived, ops.EventType);
        Assert.Equal(OpsEventCategory.Tenant, ops.Category);
        Assert.Equal(OpsEventSeverity.Info, ops.Severity);
        Assert.Equal(NormalizedTenantId, ops.TenantId);
        Assert.Equal(Upn, ops.UserId);
        Assert.Contains("contoso.invalid", ops.Message);
        Assert.Contains(row.FeedbackId!, ops.Details);

        h.Bell.Verify(b => b.CreateNotificationAsync(
            "feedback", "New Feedback", It.IsAny<string>(), "/admin/reports/user-feedback"), Times.Once);
    }

    [Fact]
    public async Task TheAlert_CarriesNeitherTextNorSenderNorContactAddress()
    {
        // Channels bound to FeedbackReceived (Telegram, Slack, webhooks) receive message + details;
        // the feedback itself is read in the portal. Personal data must never ride along.
        var h = new Harness();

        await h.Submit(Text, "alice.support@contoso.invalid");

        var ops = Assert.Single(h.OpsEvents);
        foreach (var leaked in new[] { Text, Upn, DisplayName, "alice.support@contoso.invalid" })
        {
            Assert.DoesNotContain(leaked, ops.Message);
            Assert.DoesNotContain(leaked, ops.Details ?? string.Empty);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData(null)]
    public async Task EmptyText_IsRejected_AndNothingIsStored(string? text)
    {
        var h = new Harness();

        var result = await h.Submit(text, null);

        Assert.Equal(GeneralFeedbackOutcome.BadRequest, result.Outcome);
        Assert.Empty(h.Repo.Saved);
        Assert.Empty(h.OpsEvents);
    }

    [Fact]
    public async Task TextAtTheLimit_IsAccepted_OneMoreCharacterIsRejected()
    {
        var max = Constants.SubmissionLimits.FeedbackTextMaxChars;

        var atLimit = await new Harness().Submit(new string('x', max), null);
        var overLimit = new Harness();
        var rejected = await overLimit.Submit(new string('x', max + 1), null);

        Assert.Equal(GeneralFeedbackOutcome.Ok, atLimit.Outcome);
        Assert.Equal(GeneralFeedbackOutcome.BadRequest, rejected.Outcome);
        Assert.Empty(overLimit.Repo.Saved);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("alice@localhost")]
    [InlineData("alice@contoso.invalid, bob@contoso.invalid")]
    [InlineData("Alice <alice@contoso.invalid>")]
    public async Task InvalidContactAddress_IsRejected(string email)
    {
        var h = new Harness();

        var result = await h.Submit(Text, email);

        Assert.Equal(GeneralFeedbackOutcome.BadRequest, result.Outcome);
        Assert.Empty(h.Repo.Saved);
    }

    [Fact]
    public async Task BlankContactAddress_IsStoredAsNone()
    {
        var h = new Harness();

        var result = await h.Submit(Text, "   ");

        Assert.Equal(GeneralFeedbackOutcome.Ok, result.Outcome);
        Assert.Null(Assert.Single(h.Repo.Saved).ContactEmail);
    }

    [Fact]
    public async Task TheDailyCap_RefusesTheNextSubmission_WithoutStoringIt()
    {
        var cap = Constants.SubmissionLimits.GeneralFeedbackPerUserPerDay;
        var belowCap = new Harness(recentCount: cap - 1);
        var atCap = new Harness(recentCount: cap);

        var accepted = await belowCap.Submit(Text, null);
        var refused = await atCap.Submit(Text, null);

        Assert.Equal(GeneralFeedbackOutcome.Ok, accepted.Outcome);
        Assert.Equal(GeneralFeedbackOutcome.RateLimited, refused.Outcome);
        Assert.Empty(atCap.Repo.Saved);
        Assert.Empty(atCap.OpsEvents);
    }

    [Fact]
    public async Task UnknownTenant_KeepsTheGuidLabel_AndCreatesNoConfiguration()
    {
        var h = new Harness(configExists: false);

        var result = await h.Submit(Text, null);

        Assert.Equal(GeneralFeedbackOutcome.Ok, result.Outcome);
        Assert.Null(Assert.Single(h.Repo.Saved).DomainName);
        Assert.Contains(NormalizedTenantId, Assert.Single(h.OpsEvents).Message);
        h.TenantConfig.Verify(c => c.GetConfigurationAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task NonGuidTenant_IsRejected()
    {
        var h = new Harness();

        var result = await h.Sut.ProcessSubmitAsync("not-a-guid", Upn, DisplayName,
            new GeneralFeedbackRequest { Message = Text }, Now);

        Assert.Equal(GeneralFeedbackOutcome.BadRequest, result.Outcome);
        Assert.Empty(h.Repo.Saved);
    }

    [Fact]
    public async Task StorageFailure_Is500_WithoutEventOrBell()
    {
        var h = new Harness();
        h.Repo.ThrowOnSave = new InvalidOperationException("simulated table outage");

        var result = await h.Submit(Text, null);

        Assert.Equal(GeneralFeedbackOutcome.InternalError, result.Outcome);
        Assert.Empty(h.OpsEvents);
        h.Bell.Verify(b => b.CreateNotificationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task EventFailure_StillAcceptsTheStoredSubmission()
    {
        var h = new Harness(opsEventThrows: true);

        var result = await h.Submit(Text, null);

        Assert.Equal(GeneralFeedbackOutcome.Ok, result.Outcome);
        Assert.Single(h.Repo.Saved);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public SubmitGeneralFeedbackFunction Sut { get; }
        public FakeFeedbackRepository Repo { get; } = new();
        public List<OpsEventEntry> OpsEvents { get; } = new();
        public Mock<TenantConfigurationService> TenantConfig { get; }
        public Mock<GlobalNotificationService> Bell { get; }

        public Harness(int recentCount = 0, bool configExists = true, bool opsEventThrows = false)
        {
            Repo.RecentCount = recentCount;

            TenantConfig = new Mock<TenantConfigurationService>(
                Mock.Of<IConfigRepository>(), Mock.Of<ILogger<TenantConfigurationService>>(), Mock.Of<IMemoryCache>())
            { CallBase = false };
            var config = TenantConfiguration.CreateDefault(NormalizedTenantId);
            config.DomainName = "contoso.invalid";
            TenantConfig.Setup(c => c.TryGetConfigurationAsync(NormalizedTenantId))
                .ReturnsAsync((config, configExists));

            Bell = new Mock<GlobalNotificationService>(
                Mock.Of<INotificationRepository>(), new FakeSignalRNotificationService(),
                Mock.Of<ILogger<GlobalNotificationService>>())
            { CallBase = false };

            Sut = new SubmitGeneralFeedbackFunction(
                NullLogger<SubmitGeneralFeedbackFunction>.Instance, Repo, TenantConfig.Object,
                BuildOpsEventService(opsEventThrows), Bell.Object);
        }

        public Task<GeneralFeedbackResult> Submit(string? text, string? contactEmail)
            => Sut.ProcessSubmitAsync(TenantId, Upn, DisplayName,
                new GeneralFeedbackRequest { Message = text!, ContactEmail = contactEmail }, Now);

        /// <summary>A real OpsEventService over a capturing repository, so the actual record method runs.</summary>
        private OpsEventService BuildOpsEventService(bool opsEventThrows)
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

            var adminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(),
                NullLogger<AdminConfigurationService>.Instance,
                new MemoryCache(new MemoryCacheOptions()));
            return new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance,
                TestNotifications.InertOpsAlertDispatch(adminConfig.Object));
        }
    }

    /// <summary>In-memory feedback repo; only the general-feedback members are expected.</summary>
    private sealed class FakeFeedbackRepository : IFeedbackRepository
    {
        public List<FeedbackEntry> Saved { get; } = new();
        public int RecentCount { get; set; }
        public DateTime? CountedSince { get; private set; }
        public Exception? ThrowOnSave { get; set; }

        public Task SaveGeneralFeedbackAsync(FeedbackEntry entry)
        {
            if (ThrowOnSave is { } ex) throw ex;
            entry.Type = FeedbackEntryType.General;
            entry.FeedbackId = $"{DateTime.MaxValue.Ticks - entry.InteractedAt!.Value.Ticks:D19}_0123456789ab";
            Saved.Add(entry);
            return Task.CompletedTask;
        }

        public Task<int> CountGeneralFeedbackSinceAsync(string upn, DateTime sinceUtc)
        {
            CountedSince = sinceUtc;
            return Task.FromResult(RecentCount);
        }

        public Task<FeedbackEntry?> GetInAppFeedbackAsync(string upn) => throw new NotSupportedException();
        public Task<FeedbackEntry?> UpdateInAppFeedbackAsync(string upn, Func<FeedbackEntry?, FeedbackEntry?> decide)
            => throw new NotSupportedException();
        public Task<FeedbackEntry?> GetOffboardingFeedbackAsync(string historyRowKey) => throw new NotSupportedException();
        public Task SaveOffboardingFeedbackAsync(FeedbackEntry entry) => throw new NotSupportedException();
        public Task<List<FeedbackEntry>> GetAllAsync() => throw new NotSupportedException();
    }
}
