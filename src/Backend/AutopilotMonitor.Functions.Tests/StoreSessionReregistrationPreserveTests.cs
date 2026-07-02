using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Re-registration preservation contract for <see cref="TableStorageService.StoreSessionAsync"/>.
/// On agent restart the agent re-registers the SAME session id; StoreSessionAsync rewrites the
/// Sessions row with UpsertEntity (Replace mode), which drops any field NOT carried over from the
/// existing row. Cumulative counters maintained incrementally by IncrementSessionEventCountAsync
/// (EventCount, PlatformScriptCount, RemediationScriptCount) must therefore be preserved — otherwise
/// every restart zeroes them, undercounting scripts and drifting Sessions vs. SessionsIndex (the
/// index keeps the pre-restart high-water value because the increment-merge only writes a counter
/// when its increment > 0). Regression guard for that drift.
/// </summary>
public class StoreSessionReregistrationPreserveTests
{
    private const string TenantId  = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task StoreSessionAsync_preserves_cumulative_counters_across_reregistration()
    {
        // Existing row as left by a prior agent run: counters already accumulated.
        var existing = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"]              = new DateTimeOffset(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            ["Status"]                 = "InProgress",
            ["EventCount"]             = 42,
            ["PlatformScriptCount"]    = 3,
            ["RemediationScriptCount"] = 5,
            ["RebootCount"]            = 7,
        };
        existing.ETag = new ETag("0xEXISTING");

        var harness = new Harness(existing);

        var registration = new SessionRegistration
        {
            TenantId  = TenantId,
            SessionId = SessionId,
            // Agent restart reports a later StartedAt — StoreSessionAsync keeps the earlier existing one,
            // but that is orthogonal to the counter-preservation under test.
            StartedAt = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
            SerialNumber = "SN-1",
            Manufacturer = "Contoso",
            Model        = "Model-X",
            DeviceName   = "PC-1",
        };

        var ok = await harness.Sut.StoreSessionAsync(registration);

        Assert.True(ok);
        Assert.NotNull(harness.Written);
        // The Replace-written row must still carry the cumulative counters from the existing row.
        Assert.Equal(3, harness.Written!.GetInt32("PlatformScriptCount"));
        Assert.Equal(5, harness.Written.GetInt32("RemediationScriptCount"));
        // RebootCount is the critical case: a reboot is the very thing that triggers a fresh
        // agent registration, so this Replace runs precisely when a real in-flight reboot count
        // must not be zeroed.
        Assert.Equal(7, harness.Written.GetInt32("RebootCount"));
        // EventCount preservation is the established sibling behaviour — assert it as a sanity anchor.
        Assert.Equal(42, harness.Written.GetInt32("EventCount"));
    }

    [Fact]
    public async Task StoreSessionAsync_defaults_counters_to_zero_for_a_brand_new_session()
    {
        // No existing row (first registration) → counters default to 0, never absent.
        var harness = new Harness(existing: null);

        var registration = new SessionRegistration
        {
            TenantId  = TenantId,
            SessionId = SessionId,
            StartedAt = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
            SerialNumber = "SN-1",
            Manufacturer = "Contoso",
            Model        = "Model-X",
            DeviceName   = "PC-1",
        };

        var ok = await harness.Sut.StoreSessionAsync(registration);

        Assert.True(ok);
        Assert.NotNull(harness.Written);
        Assert.Equal(0, harness.Written!.GetInt32("PlatformScriptCount"));
        Assert.Equal(0, harness.Written.GetInt32("RemediationScriptCount"));
        Assert.Equal(0, harness.Written.GetInt32("RebootCount"));
    }

    [Fact]
    public async Task StoreSessionAsync_selfDeployingProfile_isStickyTrue_acrossReregistration()
    {
        // Session 320b3bf7 kiosk marker: once observed true, a re-registration whose fresh
        // registry read failed (registration.IsSelfDeployingProfile=false) must NOT pin the
        // flag back to false — sticky-true OR, not plain preserve.
        var existing = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            ["Status"] = "InProgress",
            ["IsSelfDeployingProfile"] = true,
        };
        existing.ETag = new ETag("0xEXISTING");
        var harness = new Harness(existing);

        var registration = new SessionRegistration
        {
            TenantId = TenantId,
            SessionId = SessionId,
            StartedAt = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
            SerialNumber = "SN-1",
            Manufacturer = "Contoso",
            Model = "Model-X",
            DeviceName = "PC-1",
            IsSelfDeployingProfile = false, // fresh read failed / degraded
        };

        var ok = await harness.Sut.StoreSessionAsync(registration);

        Assert.True(ok);
        Assert.True(harness.Written!.GetBoolean("IsSelfDeployingProfile"));
    }

    [Fact]
    public async Task StoreSessionAsync_selfDeployingProfile_upgradesFalseToTrue_onReregistration()
    {
        // Inverse direction: first boot read false (policy cache not yet populated), a later
        // re-registration reads true — the OR upgrades the stored flag.
        var existing = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            ["Status"] = "InProgress",
            ["IsSelfDeployingProfile"] = false,
        };
        existing.ETag = new ETag("0xEXISTING");
        var harness = new Harness(existing);

        var registration = new SessionRegistration
        {
            TenantId = TenantId,
            SessionId = SessionId,
            StartedAt = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
            SerialNumber = "SN-1",
            Manufacturer = "Contoso",
            Model = "Model-X",
            DeviceName = "PC-1",
            IsSelfDeployingProfile = true,
        };

        var ok = await harness.Sut.StoreSessionAsync(registration);

        Assert.True(ok);
        Assert.True(harness.Written!.GetBoolean("IsSelfDeployingProfile"));
    }

    // ============================================================ Harness ====

    /// <summary>
    /// SDK-mock harness. Only the Sessions table client is configured; the Events query
    /// (earliest-timestamp probe) and the SessionsIndex upsert are both wrapped in swallow-all
    /// try/catch in the SUT, so leaving those table clients unconfigured (null) is harmless and
    /// keeps the test focused on the Sessions Replace payload.
    /// </summary>
    private sealed class Harness
    {
        public Mock<TableClient> Sessions { get; }
        public TableStorageService Sut { get; }
        public TableEntity? Written { get; private set; }

        public Harness(TableEntity? existing)
        {
            Sessions = new Mock<TableClient>();

            if (existing is not null)
            {
                Sessions.Setup(t => t.GetEntityAsync<TableEntity>(
                        TenantId, SessionId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Response.FromValue(existing, new Mock<Response>().Object));
            }
            else
            {
                Sessions.Setup(t => t.GetEntityAsync<TableEntity>(
                        TenantId, SessionId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new RequestFailedException(404, "ResourceNotFound"));
            }

            // Replace write (existing row) — capture the payload.
            Sessions.Setup(t => t.UpdateEntityAsync(
                    It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, _, _, _) =>
                {
                    Written = e;
                    return Task.FromResult(new Mock<Response>().Object);
                });

            // Add write (brand-new row, no ETag) — capture the payload.
            Sessions.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, CancellationToken>((e, _) =>
                {
                    Written = e;
                    return Task.FromResult(new Mock<Response>().Object);
                });

            var mockServiceClient = new Mock<TableServiceClient>();
            mockServiceClient.Setup(s => s.GetTableClient(Constants.TableNames.Sessions)).Returns(Sessions.Object);
            // Events / SessionsIndex intentionally unconfigured → null client → SUT swallows.

            Sut = new TableStorageService(mockServiceClient.Object, NullLogger<TableStorageService>.Instance);
        }
    }
}
