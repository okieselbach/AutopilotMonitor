using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Round-trip + shape-guard coverage for the two partitions in the new <c>Feedback</c>
/// table. Per memory <c>feedback_table_storage_serialization</c>: every new field in
/// <see cref="FeedbackEntry"/> MUST be exercised here so silent drops surface in tests,
/// not in production.
/// <para>
/// The table is intentionally NOT in any offboarding-wipe list — these tests are also the
/// canonical "this storage survives offboarding" contract; if anyone later wires Feedback
/// into <c>TenantOffboardingHandler.DiscriminatorTables</c>, the In-App-survives-offboarding
/// guarantee breaks and the user-feedback Reports tab loses history.
/// </para>
/// </summary>
public class TableFeedbackRepositoryTests
{
    private const string TenantId = "88888888-8888-8888-8888-888888888888";
    private const string Upn = "alice@contoso.invalid";
    private const string HistoryRowKey = "20260519091523123_88888888-8888-8888-8888-888888888888";

    [Fact]
    public async Task InApp_RoundTrips_AllFields()
    {
        var harness = new Harness();
        var entry = new FeedbackEntry
        {
            Upn = Upn,
            TenantId = TenantId,
            DisplayName = "Alice (Contoso)",
            Rating = 4,
            Comment = "Mostly good — desktop arrival detection is a bit slow.",
            Dismissed = false,
            Submitted = true,
            InteractedAt = new DateTime(2026, 5, 19, 9, 15, 23, DateTimeKind.Utc),
        };

        await SaveInApp(harness, entry);

        var fetched = await harness.Sut.GetInAppFeedbackAsync(Upn);
        Assert.NotNull(fetched);
        Assert.Equal(FeedbackEntryType.InApp, fetched!.Type);
        Assert.Equal(Upn, fetched.Upn);
        Assert.Equal(TenantId, fetched.TenantId);
        Assert.Equal("Alice (Contoso)", fetched.DisplayName);
        Assert.Equal(4, fetched.Rating);
        Assert.Equal("Mostly good — desktop arrival detection is a bit slow.", fetched.Comment);
        Assert.False(fetched.Dismissed);
        Assert.True(fetched.Submitted);
        Assert.Equal(entry.InteractedAt, fetched.InteractedAt);
    }

    [Fact]
    public async Task InApp_Save_LowercasesUpn_ForRowKey()
    {
        var harness = new Harness();
        await SaveInApp(harness, new FeedbackEntry
        {
            Upn = "Alice@Contoso.Invalid",
            TenantId = TenantId,
            DisplayName = "Alice",
            Rating = 5,
            Submitted = true,
            InteractedAt = DateTime.UtcNow,
        });

        // Get by lowercased upn must hit. Different-case lookup hits the same row because the
        // function-side endpoint forwards the auth-claim upn verbatim while storage
        // normalises — pin both halves of that contract here.
        var fetchedLower = await harness.Sut.GetInAppFeedbackAsync("alice@contoso.invalid");
        var fetchedMixed = await harness.Sut.GetInAppFeedbackAsync("Alice@Contoso.Invalid");
        Assert.NotNull(fetchedLower);
        Assert.NotNull(fetchedMixed);
    }

    [Fact]
    public async Task InApp_Dismissed_RoundTrips_NullRatingAndNullComment()
    {
        var harness = new Harness();
        await SaveInApp(harness, new FeedbackEntry
        {
            Upn = Upn,
            TenantId = TenantId,
            DisplayName = "Alice",
            Rating = null,
            Comment = null,
            Dismissed = true,
            Submitted = false,
            InteractedAt = DateTime.UtcNow,
        });

        var fetched = await harness.Sut.GetInAppFeedbackAsync(Upn);
        Assert.NotNull(fetched);
        Assert.Null(fetched!.Rating);
        Assert.Null(fetched.Comment);
        Assert.True(fetched.Dismissed);
        Assert.False(fetched.Submitted);
    }

    [Fact]
    public async Task InApp_Get_404_ReturnsNull()
    {
        var harness = new Harness();
        Assert.Null(await harness.Sut.GetInAppFeedbackAsync(Upn));
    }

    [Fact]
    public async Task Offboarding_RoundTrips_AllFields()
    {
        var harness = new Harness();
        var entry = new FeedbackEntry
        {
            HistoryRowKey = HistoryRowKey,
            TenantId = TenantId,
            Upn = Upn,
            DisplayName = "Alice (Contoso)",
            DomainName = "contoso.invalid",
            Comment = "Pricing was too high for our use case.",
            InteractedAt = new DateTime(2026, 5, 19, 9, 20, 0, DateTimeKind.Utc),
        };

        await harness.Sut.SaveOffboardingFeedbackAsync(entry);

        var fetched = await harness.Sut.GetOffboardingFeedbackAsync(HistoryRowKey);
        Assert.NotNull(fetched);
        Assert.Equal(FeedbackEntryType.Offboarding, fetched!.Type);
        Assert.Equal(HistoryRowKey, fetched.HistoryRowKey);
        Assert.Equal(TenantId, fetched.TenantId);
        Assert.Equal(Upn, fetched.Upn);
        Assert.Equal("contoso.invalid", fetched.DomainName);
        Assert.Equal("Pricing was too high for our use case.", fetched.Comment);
        Assert.Equal(entry.InteractedAt, fetched.InteractedAt);
    }

    [Fact]
    public async Task Offboarding_Save_RejectsMissingHistoryRowKey()
    {
        var harness = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Sut.SaveOffboardingFeedbackAsync(new FeedbackEntry
            {
                TenantId = TenantId,
                Upn = Upn,
                Comment = "no historyRowKey set",
            }));
    }

    [Fact]
    public async Task Offboarding_Get_RejectsEmptyHistoryRowKey()
    {
        var harness = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Sut.GetOffboardingFeedbackAsync(string.Empty));
    }

    [Fact]
    public async Task Save_Stamps_TypeDiscriminator_RegardlessOfCallerSetting()
    {
        // Defensive: callers may forget to set Type; the repo MUST stamp the partition's own
        // discriminator on every save so a misrouted Save can never poison the other partition.
        var harness = new Harness();
        await SaveInApp(harness, new FeedbackEntry
        {
            Type = "WRONG",  // caller error
            Upn = Upn,
            TenantId = TenantId,
            DisplayName = "Alice",
            Rating = 5,
            Submitted = true,
            InteractedAt = DateTime.UtcNow,
        });
        var fetched = await harness.Sut.GetInAppFeedbackAsync(Upn);
        Assert.Equal(FeedbackEntryType.InApp, fetched!.Type);

        await harness.Sut.SaveOffboardingFeedbackAsync(new FeedbackEntry
        {
            Type = "WRONG",
            HistoryRowKey = HistoryRowKey,
            TenantId = TenantId,
            Upn = Upn,
            Comment = "x",
            InteractedAt = DateTime.UtcNow,
        });
        var offb = await harness.Sut.GetOffboardingFeedbackAsync(HistoryRowKey);
        Assert.Equal(FeedbackEntryType.Offboarding, offb!.Type);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsBothPartitions_WithDiscriminator()
    {
        // Reports page reads via GetAllAsync and filters client-side by Type. Confirm the
        // mixed-partition return shape works end-to-end.
        var harness = new Harness();

        await SaveInApp(harness, new FeedbackEntry
        {
            Upn = "alice@contoso.invalid",
            TenantId = TenantId,
            DisplayName = "Alice",
            Rating = 5,
            Submitted = true,
            InteractedAt = DateTime.UtcNow,
        });
        await SaveInApp(harness, new FeedbackEntry
        {
            Upn = "bob@fabrikam.invalid",
            TenantId = "99999999-9999-9999-9999-999999999999",
            DisplayName = "Bob",
            Rating = null,
            Dismissed = true,
            InteractedAt = DateTime.UtcNow,
        });
        await harness.Sut.SaveOffboardingFeedbackAsync(new FeedbackEntry
        {
            HistoryRowKey = HistoryRowKey,
            TenantId = TenantId,
            Upn = "alice@contoso.invalid",
            DisplayName = "Alice",
            DomainName = "contoso.invalid",
            Comment = "Pricing too high.",
            InteractedAt = DateTime.UtcNow,
        });

        var all = await harness.Sut.GetAllAsync();

        var inApp = all.Where(e => e.Type == FeedbackEntryType.InApp).ToList();
        var offb = all.Where(e => e.Type == FeedbackEntryType.Offboarding).ToList();

        Assert.Equal(2, inApp.Count);
        Assert.Single(offb);
        Assert.Equal("contoso.invalid", offb[0].DomainName);
        Assert.Equal(HistoryRowKey, offb[0].HistoryRowKey);
    }

    // ── In-App: dismissal count and the conditional write ───────────────────

    [Fact]
    public async Task InApp_DismissCount_RoundTrips()
    {
        var harness = new Harness();
        await SaveInApp(harness, new FeedbackEntry
        {
            Upn = Upn, TenantId = TenantId, DisplayName = "Alice", Dismissed = true, DismissCount = 2,
            InteractedAt = DateTime.UtcNow,
        });

        Assert.Equal(2, (await harness.Sut.GetInAppFeedbackAsync(Upn))!.DismissCount);
    }

    [Theory]
    [InlineData(true, false, 1)]  // dismissed before the count existed → one recorded dismissal
    [InlineData(false, true, 0)]  // rated
    public async Task InApp_RowsWithoutCount_ReadAsAtMostOneDismissal(bool dismissed, bool submitted, int expected)
    {
        var harness = new Harness();
        harness.Seed(new TableEntity("InApp", Upn)
        {
            ["TenantId"] = TenantId,
            ["DisplayName"] = "Alice",
            ["Dismissed"] = dismissed,
            ["Submitted"] = submitted,
            ["InteractedAt"] = new DateTime(2026, 4, 18, 2, 12, 0, DateTimeKind.Utc),
        });

        Assert.Equal(expected, (await harness.Sut.GetInAppFeedbackAsync(Upn))!.DismissCount);
    }

    [Fact]
    public async Task UpdateInApp_DecisionNull_WritesNothing()
    {
        var harness = new Harness();

        var written = await harness.Sut.UpdateInAppFeedbackAsync(Upn, _ => null);

        Assert.Null(written);
        Assert.Null(await harness.Sut.GetInAppFeedbackAsync(Upn));
    }

    [Fact]
    public async Task UpdateInApp_ConcurrentWrite_DecidesAgainOnTheStoredRow()
    {
        // Tab A rates while tab B's dismissal is in flight: B read the row before A wrote, so its
        // conditional write fails and B decides again on A's rating — which a dismissal never replaces.
        var harness = new Harness();
        await SaveInApp(harness, new FeedbackEntry
        {
            Upn = Upn, TenantId = TenantId, DisplayName = "Alice", Dismissed = true, DismissCount = 1,
            InteractedAt = DateTime.UtcNow.AddDays(-61),
        });
        harness.BeforeNextWrite = () => harness.Seed(new TableEntity("InApp", Upn)
        {
            ["TenantId"] = TenantId, ["DisplayName"] = "Alice", ["Rating"] = 5, ["Comment"] = "Great",
            ["Dismissed"] = false, ["Submitted"] = true, ["DismissCount"] = 1, ["InteractedAt"] = DateTime.UtcNow,
        });
        var decisions = 0;

        var written = await harness.Sut.UpdateInAppFeedbackAsync(Upn, current =>
        {
            decisions++;
            return current!.Submitted ? null : new FeedbackEntry
            {
                Upn = Upn, TenantId = TenantId, DisplayName = "Alice", Dismissed = true, DismissCount = 2,
                InteractedAt = DateTime.UtcNow,
            };
        });

        Assert.Null(written);
        Assert.Equal(2, decisions);
        var stored = await harness.Sut.GetInAppFeedbackAsync(Upn);
        Assert.True(stored!.Submitted);
        Assert.Equal(5, stored.Rating);
    }

    private static async Task SaveInApp(Harness harness, FeedbackEntry entry)
        => await harness.Sut.UpdateInAppFeedbackAsync(entry.Upn, _ => entry);

    // ── General (help-menu feedback) ────────────────────────────────────────

    [Fact]
    public async Task General_RoundTrips_AllFields_WithNewestFirstRowKey()
    {
        var harness = new Harness();
        var at = new DateTime(2026, 10, 5, 9, 30, 0, DateTimeKind.Utc);
        var entry = new FeedbackEntry
        {
            Upn = "Alice@Contoso.Invalid",
            TenantId = TenantId,
            DisplayName = "Alice (Contoso)",
            DomainName = "contoso.invalid",
            Comment = "The timeline filter could remember my choice.",
            ContactEmail = "alice.support@contoso.invalid",
            InteractedAt = at,
        };

        await harness.Sut.SaveGeneralFeedbackAsync(entry);

        var fetched = Assert.Single(await harness.Sut.GetAllAsync());
        Assert.Equal(FeedbackEntryType.General, fetched.Type);
        Assert.Equal(entry.FeedbackId, fetched.FeedbackId);
        Assert.Matches("^[0-9]{19}_[0-9a-f]{12}$", fetched.FeedbackId!);
        Assert.StartsWith((DateTime.MaxValue.Ticks - at.Ticks).ToString("D19"), fetched.FeedbackId!);
        Assert.Equal("alice@contoso.invalid", fetched.Upn);
        Assert.Equal(TenantId, fetched.TenantId);
        Assert.Equal("Alice (Contoso)", fetched.DisplayName);
        Assert.Equal("contoso.invalid", fetched.DomainName);
        Assert.Equal("The timeline filter could remember my choice.", fetched.Comment);
        Assert.Equal("alice.support@contoso.invalid", fetched.ContactEmail);
        Assert.Equal(at, fetched.InteractedAt);
    }

    [Fact]
    public async Task General_EverySubmission_IsItsOwnRow()
    {
        // Unlike the in-app rating (one row per user), a second message never replaces the first.
        var harness = new Harness();
        var at = DateTime.UtcNow;
        for (var i = 0; i < 2; i++)
        {
            await harness.Sut.SaveGeneralFeedbackAsync(new FeedbackEntry
            {
                Upn = Upn, TenantId = TenantId, DisplayName = "Alice", Comment = $"Message {i}", InteractedAt = at,
            });
        }

        var all = await harness.Sut.GetAllAsync();
        Assert.Equal(2, all.Count(e => e.Type == FeedbackEntryType.General));
    }

    [Fact]
    public async Task CountGeneralFeedbackSince_CountsOnlyThatUser_InsideTheWindow()
    {
        var harness = new Harness();
        var now = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        async Task Save(string upn, DateTime at) => await harness.Sut.SaveGeneralFeedbackAsync(new FeedbackEntry
        {
            Upn = upn, TenantId = TenantId, DisplayName = "x", Comment = "c", InteractedAt = at,
        });
        await Save(Upn, now.AddHours(-1));
        await Save(Upn, now.AddHours(-23));
        await Save(Upn, now.AddHours(-24));               // exactly at the window start → counted
        await Save(Upn, now.AddHours(-25));               // outside
        await Save("bob@fabrikam.invalid", now.AddHours(-1));
        await SaveInApp(harness, new FeedbackEntry
        {
            Upn = Upn, TenantId = TenantId, DisplayName = "x", Rating = 5, Submitted = true, InteractedAt = now,
        });

        var count = await harness.Sut.CountGeneralFeedbackSinceAsync("ALICE@contoso.invalid", now.AddHours(-24));

        Assert.Equal(3, count);
        Assert.Contains("PartitionKey eq 'General'", harness.LastFilter);
        Assert.Contains("Upn eq 'alice@contoso.invalid'", harness.LastFilter);
        Assert.Contains($"RowKey lt '{(DateTime.MaxValue.Ticks - now.AddHours(-24).AddTicks(-1).Ticks):D19}'", harness.LastFilter);
    }

    [Fact]
    public async Task CountGeneralFeedbackSince_EscapesQuotesInTheUpn()
    {
        var harness = new Harness();

        await harness.Sut.CountGeneralFeedbackSinceAsync("o'brien@contoso.invalid", DateTime.UtcNow.AddHours(-24));

        Assert.Contains("Upn eq 'o''brien@contoso.invalid'", harness.LastFilter);
    }

    // ── Harness — minimal TableClient mock with a (PK,RK) → TableEntity store ──

    private sealed class Harness
    {
        public TableFeedbackRepository Sut { get; }

        private readonly Dictionary<(string Pk, string Rk), TableEntity> _store = new();

        /// <summary>Filter of the last QueryAsync call (the mock itself ignores it and returns every row).</summary>
        public string? LastFilter { get; private set; }

        /// <summary>Runs once just before the next Add/Update — a concurrent writer between read and write.</summary>
        public Action? BeforeNextWrite { get; set; }

        /// <summary>Stores a row as another writer would (fresh ETag).</summary>
        public void Seed(TableEntity entity)
        {
            entity.ETag = new ETag(Guid.NewGuid().ToString("N"));
            _store[(entity.PartitionKey, entity.RowKey)] = entity;
        }

        private void RunBeforeWrite()
        {
            var hook = BeforeNextWrite;
            BeforeNextWrite = null;
            hook?.Invoke();
        }

        public Harness()
        {
            var mockTableClient = new Mock<TableClient>();

            mockTableClient
                .Setup(c => c.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, CancellationToken>((e, _) =>
                {
                    RunBeforeWrite();
                    if (_store.ContainsKey((e.PartitionKey, e.RowKey)))
                        throw new RequestFailedException(409, "Conflict", "EntityAlreadyExists", null);
                    Seed(e);
                    return Task.FromResult(new Mock<Response>().Object);
                });

            mockTableClient
                .Setup(c => c.UpdateEntityAsync(
                    It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, ifMatch, _, _) =>
                {
                    RunBeforeWrite();
                    if (!_store.TryGetValue((e.PartitionKey, e.RowKey), out var current))
                        throw new RequestFailedException(404, "NotFound", "ResourceNotFound", null);
                    if (current.ETag != ifMatch)
                        throw new RequestFailedException(412, "Precondition Failed", "UpdateConditionNotSatisfied", null);
                    Seed(e);
                    return Task.FromResult(new Mock<Response>().Object);
                });

            mockTableClient
                .Setup(c => c.UpsertEntityAsync(
                    It.IsAny<TableEntity>(),
                    It.IsAny<TableUpdateMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<TableEntity, TableUpdateMode, CancellationToken>((e, _, _) =>
                {
                    _store[(e.PartitionKey, e.RowKey)] = e;
                    return Task.FromResult(new Mock<Response>().Object);
                });

            mockTableClient
                .Setup(c => c.GetEntityAsync<TableEntity>(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, string, IEnumerable<string>, CancellationToken>((pk, rk, _, _) =>
                {
                    if (!_store.TryGetValue((pk, rk), out var entity))
                        throw new RequestFailedException(404, "NotFound", "ResourceNotFound", null);
                    return Task.FromResult(Response.FromValue(entity, new Mock<Response>().Object));
                });

            // QueryAsync — ignores the filter (recorded for assertions), returns all stored entities.
            mockTableClient
                .Setup(c => c.QueryAsync<TableEntity>(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, int?, IEnumerable<string>, CancellationToken>((filter, _, _, _) =>
                {
                    LastFilter = filter;
                    return AsyncPageable<TableEntity>.FromPages(new[]
                    {
                        Page<TableEntity>.FromValues(_store.Values.ToList(), null, new Mock<Response>().Object),
                    });
                });

            var mockServiceClient = new Mock<TableServiceClient>();
            mockServiceClient.Setup(s => s.GetTableClient(It.IsAny<string>()))
                .Returns(mockTableClient.Object);

            var storage = new TableStorageService(
                mockServiceClient.Object,
                NullLogger<TableStorageService>.Instance);
            Sut = new TableFeedbackRepository(storage, NullLogger<TableFeedbackRepository>.Instance);
        }
    }
}
