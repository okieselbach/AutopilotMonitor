using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Worker-level tests for plan §5 PR4. Wraps the BackgroundService poll-loop in a
/// CancellationTokenSource and exercises the four explicit behaviours:
/// kill-switch entry guard, poison after max-dequeue (+ audit), malformed-envelope drop,
/// heartbeat extends visibility on long handler runs.
/// </summary>
public class SessionDeletionWorkerTests
{
    private const string TenantId   = "11111111-1111-1111-1111-111111111111";
    private const string SessionId  = "22222222-2222-2222-2222-222222222222";
    private const string ManifestId = "0123456789ABCDEF_FEDCBA9876543210";

    [Fact]
    public async Task Worker_does_not_receive_messages_while_kill_switch_active()
    {
        var harness = new Harness();
        harness.SetKillSwitch(true);

        await harness.RunForAsync(TimeSpan.FromMilliseconds(500));

        harness.MainQueue.Verify(q => q.ReceiveMessagesAsync(
            It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.HandlerMock.Verify(h => h.HandleAsync(
            It.IsAny<SessionDeletionEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Worker_moves_message_to_poison_after_max_dequeue_and_emits_ops_event()
    {
        var harness = new Harness();
        var envelope = new SessionDeletionEnvelope
        {
            TenantId = TenantId, SessionId = SessionId, ManifestId = ManifestId,
            Reason = "admin_delete", EnqueuedAt = DateTime.UtcNow,
        };
        harness.EnqueueMessage(JsonConvert.SerializeObject(envelope), dequeueCount: QueuePollingWorkerBase.DefaultMaxDequeueCount + 1);

        await harness.RunUntilAsync(() => harness.OpsEventRecorded());

        // Poison queue received the body and main queue deleted the original.
        harness.PoisonQueue.Verify(q => q.SendMessageAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        harness.MainQueue.Verify(q => q.DeleteMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        // PR-B audit consolidation: deletion_poisoned audit moved to SessionDeletionPoisoned
        // OpsEvent (operator-bound, Telegram-routable). Tenant-audit row is no longer written.
        var poisoned = Assert.Single(harness.OpsEventCalls, e => e.EventType == "SessionDeletionPoisoned");
        Assert.Equal(TenantId, poisoned.TenantId);
        Assert.Equal(SessionId, poisoned.SessionId);
        Assert.Equal(ManifestId, poisoned.ManifestId);
        Assert.DoesNotContain(harness.AuditCalls, a => a.Action == "deletion_poisoned");
        // Codex F4: poison OpsEvent carries the handler's last-failure breadcrumb from the
        // DeletionProgress blob (harness seeds a verification-failure scenario by default).
        Assert.Contains("verification_residuals", poisoned.RawDetails);
        Assert.Contains("residual row(s)", poisoned.RawDetails);
        // Codex F2 round-3: OpsEvent carries the verifier's OBSERVED residual count, not a
        // hypothetical "true" total — CascadeVerificationService short-circuits at the first
        // failing table and caps each table's sample at MaxResidualSampleSize. The degenerate
        // residualSampleSize key was dropped because it always equalled observedResidualCount.
        Assert.Contains("\"observedResidualCount\":2", poisoned.RawDetails);
        Assert.DoesNotContain("\"residualSampleSize\":", poisoned.RawDetails);
        Assert.DoesNotContain("\"residualCount\":", poisoned.RawDetails); // old, misleading key
    }

    [Fact]
    public async Task Worker_OpsEvent_carries_observed_count_at_verifier_cap_for_large_failures()
    {
        // Codex F2 round-3: when the real residual mountain is bigger than the verifier cap,
        // verification.Residuals.Count = MaxResidualSampleSize (50) and that's the only number
        // we have. The worker forwards exactly that. UI surface treats "==cap" as a "≥" lower
        // bound; the OpsEvent itself stays honest and just reports what was observed.
        var harness = new Harness();
        var envelope = new SessionDeletionEnvelope
        {
            TenantId = TenantId, SessionId = SessionId, ManifestId = ManifestId,
            Reason = "admin_delete", EnqueuedAt = DateTime.UtcNow,
        };
        harness.EnqueueMessage(JsonConvert.SerializeObject(envelope), dequeueCount: QueuePollingWorkerBase.DefaultMaxDequeueCount + 1);

        const int capObservation = 50;
        var sample = string.Join(",", Enumerable.Range(0, capObservation)
            .Select(i => $"{{\"table\":\"Events\",\"pk\":\"t\",\"rk\":\"r{i}\"}}"));
        harness.BlobMock.Setup(b => b.DownloadDeletionProgressAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((AutopilotMonitor.Shared.Models.Deletion.DeletionProgress, string))(
                new AutopilotMonitor.Shared.Models.Deletion.DeletionProgress
                {
                    LastFailureType = "verification_residuals",
                    LastFailureMessage = $"{capObservation} observed residual row(s); refusing to tombstone",
                    LastObservedResidualCount = capObservation,
                    LastResidualSampleJson = $"[{sample}]",
                },
                "etag-mock"));

        await harness.RunUntilAsync(() => harness.OpsEventRecorded());

        var poisoned = Assert.Single(harness.OpsEventCalls, e => e.EventType == "SessionDeletionPoisoned");
        Assert.Contains($"\"observedResidualCount\":{capObservation}", poisoned.RawDetails);
        // OpsEvents table truncates Details at 4096 chars (TableOpsEventRepository.cs); the
        // preview cap (OpsEventResidualSamplePreviewSize=5) keeps us safely under that even
        // with a 50-entry progress-blob sample.
        Assert.True(poisoned.RawDetails.Length < 4096,
            $"OpsEvent Details exceeded 4096 chars ({poisoned.RawDetails.Length}); the table repository will truncate mid-JSON.");
        // Preview JSON has at most OpsEventResidualSamplePreviewSize entries.
        var match = System.Text.RegularExpressions.Regex.Match(
            poisoned.RawDetails,
            "\"residualSamplePreviewJson\":\"(?<json>(?:[^\"\\\\]|\\\\.)*)\"");
        Assert.True(match.Success, "Expected residualSamplePreviewJson key in OpsEvent details.");
        var previewJson = System.Text.RegularExpressions.Regex.Unescape(match.Groups["json"].Value);
        using var previewDoc = System.Text.Json.JsonDocument.Parse(previewJson);
        Assert.InRange(previewDoc.RootElement.GetArrayLength(), 1,
            AutopilotMonitor.Shared.Models.Deletion.DeletionProgressConstants.OpsEventResidualSamplePreviewSize);
    }

    [Fact]
    public async Task Worker_OpsEvent_falls_back_to_sample_length_when_observed_count_missing_pre_followup_blob()
    {
        // Back-compat: a progress blob written before LastObservedResidualCount existed (this
        // field was added in a Codex follow-up to PR-B) still produces a useful OpsEvent — the
        // worker derives the number from the sample-array length so observers don't see a null
        // observedResidualCount when reading historical poisoned cascades.
        var harness = new Harness();
        var envelope = new SessionDeletionEnvelope
        {
            TenantId = TenantId, SessionId = SessionId, ManifestId = ManifestId,
            Reason = "admin_delete", EnqueuedAt = DateTime.UtcNow,
        };
        harness.EnqueueMessage(JsonConvert.SerializeObject(envelope), dequeueCount: QueuePollingWorkerBase.DefaultMaxDequeueCount + 1);

        harness.BlobMock.Setup(b => b.DownloadDeletionProgressAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((AutopilotMonitor.Shared.Models.Deletion.DeletionProgress, string))(
                new AutopilotMonitor.Shared.Models.Deletion.DeletionProgress
                {
                    LastFailureType = "verification_residuals",
                    LastFailureMessage = "observed residual row(s); refusing to tombstone",
                    LastObservedResidualCount = null, // pre-followup blob — field did not exist
                    LastResidualSampleJson = "[{\"table\":\"Events\",\"pk\":\"t\",\"rk\":\"r1\"},{\"table\":\"Events\",\"pk\":\"t\",\"rk\":\"r2\"},{\"table\":\"Events\",\"pk\":\"t\",\"rk\":\"r3\"}]",
                },
                "etag-mock"));

        await harness.RunUntilAsync(() => harness.OpsEventRecorded());

        var poisoned = Assert.Single(harness.OpsEventCalls, e => e.EventType == "SessionDeletionPoisoned");
        Assert.Contains("\"observedResidualCount\":3", poisoned.RawDetails);
    }

    [Fact]
    public void ShrinkResidualSampleForOpsEvent_caps_array_at_preview_size()
    {
        // Unit-level pin on the helper so the cap can't silently drift if someone changes the
        // constant or the serialization plumbing.
        var full = string.Join(",", Enumerable.Range(0, 50)
            .Select(i => $"{{\"table\":\"Events\",\"pk\":\"tenant\",\"rk\":\"row-{i}\"}}"));
        var shrunk = SessionDeletionWorker.ShrinkResidualSampleForOpsEvent($"[{full}]");
        Assert.NotNull(shrunk);
        using var doc = System.Text.Json.JsonDocument.Parse(shrunk!);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(
            AutopilotMonitor.Shared.Models.Deletion.DeletionProgressConstants.OpsEventResidualSamplePreviewSize,
            doc.RootElement.GetArrayLength());
    }

    [Fact]
    public void ShrinkResidualSampleForOpsEvent_returns_null_for_malformed_input()
    {
        Assert.Null(SessionDeletionWorker.ShrinkResidualSampleForOpsEvent(null));
        Assert.Null(SessionDeletionWorker.ShrinkResidualSampleForOpsEvent(""));
        Assert.Null(SessionDeletionWorker.ShrinkResidualSampleForOpsEvent("not-json"));
        Assert.Null(SessionDeletionWorker.ShrinkResidualSampleForOpsEvent("{\"not\":\"an array\"}"));
    }

    [Fact]
    public void ShrinkResidualSampleForOpsEvent_trims_per_field_with_ellipsis_marker()
    {
        // Codex follow-up: a single entry with a 200-char composite PK could on its own consume
        // most of the OpsEvent Details budget. The helper trims each field to
        // OpsEventResidualKeyMaxChars and appends `…` so the truncation is operator-visible.
        var maxKeyChars = AutopilotMonitor.Shared.Models.Deletion.DeletionProgressConstants.OpsEventResidualKeyMaxChars;
        var longPk = new string('p', maxKeyChars * 3);
        var longRk = new string('r', maxKeyChars * 3);
        var input = $"[{{\"table\":\"Events\",\"pk\":\"{longPk}\",\"rk\":\"{longRk}\"}}]";

        var shrunk = SessionDeletionWorker.ShrinkResidualSampleForOpsEvent(input);
        Assert.NotNull(shrunk);

        using var doc = System.Text.Json.JsonDocument.Parse(shrunk!);
        var first = doc.RootElement[0];
        var pk = first.GetProperty("pk").GetString()!;
        var rk = first.GetProperty("rk").GetString()!;
        Assert.Equal(maxKeyChars, pk.Length);
        Assert.Equal(maxKeyChars, rk.Length);
        Assert.EndsWith("…", pk);
        Assert.EndsWith("…", rk);
        Assert.StartsWith("ppp", pk);
        Assert.StartsWith("rrr", rk);
    }

    [Fact]
    public void ShrinkResidualSampleForOpsEvent_short_fields_pass_through_unchanged()
    {
        // Don't paint truncation markers on values that fit naturally.
        var input = "[{\"table\":\"Events\",\"pk\":\"short-pk\",\"rk\":\"short-rk\"}]";
        var shrunk = SessionDeletionWorker.ShrinkResidualSampleForOpsEvent(input);
        Assert.NotNull(shrunk);
        using var doc = System.Text.Json.JsonDocument.Parse(shrunk!);
        var first = doc.RootElement[0];
        Assert.Equal("short-pk", first.GetProperty("pk").GetString());
        Assert.Equal("short-rk", first.GetProperty("rk").GetString());
    }

    [Fact]
    public void ShrinkResidualSampleForOpsEvent_drops_trailing_entries_when_over_total_budget()
    {
        // Even with per-field trimming, max-length entries can collectively exceed the preview
        // budget (~1200 chars). The helper drops trailing entries one at a time until the JSON
        // fits — operators always see SOMETHING parseable, the full sample stays in the blob.
        var maxKeyChars = AutopilotMonitor.Shared.Models.Deletion.DeletionProgressConstants.OpsEventResidualKeyMaxChars;
        var budget = AutopilotMonitor.Shared.Models.Deletion.DeletionProgressConstants.OpsEventResidualPreviewBudgetChars;
        var maxEntries = AutopilotMonitor.Shared.Models.Deletion.DeletionProgressConstants.OpsEventResidualSamplePreviewSize;
        // Each entry takes ~(20 table + 96 pk + 96 rk + 30 JSON shape) ≈ 240 chars; 5 entries
        // would land at ~1200, right at the budget. Make table/pk/rk all max-length so we land
        // slightly OVER and force a drop.
        var longTable = new string('T', maxKeyChars);
        var longPk = new string('p', maxKeyChars);
        var longRk = new string('r', maxKeyChars);
        var entry = $"{{\"table\":\"{longTable}\",\"pk\":\"{longPk}\",\"rk\":\"{longRk}\"}}";
        var input = "[" + string.Join(",", Enumerable.Range(0, maxEntries).Select(_ => entry)) + "]";

        var shrunk = SessionDeletionWorker.ShrinkResidualSampleForOpsEvent(input);
        Assert.NotNull(shrunk);
        Assert.True(shrunk!.Length <= budget,
            $"Preview exceeded total budget: {shrunk.Length} > {budget}.");

        using var doc = System.Text.Json.JsonDocument.Parse(shrunk!);
        var kept = doc.RootElement.GetArrayLength();
        // We must have kept at least one entry (a single entry is small enough to fit), and
        // dropped at least one (otherwise the budget wasn't actually enforced).
        Assert.InRange(kept, 1, maxEntries - 1);
    }

    [Fact]
    public async Task Worker_OpsEvent_stays_under_4096_chars_with_worst_case_long_keys()
    {
        // Codex end-to-end pin: the full pipeline (handler → progress blob → worker shrink →
        // OpsEvent JSON envelope → table-row truncate) must NEVER produce a Details string > 4096
        // chars, because TableOpsEventRepository hard-truncates at that boundary and would corrupt
        // the JSON mid-string. We simulate the worst case: max-length PK + RK on every sample
        // entry, plus a max-length failureMessage.
        var harness = new Harness();
        var envelope = new SessionDeletionEnvelope
        {
            TenantId = TenantId, SessionId = SessionId, ManifestId = ManifestId,
            Reason = "admin_delete", EnqueuedAt = DateTime.UtcNow,
        };
        harness.EnqueueMessage(JsonConvert.SerializeObject(envelope), dequeueCount: QueuePollingWorkerBase.DefaultMaxDequeueCount + 1);

        // 50 sample entries × ~200-char composite keys = ~10 KB blob; the worker must defend.
        const int progressSampleSize = 50;
        const int hugeKeyChars = 240;
        var hugeTable = new string('T', hugeKeyChars);
        var hugePk = new string('p', hugeKeyChars);
        var hugeRk = new string('r', hugeKeyChars);
        var sample = string.Join(",", Enumerable.Range(0, progressSampleSize)
            .Select(_ => $"{{\"table\":\"{hugeTable}\",\"pk\":\"{hugePk}\",\"rk\":\"{hugeRk}\"}}"));
        // Max-length failure message too.
        var longMessage = new string('m', 1024);

        harness.BlobMock.Setup(b => b.DownloadDeletionProgressAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((AutopilotMonitor.Shared.Models.Deletion.DeletionProgress, string))(
                new AutopilotMonitor.Shared.Models.Deletion.DeletionProgress
                {
                    LastFailureType = "verification_residuals",
                    LastFailureMessage = longMessage,
                    LastObservedResidualCount = progressSampleSize,
                    LastResidualSampleJson = $"[{sample}]",
                },
                "etag-mock"));

        await harness.RunUntilAsync(() => harness.OpsEventRecorded());

        var poisoned = Assert.Single(harness.OpsEventCalls, e => e.EventType == "SessionDeletionPoisoned");
        Assert.True(poisoned.RawDetails.Length < 4096,
            $"OpsEvent Details exceeded 4096 chars ({poisoned.RawDetails.Length}); TableOpsEventRepository would truncate mid-JSON.");

        // Sanity check that the preview is still parseable as JSON after the trim+budget pass.
        var match = System.Text.RegularExpressions.Regex.Match(
            poisoned.RawDetails,
            "\"residualSamplePreviewJson\":\"(?<json>(?:[^\"\\\\]|\\\\.)*)\"");
        Assert.True(match.Success, "Expected residualSamplePreviewJson key in OpsEvent details.");
        var previewJson = System.Text.RegularExpressions.Regex.Unescape(match.Groups["json"].Value);
        using var previewDoc = System.Text.Json.JsonDocument.Parse(previewJson);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, previewDoc.RootElement.ValueKind);
    }

    [Fact]
    public async Task Worker_transitions_state_to_Poisoned_before_sending_to_poison_queue()
    {
        // PR4b E1 fix: MoveToPoisonAsync must CAS Sessions.DeletionState: Running → Poisoned
        // BEFORE sending to the poison queue. Without this transition, the restore endpoint
        // (PR4b) cannot dispatch into partial-restore mode (it keys off DeletionState=Poisoned).
        var harness = new Harness();
        var envelope = new SessionDeletionEnvelope
        {
            TenantId = TenantId, SessionId = SessionId, ManifestId = ManifestId,
            Reason = "admin_delete", EnqueuedAt = DateTime.UtcNow,
        };
        harness.EnqueueMessage(JsonConvert.SerializeObject(envelope), dequeueCount: QueuePollingWorkerBase.DefaultMaxDequeueCount + 1);

        // Track call order: the CAS must come BEFORE the poison-queue send.
        var callOrder = new List<string>();
        harness.StorageMock.Setup(s => s.CasSetSessionDeletionStateAsync(
                TenantId, SessionId,
                SessionDeletionState.Running, SessionDeletionState.Poisoned,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("cas-running-to-poisoned"))
            .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
            {
                Outcome = TableStorageService.SessionDeletionStateCasOutcome.Updated,
                CurrentState = SessionDeletionState.Poisoned,
            });
        harness.PoisonQueue.Setup(q => q.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("poison-queue-send"))
            .Returns<string, CancellationToken>((body, _) =>
            {
                var receipt = QueuesModelFactory.SendReceipt("poison-msg", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(7), "poison-pop", DateTimeOffset.UtcNow);
                return Task.FromResult(Response.FromValue(receipt, new Mock<Response>().Object));
            });

        await harness.RunUntilAsync(() => harness.OpsEventRecorded());

        var casIdx = callOrder.IndexOf("cas-running-to-poisoned");
        var poisonIdx = callOrder.IndexOf("poison-queue-send");
        Assert.True(casIdx >= 0, "CAS Running→Poisoned was not issued");
        Assert.True(poisonIdx >= 0, "Poison-queue send was not issued");
        Assert.True(casIdx < poisonIdx,
            "CAS Running→Poisoned must precede the poison-queue send so the restore endpoint can dispatch.");
    }

    [Fact]
    public async Task Worker_falls_back_to_Queued_to_Poisoned_when_Running_CAS_misses()
    {
        // Rare case: cascade poisoned before the worker ever successfully transitioned Queued→Running
        // (handler always threw at the state-acquire step). In that case Running→Poisoned hits
        // WrongState and the fallback Queued→Poisoned must succeed.
        var harness = new Harness();
        var envelope = new SessionDeletionEnvelope
        {
            TenantId = TenantId, SessionId = SessionId, ManifestId = ManifestId,
            Reason = "admin_delete", EnqueuedAt = DateTime.UtcNow,
        };
        harness.EnqueueMessage(JsonConvert.SerializeObject(envelope), dequeueCount: QueuePollingWorkerBase.DefaultMaxDequeueCount + 1);

        // First attempt: Running→Poisoned WrongState (current=Queued).
        harness.StorageMock.Setup(s => s.CasSetSessionDeletionStateAsync(
                TenantId, SessionId,
                SessionDeletionState.Running, SessionDeletionState.Poisoned,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
            {
                Outcome = TableStorageService.SessionDeletionStateCasOutcome.WrongState,
                CurrentState = SessionDeletionState.Queued,
            });
        // Fallback attempt: Queued→Poisoned Updated.
        harness.StorageMock.Setup(s => s.CasSetSessionDeletionStateAsync(
                TenantId, SessionId,
                SessionDeletionState.Queued, SessionDeletionState.Poisoned,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
            {
                Outcome = TableStorageService.SessionDeletionStateCasOutcome.Updated,
                CurrentState = SessionDeletionState.Poisoned,
            });

        await harness.RunUntilAsync(() => harness.OpsEventRecorded());

        // Both CAS attempts fired, and the poison-queue send still completed.
        harness.StorageMock.Verify(s => s.CasSetSessionDeletionStateAsync(
            TenantId, SessionId,
            SessionDeletionState.Running, SessionDeletionState.Poisoned,
            It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.StorageMock.Verify(s => s.CasSetSessionDeletionStateAsync(
            TenantId, SessionId,
            SessionDeletionState.Queued, SessionDeletionState.Poisoned,
            It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.PoisonQueue.Verify(q => q.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Worker_still_poisons_message_when_state_CAS_persistently_fails()
    {
        // Best-effort contract: if both CAS attempts fail (concurrent writer chaos), the
        // poison-queue move + audit must STILL complete so the operator has an observable
        // trail. Restore endpoint may reject with 409 "active_cascade" until the operator
        // manually intervenes — but the cascade message itself is off the main queue.
        var harness = new Harness();
        var envelope = new SessionDeletionEnvelope
        {
            TenantId = TenantId, SessionId = SessionId, ManifestId = ManifestId,
            Reason = "admin_delete", EnqueuedAt = DateTime.UtcNow,
        };
        harness.EnqueueMessage(JsonConvert.SerializeObject(envelope), dequeueCount: QueuePollingWorkerBase.DefaultMaxDequeueCount + 1);

        harness.StorageMock.Setup(s => s.CasSetSessionDeletionStateAsync(
                TenantId, SessionId,
                It.IsAny<string>(), SessionDeletionState.Poisoned,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
            {
                Outcome = TableStorageService.SessionDeletionStateCasOutcome.WrongState,
                CurrentState = "Unknown",
            });

        await harness.RunUntilAsync(() => harness.OpsEventRecorded());

        harness.PoisonQueue.Verify(q => q.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        Assert.Contains(harness.OpsEventCalls, e => e.EventType == "SessionDeletionPoisoned");
    }

    [Fact]
    public async Task Worker_drops_malformed_envelope_without_invoking_handler()
    {
        var harness = new Harness();
        harness.EnqueueMessage("{ \"this is not valid JSON for an envelope: garbage", dequeueCount: 1);

        await harness.RunUntilAsync(() => harness.MainQueueDeleted());

        harness.HandlerMock.Verify(h => h.HandleAsync(
            It.IsAny<SessionDeletionEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Malformed messages are removed from the main queue (otherwise they'd loop forever).
        harness.MainQueue.Verify(q => q.DeleteMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        // Poison queue should NOT receive a malformed body — those are dropped, not poisoned.
        harness.PoisonQueue.Verify(q => q.SendMessageAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Worker_heartbeat_extends_visibility_while_handler_runs()
    {
        var harness = new Harness(heartbeatInterval: TimeSpan.FromMilliseconds(50));
        var envelope = new SessionDeletionEnvelope
        {
            TenantId = TenantId, SessionId = SessionId, ManifestId = ManifestId,
            Reason = "admin_delete", EnqueuedAt = DateTime.UtcNow,
        };
        harness.EnqueueMessage(JsonConvert.SerializeObject(envelope), dequeueCount: 1);

        // Handler hangs for 350ms so the heartbeat task ticks at least 5×.
        harness.HandlerMock.Setup(h => h.HandleAsync(
                It.IsAny<SessionDeletionEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns(async (SessionDeletionEnvelope _, CancellationToken ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350), ct);
            });

        await harness.RunUntilAsync(() => harness.HeartbeatCount() >= 3);

        // Heartbeat called at least 3× while the handler was busy (350ms / 50ms ≈ 7, minus a few
        // due to scheduling jitter). One call is enough to prove the heartbeat task is wired.
        harness.MainQueue.Verify(q => q.UpdateMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(3));
    }

    [Fact]
    public async Task Worker_drops_envelope_missing_required_fields_without_invoking_handler()
    {
        var harness = new Harness();
        var envelope = new SessionDeletionEnvelope
        {
            // ManifestId deliberately empty — required-fields guard must catch this.
            TenantId = TenantId, SessionId = SessionId, ManifestId = string.Empty,
        };
        harness.EnqueueMessage(JsonConvert.SerializeObject(envelope), dequeueCount: 1);

        await harness.RunUntilAsync(() => harness.MainQueueDeleted());

        harness.HandlerMock.Verify(h => h.HandleAsync(
            It.IsAny<SessionDeletionEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.MainQueue.Verify(q => q.DeleteMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    // ============================================================ Harness ====

    private sealed class Harness
    {
        public Mock<QueueClient> MainQueue { get; }
        public Mock<QueueClient> PoisonQueue { get; }
        public Mock<SessionDeletionHandler> HandlerMock { get; }
        public Mock<AdminConfigurationService> AdminConfig { get; }
        // PR-B audit consolidation: deletion_poisoned tenant audit moved to SessionDeletionPoisoned
        // OpsEvent. We keep AuditCalls as a passthrough capture (always empty for the worker
        // post-PR-B) so existing assertions like DoesNotContain still compile.
        public List<AuditEntry> AuditCalls { get; } = new List<AuditEntry>();
        public List<CapturedOpsEvent> OpsEventCalls { get; } = new List<CapturedOpsEvent>();
        public SessionDeletionWorker Sut { get; }

        private readonly Queue<QueueMessage> _pendingMessages = new Queue<QueueMessage>();

        public Harness(TimeSpan? heartbeatInterval = null)
        {
            MainQueue = new Mock<QueueClient>();
            PoisonQueue = new Mock<QueueClient>();

            MainQueue.Setup(q => q.CreateIfNotExistsAsync(
                    It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Response?)null);
            PoisonQueue.Setup(q => q.CreateIfNotExistsAsync(
                    It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Response?)null);

            MainQueue.Setup(q => q.ReceiveMessagesAsync(
                    It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns<int, TimeSpan?, CancellationToken>((maxMessages, _, _) =>
                {
                    var batch = new List<QueueMessage>();
                    while (batch.Count < maxMessages && _pendingMessages.Count > 0)
                    {
                        batch.Add(_pendingMessages.Dequeue());
                    }
                    var response = QueuesModelFactory.QueueMessage(
                        messageId: "msg-batch", popReceipt: "pop-batch",
                        body: new BinaryData(string.Empty), dequeueCount: 0);
                    return Task.FromResult(Response.FromValue(batch.ToArray(), new Mock<Response>().Object));
                });

            MainQueue.Setup(q => q.DeleteMessageAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Mock<Response>().Object);

            MainQueue.Setup(q => q.UpdateMessageAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, string, TimeSpan, CancellationToken>((id, pr, body, vis, ct) =>
                {
                    var updated = QueuesModelFactory.UpdateReceipt(
                        "pop-extended-" + Guid.NewGuid().ToString("N"),
                        DateTimeOffset.UtcNow.Add(vis));
                    return Task.FromResult(Response.FromValue(updated, new Mock<Response>().Object));
                });

            PoisonQueue.Setup(q => q.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((body, _) =>
                {
                    var receipt = QueuesModelFactory.SendReceipt("poison-msg", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(7), "poison-pop", DateTimeOffset.UtcNow);
                    return Task.FromResult(Response.FromValue(receipt, new Mock<Response>().Object));
                });

            // Handler — virtual HandleAsync; default behaviour is no-op (handler succeeded).
            // Moq needs real ctor args for the proxy; the worker only calls HandleAsync so the
            // inner deps stay untouched.
            var storageMock = new Mock<TableStorageService>(Mock.Of<TableServiceClient>(), NullLogger<TableStorageService>.Instance);
            // PR4c F5: Worker now reads the Sessions row to verify PendingDeletionManifestId
            // matches the envelope's manifestId before issuing the poison CAS. Default mock
            // returns a row whose pending matches the test's ManifestId so the existing PR4b
            // tests (which exercise the post-pre-check CAS paths) keep working.
            storageMock.Setup(s => s.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new TableEntity(TenantId, SessionId)
                {
                    ["DeletionState"] = SessionDeletionState.Running,
                    ["PendingDeletionManifestId"] = ManifestId,
                });
            // Default: poison-state CAS succeeds. Individual tests override to verify the E1 fix.
            storageMock.Setup(s => s.CasSetSessionDeletionStateAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
                {
                    Outcome = TableStorageService.SessionDeletionStateCasOutcome.Updated,
                    CurrentState = SessionDeletionState.Poisoned,
                });
            var blobMock = new Mock<BlobStorageService>(
                new Azure.Storage.Blobs.BlobServiceClient("UseDevelopmentStorage=true"),
                NullLogger<BlobStorageService>.Instance, false);
            // PR-B Codex F4 follow-up: worker reads DeletionProgress on the poison path so the
            // SessionDeletionPoisoned OpsEvent can include the handler's last-failure breadcrumb.
            // Default mock returns a populated progress to exercise the enrichment path.
            blobMock.Setup(b => b.DownloadDeletionProgressAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(((AutopilotMonitor.Shared.Models.Deletion.DeletionProgress, string))(
                    new AutopilotMonitor.Shared.Models.Deletion.DeletionProgress
                    {
                        LastFailureType = "verification_residuals",
                        LastFailureMessage = "2 observed residual row(s)",
                        // Codex F2 round-3: the persisted count is the verifier's OBSERVED count
                        // (capped at MaxResidualSampleSize=50 per table + short-circuit at first
                        // failing table). Default mock uses a low number so the field matches the
                        // sample length; the cap-hit test below exercises the lower-bound case.
                        LastObservedResidualCount = 2,
                        LastResidualSampleJson = "[{\"table\":\"Events\",\"pk\":\"t\",\"rk\":\"r\"},{\"table\":\"Signals\",\"pk\":\"t\",\"rk\":\"r2\"}]",
                    },
                    "etag-mock"));
            var verifierMock = new Mock<CascadeVerificationService>(
                Mock.Of<ISessionDeletionInventoryReader>(),
                NullLogger<CascadeVerificationService>.Instance);
            HandlerMock = new Mock<SessionDeletionHandler>(
                storageMock.Object, blobMock.Object, verifierMock.Object,
                Mock.Of<IMaintenanceRepository>(),
                new FakeSignalRNotificationService(),
                new AutopilotMonitor.Functions.Tests.Helpers.NoOpDiagnosticsBlobCascadeDeleter(),
                NullLogger<SessionDeletionHandler>.Instance);
            HandlerMock.Setup(h => h.HandleAsync(It.IsAny<SessionDeletionEnvelope>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            AdminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(),
                NullLogger<AdminConfigurationService>.Instance,
                new MemoryCache(new MemoryCacheOptions()));
            // Default: kill-switch off. PR5 finding 1 moved the worker's read onto the uncached
            // helper; the cached setter is preserved for any sibling test that still observes it.
            AdminConfig.Setup(a => a.GetConfigurationAsync())
                .ReturnsAsync(new AdminConfiguration { SessionDeletionKillSwitch = false });
            AdminConfig.Setup(a => a.IsSessionDeletionKillSwitchActiveAsync())
                .ReturnsAsync(false);

            // PR-B audit consolidation: build a real OpsEventService backed by a captured-list
            // mock repository so the worker's RecordSessionDeletionPoisonedAsync call is
            // observable as an OpsEventEntry on the OpsEventCalls list.
            var opsRepo = new Mock<IOpsEventRepository>();
            opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
                .Returns<OpsEventEntry>(e =>
                {
                    // Locked so RunUntilAsync's condition poll can read Count from the test
                    // thread while the worker thread appends.
                    lock (OpsEventCalls) OpsEventCalls.Add(CapturedOpsEvent.From(e));
                    return Task.CompletedTask;
                });
            var alertDispatch = new OpsAlertDispatchService(
                AdminConfig.Object,
                new TelegramNotificationService(new HttpClient(), Mock.Of<IConfigRepository>(), NullLogger<TelegramNotificationService>.Instance),
                new AutopilotMonitor.Functions.Services.Notifications.WebhookNotificationService(new HttpClient(), NullLogger<AutopilotMonitor.Functions.Services.Notifications.WebhookNotificationService>.Instance),
                NullLogger<OpsAlertDispatchService>.Instance);
            var opsService = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch);

            Sut = new SessionDeletionWorker(
                MainQueue.Object, PoisonQueue.Object,
                HandlerMock.Object, storageMock.Object,
                AdminConfig.Object, blobMock.Object, opsService,
                NullLogger<SessionDeletionWorker>.Instance,
                heartbeatInterval: heartbeatInterval ?? TimeSpan.FromMilliseconds(200),
                pollInterval: TimeSpan.FromMilliseconds(50));

            StorageMock = storageMock;
            BlobMock = blobMock;
        }

        public Mock<BlobStorageService> BlobMock { get; private set; } = null!;

        public Mock<TableStorageService> StorageMock { get; private set; } = null!;

        public void SetKillSwitch(bool active)
        {
            // PR5 finding 1: worker checks the kill-switch via the uncached helper, not via the
            // 5-minute-cached GetConfigurationAsync, so a flip-ON is honored across instances
            // within seconds. Mock both paths so existing tests keep working regardless of which
            // surface the production code uses.
            AdminConfig.Setup(a => a.GetConfigurationAsync())
                .ReturnsAsync(new AdminConfiguration { SessionDeletionKillSwitch = active });
            AdminConfig.Setup(a => a.IsSessionDeletionKillSwitchActiveAsync())
                .ReturnsAsync(active);
        }

        public void EnqueueMessage(string body, int dequeueCount)
        {
            var msg = QueuesModelFactory.QueueMessage(
                messageId: "msg-" + Guid.NewGuid().ToString("N"),
                popReceipt: "pop-" + Guid.NewGuid().ToString("N"),
                body: new BinaryData(body),
                dequeueCount: dequeueCount);
            _pendingMessages.Enqueue(msg);
        }

        public async Task RunForAsync(TimeSpan duration)
        {
            using var cts = new CancellationTokenSource(duration);
            try { await Sut.StartAsync(cts.Token); }
            catch (OperationCanceledException) { /* expected */ }
            try { await Task.Delay(duration, cts.Token); }
            catch (OperationCanceledException) { /* expected on timeout */ }
            try { await Sut.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { /* expected */ }
        }

        /// <summary>
        /// De-flake (CI run 30047512186): <see cref="RunForAsync"/>'s fixed wall-clock window
        /// raced the worker's background poll loop on loaded CI runners — positive assertions
        /// (delete issued, ops event recorded) could fire before the loop ever processed the
        /// message. Runs the worker until <paramref name="condition"/> observes the awaited
        /// side effect (polled every 25 ms); the ceiling only bounds a genuinely broken worker,
        /// the asserts after this call remain the source of truth. Same pattern as the watchdog
        /// event-gate de-flake. Only for POSITIVE expectations — Times.Never-style tests keep
        /// the fixed window (there is no event to wait for).
        /// </summary>
        public async Task RunUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            using var cts = new CancellationTokenSource();
            try { await Sut.StartAsync(cts.Token); }
            catch (OperationCanceledException) { /* expected */ }
            while (!condition() && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            cts.Cancel();
            try { await Sut.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { /* expected */ }
        }

        // Condition probes for RunUntilAsync. Moq's Invocations list snapshots under its
        // internal lock, so polling it from the test thread while the worker invokes the
        // mock is safe. OpsEventCalls is guarded by its own lock (see the repo callback).
        public bool MainQueueDeleted() =>
            MainQueue.Invocations.Any(i => i.Method.Name == nameof(QueueClient.DeleteMessageAsync));
        public int HeartbeatCount() =>
            MainQueue.Invocations.Count(i => i.Method.Name == nameof(QueueClient.UpdateMessageAsync));
        public bool OpsEventRecorded()
        {
            lock (OpsEventCalls) return OpsEventCalls.Count > 0;
        }
    }

    private sealed record AuditEntry(
        string TenantId, string Action, string EntityType, string EntityId, string PerformedBy,
        Dictionary<string, string>? Details);

    /// <summary>
    /// Flattened OpsEventEntry projection for assertions. <see cref="ManifestId"/> + the other
    /// strongly-typed accessors are pulled from <c>Details</c> JSON so tests don't have to
    /// re-deserialise.
    /// </summary>
    private sealed record CapturedOpsEvent(
        string EventType, string Severity, string Message,
        string? TenantId, string? SessionId, string? ManifestId, string RawDetails)
    {
        public static CapturedOpsEvent From(OpsEventEntry e)
        {
            string? tenantId = e.TenantId;
            string? sessionId = null;
            string? manifestId = null;
            if (!string.IsNullOrEmpty(e.Details))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.Details!);
                    if (doc.RootElement.TryGetProperty("sessionId", out var s)) sessionId = s.GetString();
                    if (doc.RootElement.TryGetProperty("manifestId", out var m)) manifestId = m.GetString();
                }
                catch (System.Text.Json.JsonException) { /* tolerate malformed test payloads */ }
            }
            return new CapturedOpsEvent(e.EventType, e.Severity, e.Message, tenantId, sessionId, manifestId, e.Details ?? string.Empty);
        }
    }
}
