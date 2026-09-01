using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Tests.Offboarding;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Offboarding;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Unit tests for the offboarding-feedback submission helper. The HTTP entrypoint itself
/// sits on top of <see cref="HttpRequestData"/> (abstract, hard to fake without booting the
/// Functions host) so these tests target <c>ProcessSubmitAsync</c> directly — same pattern
/// used by <c>TenantOffboardFunctionTests</c> for <c>ResumeExistingMarkerAsync</c>.
/// </summary>
public sealed class SubmitOffboardingFeedbackFunctionTests
{
    private const string TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string HistoryRowKey = "20260519091523123_aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Upn = "alice@contoso.invalid";
    private const string DisplayName = "Alice (Contoso)";

    [Fact]
    public async Task Submit_HappyPath_PersistsFeedback_AndEmitsOpsEvent()
    {
        var h = Harness.WithInProgressMarker();

        var result = await h.Sut.ProcessSubmitAsync(
            TenantId, Upn, DisplayName, "Pricing was too high for our use case.");

        Assert.Equal(SubmitOutcome.Ok, result.Outcome);
        Assert.NotNull(h.Repo.PersistedOffboardingFeedback);
        Assert.Equal(HistoryRowKey, h.Repo.PersistedOffboardingFeedback!.HistoryRowKey);
        Assert.Equal(TenantId, h.Repo.PersistedOffboardingFeedback.TenantId);
        Assert.Equal(Upn, h.Repo.PersistedOffboardingFeedback.Upn);
        Assert.Equal(DisplayName, h.Repo.PersistedOffboardingFeedback.DisplayName);
        Assert.Equal("contoso.invalid", h.Repo.PersistedOffboardingFeedback.DomainName);
        Assert.Equal("Pricing was too high for our use case.", h.Repo.PersistedOffboardingFeedback.Comment);
        Assert.NotNull(h.Repo.PersistedOffboardingFeedback.InteractedAt);

        // OpsEvent emitted with the canonical type catalog-registered for Telegram-routing.
        var ops = Assert.Single(h.OpsEvents);
        Assert.Equal("OffboardingFeedbackReceived", ops.EventType);
        Assert.Equal(TenantId, ops.TenantId);
        Assert.Equal(Upn, ops.UserId);
    }

    [Fact]
    public async Task Submit_TrimsAndTruncatesComment_To4096Chars()
    {
        // Departing tenants often have more context to share than the in-app 500-char
        // limit allows; the offboarding endpoint accepts up to 4096 chars (still well
        // under the Azure Tables 64 KB property cap). Anything beyond is silently
        // truncated rather than rejected so the user's submit doesn't fail outright.
        var h = Harness.WithInProgressMarker();
        var longComment = new string('x', 5000);

        var result = await h.Sut.ProcessSubmitAsync(TenantId, Upn, DisplayName, "   " + longComment + "   ");

        Assert.Equal(SubmitOutcome.Ok, result.Outcome);
        Assert.Equal(4096, h.Repo.PersistedOffboardingFeedback!.Comment!.Length);
        Assert.DoesNotContain(" ", h.Repo.PersistedOffboardingFeedback.Comment!.Substring(0, 1));
    }

    [Fact]
    public async Task Submit_EmptyComment_400_AndNoSave()
    {
        var h = Harness.WithInProgressMarker();

        var result = await h.Sut.ProcessSubmitAsync(TenantId, Upn, DisplayName, "");

        Assert.Equal(SubmitOutcome.BadRequest, result.Outcome);
        Assert.Null(h.Repo.PersistedOffboardingFeedback);
        Assert.Empty(h.OpsEvents);
    }

    [Fact]
    public async Task Submit_WhitespaceOnlyComment_400_AndNoSave()
    {
        var h = Harness.WithInProgressMarker();

        var result = await h.Sut.ProcessSubmitAsync(TenantId, Upn, DisplayName, "    \t\n  ");

        Assert.Equal(SubmitOutcome.BadRequest, result.Outcome);
        Assert.Null(h.Repo.PersistedOffboardingFeedback);
    }

    [Fact]
    public async Task Submit_NullComment_400_AndNoSave()
    {
        var h = Harness.WithInProgressMarker();

        var result = await h.Sut.ProcessSubmitAsync(TenantId, Upn, DisplayName, null);

        Assert.Equal(SubmitOutcome.BadRequest, result.Outcome);
        Assert.Null(h.Repo.PersistedOffboardingFeedback);
    }

    [Fact]
    public async Task Submit_NoMarker_404_AndNoSave()
    {
        var h = Harness.WithNoMarker();

        var result = await h.Sut.ProcessSubmitAsync(TenantId, Upn, DisplayName, "Some thoughts.");

        Assert.Equal(SubmitOutcome.NotFound, result.Outcome);
        Assert.Null(h.Repo.PersistedOffboardingFeedback);
    }

    [Fact]
    public async Task Submit_CompletedMarker_409_AndNoSave()
    {
        var h = Harness.WithMarker("Completed");

        var result = await h.Sut.ProcessSubmitAsync(TenantId, Upn, DisplayName, "Too late, sorry");

        Assert.Equal(SubmitOutcome.Conflict, result.Outcome);
        Assert.Contains("Completed", result.Message ?? "");
        Assert.Null(h.Repo.PersistedOffboardingFeedback);
    }

    [Fact]
    public async Task Submit_FailedMarker_409_AndNoSave()
    {
        var h = Harness.WithMarker("Failed");

        var result = await h.Sut.ProcessSubmitAsync(TenantId, Upn, DisplayName, "Feedback");

        Assert.Equal(SubmitOutcome.Conflict, result.Outcome);
        Assert.Null(h.Repo.PersistedOffboardingFeedback);
    }

    [Fact]
    public async Task Submit_InitiatedMarker_OkPath()
    {
        var h = Harness.WithMarker("Initiated");

        var result = await h.Sut.ProcessSubmitAsync(TenantId, Upn, DisplayName, "Initial thoughts.");

        Assert.Equal(SubmitOutcome.Ok, result.Outcome);
    }

    [Fact]
    public async Task Submit_RepositoryThrows_500_AndNoOpsEvent()
    {
        var h = Harness.WithInProgressMarker();
        h.Repo.ThrowOnSaveOffboardingFeedback =
            new InvalidOperationException("simulated table-storage outage");

        var result = await h.Sut.ProcessSubmitAsync(TenantId, Upn, DisplayName, "Feedback");

        Assert.Equal(SubmitOutcome.InternalError, result.Outcome);
        Assert.Empty(h.OpsEvents);
    }

    [Fact]
    public async Task Submit_OpsEventThrows_StillReturnsOk_BestEffort()
    {
        // OpsEvent is best-effort observability — its failure must NOT mask the successful
        // user-feedback persistence. The end user's submit succeeded; the Ops dashboard
        // misses one row.
        var h = Harness.WithInProgressMarker(opsEventThrows: true);

        var result = await h.Sut.ProcessSubmitAsync(TenantId, Upn, DisplayName, "Feedback");

        Assert.Equal(SubmitOutcome.Ok, result.Outcome);
        Assert.NotNull(h.Repo.PersistedOffboardingFeedback);
    }

    [Fact]
    public async Task Submit_HistoryRowMissing_StillSucceeds_WithNullDomainName()
    {
        // Defensive: marker references a history rowKey that no longer resolves (timing race
        // or admin manually deleted history). Submit still succeeds; DomainName falls through
        // to null so the Reports page shows the tenantId GUID instead of a friendly domain.
        var h = Harness.WithInProgressMarker(includeHistoryRow: false);

        var result = await h.Sut.ProcessSubmitAsync(TenantId, Upn, DisplayName, "Feedback");

        Assert.Equal(SubmitOutcome.Ok, result.Outcome);
        Assert.NotNull(h.Repo.PersistedOffboardingFeedback);
        Assert.Null(h.Repo.PersistedOffboardingFeedback!.DomainName);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public SubmitOffboardingFeedbackFunction Sut { get; }
        public FakeOffboardingAuditRepository Audit { get; }
        public FakeFeedbackRepository Repo { get; }
        public List<OpsEventEntry> OpsEvents { get; } = new();

        private Harness(FakeOffboardingAuditRepository audit, FakeFeedbackRepository repo,
            OpsEventService opsEvents, List<OpsEventEntry> opsCapture)
        {
            Audit = audit; Repo = repo; OpsEvents = opsCapture;
            Sut = new SubmitOffboardingFeedbackFunction(
                NullLogger<SubmitOffboardingFeedbackFunction>.Instance,
                audit, repo, opsEvents);
        }

        public static Harness WithInProgressMarker(bool includeHistoryRow = true, bool opsEventThrows = false)
            => Build("InProgress", includeHistoryRow, opsEventThrows);

        public static Harness WithMarker(string status, bool includeHistoryRow = true)
            => Build(status, includeHistoryRow, opsEventThrows: false);

        public static Harness WithNoMarker()
        {
            var (ops, capture) = BuildOpsEventService(opsEventThrows: false);
            return new Harness(new FakeOffboardingAuditRepository(),
                new FakeFeedbackRepository(), ops, capture);
        }

        private static Harness Build(string status, bool includeHistoryRow, bool opsEventThrows)
        {
            var audit = new FakeOffboardingAuditRepository();
            audit.Markers[TenantId] = new OffboardingMarkerEntry
            {
                PartitionKey = Constants.OffboardingPartitionKeys.Marker,
                RowKey = TenantId,
                TenantId = TenantId,
                OffboardingHistoryRowKey = HistoryRowKey,
                InitiatedAt = DateTime.UtcNow.AddMinutes(-1),
                InitiatedBy = Upn,
                Status = status,
            };
            if (includeHistoryRow)
            {
                audit.History[HistoryRowKey] = new OffboardingHistoryEntry
                {
                    PartitionKey = Constants.OffboardingPartitionKeys.History,
                    RowKey = HistoryRowKey,
                    TenantId = TenantId,
                    DomainName = "contoso.invalid",
                    InitiatedBy = Upn,
                    OffboardedAt = DateTime.UtcNow.AddMinutes(-1),
                    Status = status,
                };
            }
            var (ops, capture) = BuildOpsEventService(opsEventThrows);
            return new Harness(audit, new FakeFeedbackRepository(), ops, capture);
        }

        /// <summary>
        /// Builds a real <see cref="OpsEventService"/> with a stub repository that captures
        /// every <c>SaveOpsEventAsync</c> into a list for assertions. Real service =&gt; we
        /// exercise the actual <c>RecordOffboardingFeedbackReceivedAsync</c> branch, including
        /// the catalog-registered EventType name.
        /// </summary>
        private static (OpsEventService Service, List<OpsEventEntry> Captured) BuildOpsEventService(bool opsEventThrows)
        {
            var captured = new List<OpsEventEntry>();
            var opsRepo = new Mock<IOpsEventRepository>();
            opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
                .Callback<OpsEventEntry>(e =>
                {
                    if (opsEventThrows)
                        throw new InvalidOperationException("simulated OpsEvent storage outage");
                    captured.Add(e);
                })
                .Returns(Task.CompletedTask);

            // Alert-dispatch dependencies are inert: empty AdminConfig service + bare HTTP
            // clients. The feedback path emits Info-severity so even if alerts ARE evaluated,
            // no Telegram/webhook fires for an unconfigured rule set.
            var memCache = new MemoryCache(new MemoryCacheOptions());
            var adminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(),
                NullLogger<AdminConfigurationService>.Instance,
                memCache);
            var alertDispatch = TestNotifications.InertOpsAlertDispatch(adminConfig.Object);

            return (
                new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch),
                captured);
        }
    }

    /// <summary>
    /// In-memory feedback repo. Only <see cref="SaveOffboardingFeedbackAsync"/> is exercised
    /// by this function; the other methods throw to make accidental coupling visible.
    /// </summary>
    private sealed class FakeFeedbackRepository : IFeedbackRepository
    {
        public FeedbackEntry? PersistedOffboardingFeedback { get; private set; }
        public Exception? ThrowOnSaveOffboardingFeedback { get; set; }

        public Task<FeedbackEntry?> GetInAppFeedbackAsync(string upn)
            => throw new NotSupportedException("Test should not read InApp feedback");

        public Task SaveInAppFeedbackAsync(FeedbackEntry entry)
            => throw new NotSupportedException("Test should not write InApp feedback");

        public Task<FeedbackEntry?> GetOffboardingFeedbackAsync(string historyRowKey)
            => throw new NotSupportedException("Test should not read offboarding feedback");

        public Task SaveOffboardingFeedbackAsync(FeedbackEntry entry)
        {
            if (ThrowOnSaveOffboardingFeedback is { } ex) throw ex;
            PersistedOffboardingFeedback = entry;
            return Task.CompletedTask;
        }

        public Task<List<FeedbackEntry>> GetAllAsync()
            => throw new NotSupportedException("Test should not enumerate feedback");
    }
}
