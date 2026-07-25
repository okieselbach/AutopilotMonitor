using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Services;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Behavioural coverage for <see cref="TableHardwareRejectionNotificationTracker"/> — the dedup
/// store behind the two distress-driven bell notifications (hardware rejection per model,
/// TPM-PSS incompatibility per serial). <see cref="HardwareRejectionNotificationTrackerKeyTests"/>
/// pins the RowKey shape; this file pins what the tracker DOES with those keys.
///
/// CORRECTNESS GUARD: the "one bell ever" promise rests entirely on the 409 branch of
/// <c>AddEntityAsync</c> — a rewrite to Upsert or a get-then-add would silently start ringing
/// the bell on every distress report (a device with an incompatible TPM retries forever, so
/// "every report" means a notification storm, not a second bell).
///
/// The retention sweep is covered here too because <c>DeleteOlderThanAsync</c> prunes BOTH key
/// spaces from the shared table: pruning a <c>tpmpss|</c> row re-arms that device's bell.
/// </summary>
public class HardwareRejectionNotificationTrackerDedupTests
{
    private const string TenantId = "77777777-7777-7777-7777-777777777777";
    private const string OtherTenantId = "88888888-8888-8888-8888-888888888888";
    private const string Serial = "S4SQ8685";

    // ── TPM PSS: first bell, then never again ──────────────────────────────────────

    [Fact]
    public async Task TpmPss_FirstReport_RegistersRowAndReturnsTrue()
    {
        var harness = new Harness();

        var isFirst = await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(TenantId, Serial);

        Assert.True(isFirst);
        var entity = Assert.Single(harness.Store.Values);
        Assert.Equal(TenantId.ToLowerInvariant(), entity.PartitionKey);
        Assert.Equal("tpmpss|s4sq8685", entity.RowKey);
        // Serialization guard: the row must carry the fields the cleanup sweep and any future
        // operator query rely on — losing FirstNotifiedAt would make the row immortal.
        Assert.Equal(TenantId, entity.GetString("TenantId"));
        Assert.Equal(Serial, entity.GetString("SerialNumber"));
        Assert.NotNull(entity.GetDateTime("FirstNotifiedAt"));
    }

    [Fact]
    public async Task TpmPss_SecondReport_SameSerial_ReturnsFalse()
    {
        var harness = new Harness();

        Assert.True(await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(TenantId, Serial));
        Assert.False(await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(TenantId, Serial));
        Assert.Single(harness.Store);
    }

    [Theory]
    [InlineData("s4sq8685")]
    [InlineData("  S4SQ8685  ")]
    [InlineData("S4Sq8685")]
    public async Task TpmPss_SecondReport_CasingOrWhitespaceVariant_StillDeduped(string variant)
    {
        var harness = new Harness();

        Assert.True(await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(TenantId, Serial));
        Assert.False(await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(TenantId, variant));
    }

    [Fact]
    public async Task TpmPss_DifferentSerial_OrDifferentTenant_RingsAgain()
    {
        var harness = new Harness();

        Assert.True(await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(TenantId, Serial));
        Assert.True(await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(TenantId, "OTHER123"));
        Assert.True(await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(OtherTenantId, Serial));
        Assert.Equal(3, harness.Store.Count);
    }

    [Theory]
    [InlineData("", Serial)]
    [InlineData("   ", Serial)]
    [InlineData(TenantId, "")]
    [InlineData(TenantId, "   ")]
    public async Task TpmPss_BlankInput_ReturnsFalse_WithoutTouchingTheTable(string tenantId, string serial)
    {
        var harness = new Harness();

        Assert.False(await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(tenantId, serial));
        Assert.Empty(harness.Store);
        harness.Table.Verify(
            c => c.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TpmPss_UnexpectedTableFailure_ReturnsFalse()
    {
        // Fail-soft in the SUPPRESSING direction: the write may or may not have landed, so the
        // caller must not fire a bell it might fire again on the next report.
        var harness = new Harness { AddFailure = new RequestFailedException(503, "ServerBusy") };

        Assert.False(await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(TenantId, Serial));
    }

    // ── Hardware rejection: same contract, disjoint key space ──────────────────────

    [Fact]
    public async Task HardwareRejection_SecondReport_SameModel_ReturnsFalse()
    {
        var harness = new Harness();

        Assert.True(await harness.Sut.TryRegisterFirstNotificationAsync(TenantId, "Lenovo", "ThinkPad X1"));
        Assert.False(await harness.Sut.TryRegisterFirstNotificationAsync(TenantId, "LENOVO", " thinkpad x1 "));
        Assert.Single(harness.Store);
    }

    [Fact]
    public async Task HardwareRejection_AndTpmPss_DoNotDedupEachOtherInTheSharedTable()
    {
        var harness = new Harness();

        Assert.True(await harness.Sut.TryRegisterFirstNotificationAsync(TenantId, "Lenovo", Serial));
        Assert.True(await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(TenantId, Serial));
        Assert.Equal(2, harness.Store.Count);
    }

    // ── Retention sweep ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteOlderThanAsync_PrunesTpmPssRows_AndReArmsThatDevicesBell()
    {
        var harness = new Harness();
        Assert.True(await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(TenantId, Serial));
        harness.AgeRows(TimeSpan.FromDays(31));

        var deleted = await harness.Sut.DeleteOlderThanAsync(DateTime.UtcNow.AddDays(-30));

        Assert.Equal(1, deleted);
        Assert.Empty(harness.Store);
        // A device whose TPM was never fixed keeps reporting; after the retention window it is
        // worth one more bell rather than permanent silence.
        Assert.True(await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(TenantId, Serial));
    }

    [Fact]
    public async Task DeleteOlderThanAsync_KeepsRowsInsideTheWindow_DedupSurvives()
    {
        var harness = new Harness();
        Assert.True(await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(TenantId, Serial));
        Assert.True(await harness.Sut.TryRegisterFirstNotificationAsync(TenantId, "Lenovo", "ThinkPad X1"));

        var deleted = await harness.Sut.DeleteOlderThanAsync(DateTime.UtcNow.AddDays(-30));

        Assert.Equal(0, deleted);
        Assert.Equal(2, harness.Store.Count);
        Assert.False(await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(TenantId, Serial));
        Assert.False(await harness.Sut.TryRegisterFirstNotificationAsync(TenantId, "Lenovo", "ThinkPad X1"));
    }

    [Fact]
    public async Task DeleteOlderThanAsync_FiltersOnFirstNotifiedAt_NotTimestamp()
    {
        // Rows are insert-once, so FirstNotifiedAt is the creation time. Timestamp would look
        // equivalent but is rewritten by a table restore/copy, which would silently re-arm every
        // bell in the tenant at once.
        var harness = new Harness();
        var cutoff = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

        await harness.Sut.DeleteOlderThanAsync(cutoff);

        var filter = Assert.Single(harness.QueryFilters);
        Assert.Equal("FirstNotifiedAt lt datetime'2026-07-25T12:00:00Z'", filter);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_ContinuesAfterASingleRowFailure()
    {
        var harness = new Harness();
        Assert.True(await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(TenantId, Serial));
        Assert.True(await harness.Sut.TryRegisterFirstTpmPssNotificationAsync(TenantId, "OTHER123"));
        harness.AgeRows(TimeSpan.FromDays(31));
        harness.FailDeleteForRowKeys.Add("tpmpss|s4sq8685");

        var deleted = await harness.Sut.DeleteOlderThanAsync(DateTime.UtcNow.AddDays(-30));

        Assert.Equal(1, deleted);
        Assert.Equal("tpmpss|s4sq8685", Assert.Single(harness.Store.Values).RowKey);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_QueryFailure_ReturnsZero()
    {
        var harness = new Harness { QueryFailure = new RequestFailedException(500, "Boom") };

        Assert.Equal(0, await harness.Sut.DeleteOlderThanAsync(DateTime.UtcNow.AddDays(-30)));
    }

    // ── Harness ───────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        private static readonly Regex CutoffFilter =
            new(@"^FirstNotifiedAt lt datetime'(?<cutoff>[^']+)'$", RegexOptions.Compiled);

        public TableHardwareRejectionNotificationTracker Sut { get; }
        public Mock<TableClient> Table { get; }
        public Dictionary<(string Pk, string Rk), TableEntity> Store { get; } = new();
        public List<string> QueryFilters { get; } = new();
        public HashSet<string> FailDeleteForRowKeys { get; } = new();

        /// <summary>Non-409 failure injected into AddEntityAsync (fail-soft path).</summary>
        public Exception? AddFailure { get; init; }

        /// <summary>Failure injected into QueryAsync enumeration (sweep fail-soft path).</summary>
        public Exception? QueryFailure { get; init; }

        public Harness()
        {
            Table = new Mock<TableClient>();

            Table.Setup(c => c.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, CancellationToken>((e, _) =>
                {
                    if (AddFailure != null) throw AddFailure;
                    if (Store.ContainsKey((e.PartitionKey, e.RowKey)))
                        throw new RequestFailedException(409, "Conflict", "EntityAlreadyExists", null);
                    Store[(e.PartitionKey, e.RowKey)] = e;
                    return Task.FromResult(new Mock<Response>().Object);
                });

            Table.Setup(c => c.QueryAsync<TableEntity>(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, int?, IEnumerable<string>, CancellationToken>((filter, _, _, _) =>
                {
                    if (QueryFailure != null) throw QueryFailure;
                    QueryFilters.Add(filter);
                    var rows = Store.Values.Where(e => MatchesCutoff(filter, e)).ToList();
                    return AsyncPageable<TableEntity>.FromPages(new[]
                    {
                        Page<TableEntity>.FromValues(rows, null, new Mock<Response>().Object),
                    });
                });

            Table.Setup(c => c.DeleteEntityAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<ETag>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, string, ETag, CancellationToken>((pk, rk, _, _) =>
                {
                    if (FailDeleteForRowKeys.Contains(rk))
                        throw new RequestFailedException(500, "DeleteFailed");
                    Store.Remove((pk, rk));
                    return Task.FromResult(new Mock<Response>().Object);
                });

            var serviceClient = new Mock<TableServiceClient>();
            serviceClient.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns(Table.Object);

            Sut = new TableHardwareRejectionNotificationTracker(
                new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance),
                NullLogger<TableHardwareRejectionNotificationTracker>.Instance);
        }

        /// <summary>Backdates every stored row so a retention sweep sees it as expired.</summary>
        public void AgeRows(TimeSpan age)
        {
            foreach (var entity in Store.Values)
                entity["FirstNotifiedAt"] = DateTime.UtcNow - age;
        }

        /// <summary>
        /// Minimal server-side emulation of the OData filter the tracker builds. An unrecognised
        /// filter matches nothing, so a rewrite that no longer selects expired rows fails loudly
        /// here instead of leaving dead rows in production.
        /// </summary>
        private static bool MatchesCutoff(string filter, TableEntity entity)
        {
            var match = CutoffFilter.Match(filter ?? string.Empty);
            if (!match.Success) return false;

            var cutoff = DateTime.Parse(
                match.Groups["cutoff"].Value,
                null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
            var firstNotified = entity.GetDateTime("FirstNotifiedAt");
            return firstNotified.HasValue && firstNotified.Value.ToUniversalTime() < cutoff;
        }
    }
}
