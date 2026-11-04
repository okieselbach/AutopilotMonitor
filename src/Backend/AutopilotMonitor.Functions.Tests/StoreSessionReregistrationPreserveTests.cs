using System;
using System.Collections.Generic;
using System.Linq;
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
            ["ConnectionType"]         = "Ethernet",
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

        var ok = await harness.Store(registration);

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
        // Merge-mode field (UpdateSessionConnectionTypeAsync) — must survive the Replace so the
        // value isn't lost in the window until the restarted agent re-emits network_interface_info.
        Assert.Equal("Ethernet", harness.Written.GetString("ConnectionType"));
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

        var ok = await harness.Store(registration);

        Assert.True(ok);
        Assert.NotNull(harness.Written);
        Assert.Equal(0, harness.Written!.GetInt32("PlatformScriptCount"));
        Assert.Equal(0, harness.Written.GetInt32("RemediationScriptCount"));
        Assert.Equal(0, harness.Written.GetInt32("RebootCount"));
        // No server-time tracker or twins before the first upload.
        foreach (var col in TableStorageService.ServerTimeTwinColumns.Append(TableStorageService.ServerTimeStateColumn))
            Assert.False(harness.Written.ContainsKey(col), col);
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

        var ok = await harness.Store(registration);

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

        var ok = await harness.Store(registration);

        Assert.True(ok);
        Assert.True(harness.Written!.GetBoolean("IsSelfDeployingProfile"));
    }

    [Fact]
    public async Task StoreSessionAsync_cloudPc_isStickyTrue_acrossReregistration()
    {
        // W365 marker mirrors the self-deploying sticky-true contract: once a boot observed
        // the Cloud PC markers, a later re-registration whose registry/service probe degraded
        // (registration.IsCloudPc=false) must NOT clear the fact.
        var existing = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            ["Status"] = "InProgress",
            ["IsCloudPc"] = true,
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
            IsCloudPc = false, // fresh marker read degraded
        };

        var ok = await harness.Store(registration);

        Assert.True(ok);
        Assert.True(harness.Written!.GetBoolean("IsCloudPc"));
    }

    [Fact]
    public async Task StoreSessionAsync_enrollmentType_isStickyV2_acrossReregistration()
    {
        // EnrollmentRegistryDetector defaults to "v1" whenever the AutopilotSettings key is
        // momentarily unreadable, so a re-registration after such a degraded read must NOT
        // downgrade an already-observed Device Preparation session — sticky-v2, same contract
        // as the sticky-true booleans.
        var existing = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            ["Status"] = "InProgress",
            ["EnrollmentType"] = "v2",
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
            EnrollmentType = "v1", // degraded registry read on restart
        };

        var ok = await harness.Store(registration);

        Assert.True(ok);
        Assert.Equal("v2", harness.Written!.GetString("EnrollmentType"));
    }

    [Fact]
    public async Task StoreSessionAsync_enrollmentType_upgradesV1ToV2_onReregistration()
    {
        // Inverse direction: first registration read "v1" (key not yet populated), a later
        // re-registration reads "v2" — the more informative value wins.
        var existing = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            ["Status"] = "InProgress",
            ["EnrollmentType"] = "v1",
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
            EnrollmentType = "v2",
        };

        var ok = await harness.Store(registration);

        Assert.True(ok);
        Assert.Equal("v2", harness.Written!.GetString("EnrollmentType"));
    }

    [Fact]
    public async Task StoreSessionAsync_cloudPc_upgradesFalseToTrue_onReregistration()
    {
        var existing = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            ["Status"] = "InProgress",
            ["IsCloudPc"] = false,
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
            IsCloudPc = true,
        };

        var ok = await harness.Store(registration);

        Assert.True(ok);
        Assert.True(harness.Written!.GetBoolean("IsCloudPc"));
    }

    // ============================================================ Harness ====

    /// <summary>
    /// SDK-mock harness. Only the Sessions table client is configured; the Events query
    /// (earliest-timestamp probe) and the SessionsIndex upsert are both wrapped in swallow-all
    /// try/catch in the SUT, so leaving those table clients unconfigured (null) is harmless and
    /// keeps the test focused on the Sessions Replace payload.
    /// </summary>
    // ── SESSION-OWNER-BINDING ────────────────────────────────────────────────

    [Fact]
    public async Task StoreSessionAsync_preserves_the_owner_when_no_owner_is_stamped()
    {
        var existing = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            ["Status"] = "InProgress",
            ["OwnerKind"] = "Cert",
            ["OwnerThumbprint"] = "AA11BB22",
            ["OwnerDeviceId"] = "0f4e1c2d-1111-4aaa-8bbb-000000000001",
            ["OwnerSerial"] = "SN-1",
            ["OwnerBoundAt"] = new DateTimeOffset(new DateTime(2026, 1, 1, 8, 0, 5, DateTimeKind.Utc)),
        };
        existing.ETag = new ETag("0xEXISTING");
        var harness = new Harness(existing);

        var ok = await harness.Store(new SessionRegistration
        {
            TenantId = TenantId, SessionId = SessionId, SerialNumber = "SN-1",
            StartedAt = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
        });

        Assert.True(ok);
        Assert.NotNull(harness.Written);
        Assert.Equal("Cert", harness.Written!["OwnerKind"]);
        Assert.Equal("AA11BB22", harness.Written["OwnerThumbprint"]);
        Assert.Equal("0f4e1c2d-1111-4aaa-8bbb-000000000001", harness.Written["OwnerDeviceId"]);
        Assert.Equal("SN-1", harness.Written["OwnerSerial"]);
        Assert.True(harness.Written.ContainsKey("OwnerBoundAt"));
    }

    [Fact]
    public async Task StoreSessionAsync_replaces_the_owner_when_one_is_stamped_and_clears_the_other_kind()
    {
        var existing = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            ["Status"] = "InProgress",
            ["OwnerKind"] = "Bootstrap",
            ["OwnerBootstrapCode"] = "ABC123",
            ["OwnerSerial"] = "SN-1",
            ["OwnerBoundAt"] = new DateTimeOffset(new DateTime(2026, 1, 1, 8, 0, 5, DateTimeKind.Utc)),
        };
        existing.ETag = new ETag("0xEXISTING");
        var harness = new Harness(existing);

        var boundAt = new DateTime(2026, 1, 1, 9, 0, 1, DateTimeKind.Utc);
        var ok = await harness.Store(new SessionRegistration
        {
            TenantId = TenantId, SessionId = SessionId, SerialNumber = "SN-1",
            StartedAt = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
        }, new SessionOwner { Kind = "Cert", Thumbprint = "CC33DD44", DeviceId = "dev-1", Serial = "SN-1", BoundAt = boundAt });

        Assert.True(ok);
        Assert.Equal("Cert", harness.Written!["OwnerKind"]);
        Assert.Equal("CC33DD44", harness.Written["OwnerThumbprint"]);
        Assert.False(harness.Written.ContainsKey("OwnerBootstrapCode"));
        Assert.Equal(new DateTimeOffset(boundAt), harness.Written.GetDateTimeOffset("OwnerBoundAt"));
    }

    [Fact]
    public async Task StoreSessionAsync_carries_the_server_time_tracker_and_twins_from_the_fresh_read()
    {
        // The agent's first upload often races its registration (WhiteGlove Part 2: the
        // agent_started batch). Ingest absorbed it between the initial read and the CAS read —
        // the Replace must carry the fresh tracker and twins, not the initial (empty) ones.
        var startedAt = new DateTimeOffset(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc));
        var initial = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = startedAt,
            ["Status"] = "Pending",
            ["IsPreProvisioned"] = true,
        };
        initial.ETag = new ETag("0xINITIAL");
        var fresh = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = startedAt,
            ["Status"] = "Pending",
            ["IsPreProvisioned"] = true,
            ["ServerTimeState"] = "{\"version\":1,\"runs\":3}",
            ["StartedAtServer"] = startedAt.AddHours(-9),
            ["ResumedAtServer"] = startedAt.AddDays(2),
        };
        fresh.ETag = new ETag("0xFRESH");
        var harness = new Harness(initial);
        harness.Sessions.SetupSequence(t => t.GetEntityAsync<TableEntity>(
                TenantId, SessionId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(initial, new Mock<Response>().Object))
            .ReturnsAsync(Response.FromValue(fresh, new Mock<Response>().Object));

        var ok = await harness.Store(new SessionRegistration
        {
            TenantId = TenantId, SessionId = SessionId, SerialNumber = "SN-1",
            StartedAt = new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc),
        });

        Assert.True(ok);
        Assert.Equal("InProgress", harness.Written!.GetString("Status")); // WhiteGlove Part 2 resumes
        Assert.Equal("{\"version\":1,\"runs\":3}", harness.Written.GetString("ServerTimeState"));
        Assert.Equal(startedAt.AddHours(-9), harness.Written.GetDateTimeOffset("StartedAtServer"));
        Assert.Equal(startedAt.AddDays(2), harness.Written.GetDateTimeOffset("ResumedAtServer"));
        Assert.False(harness.Written.ContainsKey("CompletedAtServer"));
    }

    [Fact]
    public async Task StoreSessionAsync_fresh_row_carries_the_stamped_owner()
    {
        var harness = new Harness(existing: null);
        var ok = await harness.Store(new SessionRegistration
        {
            TenantId = TenantId, SessionId = SessionId, SerialNumber = "SN-1",
            StartedAt = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
        }, new SessionOwner { Kind = "Bootstrap", BootstrapCode = "ABC123", Serial = "SN-1", BoundAt = DateTime.UtcNow });

        Assert.True(ok);
        Assert.Equal("Bootstrap", harness.Written!["OwnerKind"]);
        Assert.Equal("ABC123", harness.Written["OwnerBootstrapCode"]);
        Assert.False(harness.Written.ContainsKey("OwnerThumbprint"));
    }

    // ── Carry-over of the columns the registration does not own ─────────────

    [Fact]
    public async Task StoreSessionAsync_carries_every_declared_column_it_does_not_own()
    {
        // Driven by the manifest: a column added to SessionIndexFieldManifest later is carried
        // through an agent restart without anyone touching StoreSessionAsync. Merge-written columns
        // such as ImeAgentVersion, LastIngestAt, ExcessiveEventsAlerted, Block* and the pending
        // server actions used to be dropped by the Replace.
        var existing = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            ["Status"] = "InProgress",
        };
        foreach (var column in TableStorageService.CarriedColumns)
            existing[column] = $"carried:{column}";
        existing.ETag = new ETag("0xEXISTING");
        var harness = new Harness(existing);

        var ok = await harness.Store(new SessionRegistration
        {
            TenantId = TenantId, SessionId = SessionId, SerialNumber = "SN-1",
            StartedAt = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
        });

        Assert.True(ok);
        Assert.Contains("ImeAgentVersion", TableStorageService.CarriedColumns);
        Assert.Contains("PendingActionsJson", TableStorageService.CarriedColumns);
        foreach (var column in TableStorageService.CarriedColumns)
            Assert.Equal($"carried:{column}", harness.Written![column]);
    }

    [Fact]
    public void RegistrationOwnedColumns_name_only_declared_columns()
    {
        // A typo in the owned list would silently turn a column into a carried one.
        var declared = SessionIndexFieldManifest.All
            .Concat(SessionIndexFieldManifest.PrimaryOnly)
            .Append("IndexRowKey") // Sessions-only bookkeeping, deliberately outside the index manifest
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(TableStorageService.RegistrationOwnedColumns, column => Assert.Contains(column, declared));
        Assert.Equal(
            TableStorageService.RegistrationOwnedColumns.Length,
            TableStorageService.RegistrationOwnedColumns.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task StoreSessionAsync_cas_retry_carries_the_values_of_its_own_fresh_read()
    {
        var startedAt = new DateTimeOffset(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc));
        var initial = new TableEntity(TenantId, SessionId) { ["StartedAt"] = startedAt, ["Status"] = "InProgress" };
        initial.ETag = new ETag("0xINITIAL");
        var firstFresh = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = startedAt, ["Status"] = "InProgress",
            ["ImeAgentVersion"] = "1.0", ["LastIngestAt"] = startedAt.AddMinutes(5),
        };
        firstFresh.ETag = new ETag("0xFRESH1");
        var secondFresh = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = startedAt, ["Status"] = "InProgress",
            ["ImeAgentVersion"] = "2.0",
        };
        secondFresh.ETag = new ETag("0xFRESH2");

        var harness = new Harness(initial);
        harness.Sessions.SetupSequence(t => t.GetEntityAsync<TableEntity>(
                TenantId, SessionId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(initial, new Mock<Response>().Object))
            .ReturnsAsync(Response.FromValue(firstFresh, new Mock<Response>().Object))
            .ReturnsAsync(Response.FromValue(secondFresh, new Mock<Response>().Object));

        var attempts = 0;
        TableEntity? written = null;
        harness.Sessions.Setup(t => t.UpdateEntityAsync(
                It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, _, _, _) =>
            {
                if (++attempts == 1)
                    return Task.FromException<Response>(new RequestFailedException(412, "ConditionNotMet"));
                written = e;
                return Task.FromResult(new Mock<Response>().Object);
            });

        var ok = await harness.Store(new SessionRegistration
        {
            TenantId = TenantId, SessionId = SessionId, SerialNumber = "SN-1",
            StartedAt = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
        });

        Assert.True(ok);
        Assert.Equal(2, attempts);
        Assert.Equal("2.0", written!.GetString("ImeAgentVersion"));
        // Carried by the failed attempt, gone from the second fresh read: not carried again.
        Assert.False(written.ContainsKey("LastIngestAt"));
    }

    [Fact]
    public async Task StoreSessionAsync_does_not_carry_the_terminal_tuple_of_a_non_terminal_row()
    {
        // The terminal tuple is owned: it is re-stamped only while the fresh status is terminal.
        // A WhiteGlove Part 1 row resuming into Part 2 keeps the old behaviour of dropping it.
        var existing = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            ["Status"] = "Pending",
            ["CompletionSource"] = "whiteglove_part1",
            ["FailureSnapshotJson"] = "{}",
        };
        existing.ETag = new ETag("0xEXISTING");
        var harness = new Harness(existing);

        var ok = await harness.Store(new SessionRegistration
        {
            TenantId = TenantId, SessionId = SessionId, SerialNumber = "SN-1",
            StartedAt = new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc),
        });

        Assert.True(ok);
        Assert.Equal("InProgress", harness.Written!.GetString("Status"));
        Assert.False(harness.Written.ContainsKey("CompletionSource"));
        Assert.False(harness.Written.ContainsKey("FailureSnapshotJson"));
    }

    // ── Server facts: validator and Autopilot registration ──────────────────

    private static SessionRegistration Reregistration() => new()
    {
        TenantId = TenantId, SessionId = SessionId, SerialNumber = "SN-1",
        StartedAt = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
    };

    private static TableEntity ExistingRow(params (string Column, object Value)[] columns)
    {
        var row = new TableEntity(TenantId, SessionId)
        {
            ["StartedAt"] = new DateTimeOffset(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            ["Status"] = "InProgress",
        };
        foreach (var (column, value) in columns)
            row[column] = value;
        row.ETag = new ETag("0xEXISTING");
        return row;
    }

    [Fact]
    public async Task StoreSessionAsync_writes_the_autopilot_registration_of_a_new_session()
    {
        var harness = new Harness(existing: null);

        var ok = await harness.Store(Reregistration(), facts: new SessionServerFacts
        {
            ValidatedBy = ValidatorType.AutopilotV1,
            AutopilotRegistration = new AutopilotRegistration
            {
                DeviceId = "ap-1", GroupTag = "TEST", PurchaseOrderId = "PO-7", DisplayName = "LAB-01", UserAssigned = true,
            },
        });

        Assert.True(ok);
        Assert.Equal("AutopilotV1", harness.Written!.GetString("ValidatedBy"));
        Assert.Equal("ap-1", harness.Written.GetString("AutopilotDeviceId"));
        Assert.Equal("TEST", harness.Written.GetString("AutopilotGroupTag"));
        Assert.Equal("PO-7", harness.Written.GetString("AutopilotPurchaseOrderId"));
        Assert.Equal("LAB-01", harness.Written.GetString("AutopilotDisplayName"));
        Assert.True(harness.Written.GetBoolean("AutopilotUserAssigned"));
    }

    [Fact]
    public async Task StoreSessionAsync_a_fresh_autopilot_registration_replaces_the_stored_one_whole()
    {
        // The group tag was removed in Intune between two agent starts: the column clears.
        var harness = new Harness(ExistingRow(
            ("AutopilotDeviceId", "ap-1"), ("AutopilotGroupTag", "OLD"), ("AutopilotPurchaseOrderId", "PO-1"),
            ("AutopilotDisplayName", "LAB-01"), ("AutopilotUserAssigned", true)));

        var ok = await harness.Store(Reregistration(), facts: new SessionServerFacts
        {
            ValidatedBy = ValidatorType.AutopilotV1,
            AutopilotRegistration = new AutopilotRegistration { DeviceId = "ap-1", PurchaseOrderId = "PO-2" },
        });

        Assert.True(ok);
        Assert.False(harness.Written!.ContainsKey("AutopilotGroupTag"));
        Assert.False(harness.Written.ContainsKey("AutopilotDisplayName"));
        Assert.Equal("PO-2", harness.Written.GetString("AutopilotPurchaseOrderId"));
        Assert.False(harness.Written.GetBoolean("AutopilotUserAssigned"));
    }

    [Fact]
    public async Task StoreSessionAsync_without_an_autopilot_registration_keeps_the_stored_one_and_the_validator()
    {
        // A re-registration another path admitted (or with validation switched off) proves nothing
        // about the Autopilot registration: the stored values and the recorded validator stay.
        var harness = new Harness(ExistingRow(
            ("ValidatedBy", "AutopilotV1"), ("AutopilotDeviceId", "ap-1"), ("AutopilotGroupTag", "TEST"),
            ("AutopilotUserAssigned", false)));

        var ok = await harness.Store(Reregistration(), facts: new SessionServerFacts());

        Assert.True(ok);
        Assert.Equal("AutopilotV1", harness.Written!.GetString("ValidatedBy"));
        Assert.Equal("ap-1", harness.Written.GetString("AutopilotDeviceId"));
        Assert.Equal("TEST", harness.Written.GetString("AutopilotGroupTag"));
        Assert.False(harness.Written.GetBoolean("AutopilotUserAssigned"));
    }

    private sealed class Harness
    {
        public Mock<TableClient> Sessions { get; }
        public Mock<TableClient> Lookup { get; } = new();
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

            // Ownership claim (first-writer-wins lookup) — Add succeeds: this tenant owns the id.
            Lookup.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Mock<Response>().Object);

            var mockServiceClient = new Mock<TableServiceClient>();
            mockServiceClient.Setup(s => s.GetTableClient(Constants.TableNames.Sessions)).Returns(Sessions.Object);
            mockServiceClient.Setup(s => s.GetTableClient(Constants.TableNames.SessionTenantLookup)).Returns(Lookup.Object);
            // Events / SessionsIndex intentionally unconfigured → null client → SUT swallows.

            Sut = new TableStorageService(mockServiceClient.Object, NullLogger<TableStorageService>.Instance);
        }

        /// <summary>Stores with no server facts unless a test hands them in.</summary>
        public Task<bool> Store(SessionRegistration registration, SessionOwner? owner = null, SessionServerFacts? facts = null)
            => Sut.StoreSessionAsync(registration, facts ?? new SessionServerFacts(), owner);
    }
}
