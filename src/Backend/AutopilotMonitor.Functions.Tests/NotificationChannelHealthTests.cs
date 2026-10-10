using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Channel delivery health: the status rule and the fold of each send outcome
/// (<see cref="NotificationChannelHealthEvaluator"/>), the recorder's compare-and-swap write and
/// its once-per-episode bell, the dispatcher's recording of every non-Push send, the read
/// endpoint's projection onto the stored channels, and the table mapping.
/// </summary>
public class NotificationChannelHealthTests
{
    private const string TenantKey = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTime T0 = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
    private static readonly NotificationScope Tenant = NotificationScope.Tenant(TenantKey);

    private static NotificationChannel Webhook(string id = "c1", string url = "https://hooks.example.invalid/a", string name = "Service Desk")
        => new() { Id = id, Name = name, ProviderType = (int)WebhookProviderType.GenericJson, Url = url, Enabled = true };

    private static NotificationSendResult Ok() => new() { Success = true, StatusCode = 202, Message = "Test notification sent successfully." };

    private static NotificationSendResult Fail(int? status = 401, string message = "Webhook returned HTTP 401: {\"error\":\"secret body\"}")
        => new() { Success = false, StatusCode = status, Message = message };

    private static ChannelHealthFold Fold(NotificationChannelHealth? stored, NotificationSendResult result,
        ChannelSendKind kind = ChannelSendKind.Delivery, DateTime? at = null, NotificationChannel? channel = null)
    {
        channel ??= Webhook();
        return NotificationChannelHealthEvaluator.Apply(stored, TenantKey, channel.Id,
            NotificationChannelHealthEvaluator.Fingerprint(channel), result, kind, at ?? T0);
    }

    // ── Status rule and fold ──────────────────────────────────────────────

    [Fact]
    public void A_delivered_send_on_a_new_channel_is_ok()
    {
        var fold = Fold(null, Ok());

        Assert.Equal("1", fold.Next.Recent);
        Assert.Equal(T0, fold.Next.LastSuccessUtc);
        Assert.Equal(NotificationChannelHealthStatus.Ok, NotificationChannelHealthEvaluator.Evaluate(fold.Next, T0));
        Assert.Equal(ChannelHealthTransition.None, fold.Transition);
        Assert.False(fold.RaiseBell);
    }

    [Fact]
    public void One_failure_degrades_with_the_status_line_and_never_the_body()
    {
        var fold = Fold(Fold(null, Ok()).Next, Fail());

        Assert.Equal(1, fold.Next.ConsecutiveFailures);
        Assert.Equal("HTTP 401 Unauthorized", fold.Next.LastError);
        Assert.Equal(401, fold.Next.LastStatusCode);
        Assert.Equal(T0, fold.Next.FailingSinceUtc);
        Assert.Equal(NotificationChannelHealthStatus.Degraded, NotificationChannelHealthEvaluator.Evaluate(fold.Next, T0));
        Assert.False(fold.RaiseBell);
    }

    [Fact]
    public void Two_failed_deliveries_in_a_row_are_failing_and_ring_the_bell_once_per_episode()
    {
        var first = Fold(null, Fail(), at: T0);
        var second = Fold(first.Next, Fail(), at: T0.AddHours(1));
        var third = Fold(second.Next, Fail(), at: T0.AddHours(2));

        Assert.Equal(NotificationChannelHealthStatus.Failing, NotificationChannelHealthEvaluator.Evaluate(second.Next, T0.AddHours(1)));
        Assert.Equal(ChannelHealthTransition.BecameFailing, second.Transition);
        Assert.True(second.RaiseBell);
        Assert.Equal(T0.AddHours(1), second.Next.FailingNotifiedUtc);
        Assert.Equal(T0, second.Next.FailingSinceUtc);

        Assert.Equal(ChannelHealthTransition.None, third.Transition);
        Assert.False(third.RaiseBell);
        Assert.Equal(3, third.Next.ConsecutiveFailures);
    }

    [Fact]
    public void A_failing_test_never_rings_but_the_next_failed_delivery_does()
    {
        var first = Fold(null, Fail(), ChannelSendKind.Test);
        var second = Fold(first.Next, Fail(), ChannelSendKind.Test);
        Assert.Equal(ChannelHealthTransition.BecameFailing, second.Transition);
        Assert.False(second.RaiseBell);
        Assert.Null(second.Next.FailingNotifiedUtc);

        var delivery = Fold(second.Next, Fail());
        Assert.True(delivery.RaiseBell);
        Assert.Equal(ChannelHealthTransition.None, delivery.Transition);
    }

    [Theory]
    [InlineData(ChannelSendKind.Delivery)]
    [InlineData(ChannelSendKind.Test)]
    public void A_success_ends_the_episode_and_the_next_one_rings_again(ChannelSendKind kind)
    {
        var failing = Fold(Fold(null, Fail()).Next, Fail());
        var recovered = Fold(failing.Next, Ok(), kind, T0.AddHours(3));

        Assert.Equal(ChannelHealthTransition.Recovered, recovered.Transition);
        Assert.Equal(0, recovered.Next.ConsecutiveFailures);
        Assert.Null(recovered.Next.FailingSinceUtc);
        Assert.Null(recovered.Next.FailingNotifiedUtc);
        Assert.Equal("HTTP 401 Unauthorized", recovered.Next.LastError);   // the last failure stays readable

        var again = Fold(Fold(recovered.Next, Fail()).Next, Fail());
        Assert.True(again.RaiseBell);
    }

    [Fact]
    public void One_failure_keeps_a_delivering_channel_degraded_for_seven_days()
    {
        var degraded = Fold(Fold(null, Fail(), at: T0).Next, Ok(), at: T0.AddMinutes(5)).Next;

        Assert.Equal(NotificationChannelHealthStatus.Degraded, NotificationChannelHealthEvaluator.Evaluate(degraded, T0.AddDays(6)));
        Assert.Equal(NotificationChannelHealthStatus.Ok, NotificationChannelHealthEvaluator.Evaluate(degraded, T0.AddDays(7)));
    }

    [Fact]
    public void A_failure_pushed_out_of_the_kept_outcomes_no_longer_degrades()
    {
        var row = Fold(null, Fail(), at: T0).Next;
        for (var i = 0; i < NotificationChannelHealth.RecentCapacity - 1; i++)
            row = Fold(row, Ok(), at: T0.AddMinutes(i + 1)).Next;
        Assert.Equal(NotificationChannelHealthStatus.Degraded, NotificationChannelHealthEvaluator.Evaluate(row, T0.AddHours(1)));

        row = Fold(row, Ok(), at: T0.AddMinutes(30)).Next;
        Assert.Equal(NotificationChannelHealthStatus.Ok, NotificationChannelHealthEvaluator.Evaluate(row, T0.AddHours(1)));
    }

    [Fact]
    public void Recent_keeps_the_last_twenty_outcomes()
    {
        var row = Fold(null, Fail()).Next;
        for (var i = 0; i < NotificationChannelHealth.RecentCapacity; i++)
            row = Fold(row, Ok()).Next;

        Assert.Equal(new string('1', NotificationChannelHealth.RecentCapacity), row.Recent);
        var dto = NotificationChannelHealthEvaluator.ToDto("c1", row, T0);
        Assert.Equal(20, dto.RecentAttempts);
        Assert.Equal(0, dto.RecentFailures);
    }

    [Fact]
    public void A_new_destination_starts_fresh_and_an_old_row_reads_as_unknown()
    {
        var failing = Fold(Fold(null, Fail()).Next, Fail()).Next;
        var moved = Webhook(url: "https://hooks.example.invalid/b");
        var movedFingerprint = NotificationChannelHealthEvaluator.Fingerprint(moved);

        Assert.Null(NotificationChannelHealthEvaluator.Current(failing, movedFingerprint));
        Assert.Equal(NotificationChannelHealthStatus.Unknown,
            NotificationChannelHealthEvaluator.Evaluate(NotificationChannelHealthEvaluator.Current(failing, movedFingerprint), T0));

        var fresh = Fold(failing, Ok(), channel: moved);
        Assert.Equal("1", fresh.Next.Recent);
        Assert.Equal(movedFingerprint, fresh.Next.Fingerprint);
        Assert.Equal(ChannelHealthTransition.None, fresh.Transition);
    }

    [Fact]
    public void The_fingerprint_follows_every_field_that_decides_the_destination()
    {
        var baseline = NotificationChannelHealthEvaluator.Fingerprint(Webhook());

        Assert.Equal(baseline, NotificationChannelHealthEvaluator.Fingerprint(Webhook(name: "Renamed")));
        Assert.NotEqual(baseline, NotificationChannelHealthEvaluator.Fingerprint(Webhook(url: "https://hooks.example.invalid/b")));
        var headers = Webhook(); headers.CustomHeadersJson = "{\"Authorization\":\"Bearer x\"}";
        Assert.NotEqual(baseline, NotificationChannelHealthEvaluator.Fingerprint(headers));
        var signed = Webhook(); signed.SigningSecret = "s3cret";
        Assert.NotEqual(baseline, NotificationChannelHealthEvaluator.Fingerprint(signed));
        var slack = Webhook(); slack.ProviderType = (int)WebhookProviderType.Slack;
        Assert.NotEqual(baseline, NotificationChannelHealthEvaluator.Fingerprint(slack));
        Assert.Equal(16, baseline.Length);
        Assert.DoesNotContain("hooks", baseline);
    }

    [Theory]
    [InlineData(502, "Webhook returned HTTP 502: <html>body</html>", "HTTP 502 Bad Gateway")]
    [InlineData(404, "", "HTTP 404 Not Found")]
    [InlineData(599, "", "HTTP 599")]
    [InlineData(null, "Connection error: No such host is known.", "Connection error: No such host is known.")]
    [InlineData(null, "", "Delivery failed.")]
    public void DescribeFailure_uses_the_status_line_or_the_transport_message(int? status, string message, string expected)
        => Assert.Equal(expected, NotificationChannelHealthEvaluator.DescribeFailure(new NotificationSendResult { StatusCode = status, Message = message }));

    [Fact]
    public void DescribeFailure_caps_a_long_transport_message()
    {
        var described = NotificationChannelHealthEvaluator.DescribeFailure(new NotificationSendResult { Message = new string('x', 500) });
        Assert.Equal(NotificationChannelHealthEvaluator.MaxErrorLength, described.Length);
        Assert.EndsWith("…", described);
    }

    // ── Recorder ──────────────────────────────────────────────────────────

    private sealed class Clock : TimeProvider
    {
        public DateTime Now { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    /// <summary>In-memory table with real ETag semantics; <see cref="BeforeWrite"/> lets a test slip in a competing writer.</summary>
    private sealed class FakeRepository : INotificationChannelHealthRepository
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string, string), (NotificationChannelHealth Row, int Version)> _rows = new();

        public Action<FakeRepository>? BeforeWrite { get; set; }
        public bool Throw { get; set; }
        public int Writes { get; private set; }

        public NotificationChannelHealth? Get(string scope, string id)
        {
            lock (_gate) return _rows.TryGetValue((scope, id), out var e) ? e.Row : null;
        }

        public Task<(NotificationChannelHealth? Row, string? ETag)> GetWithETagAsync(string scopeKey, string channelId)
        {
            if (Throw) throw new InvalidOperationException("storage down");
            lock (_gate)
            {
                return Task.FromResult<(NotificationChannelHealth?, string?)>(
                    _rows.TryGetValue((scopeKey, channelId), out var e) ? (e.Row, e.Version.ToString()) : (null, null));
            }
        }

        public Task<bool> TryUpsertAsync(NotificationChannelHealth row, string? ifMatchETag)
        {
            var hook = BeforeWrite;
            BeforeWrite = null;
            hook?.Invoke(this);
            lock (_gate)
            {
                var key = (row.ScopeKey, row.ChannelId);
                var exists = _rows.TryGetValue(key, out var current);
                if (ifMatchETag == null ? exists : !exists || current.Version.ToString() != ifMatchETag)
                    return Task.FromResult(false);
                _rows[key] = (row, exists ? current.Version + 1 : 1);
                Writes++;
                return Task.FromResult(true);
            }
        }

        public Task<List<NotificationChannelHealth>> ListAsync(string scopeKey)
        {
            lock (_gate) return Task.FromResult(_rows.Where(r => r.Key.Item1 == scopeKey).Select(r => r.Value.Row).ToList());
        }

        public Task<List<(NotificationChannelHealth Row, string ETag)>> ListAllAsync()
        {
            if (Throw) throw new InvalidOperationException("storage down");
            lock (_gate) return Task.FromResult(_rows.Values.Select(v => (v.Row, v.Version.ToString())).ToList());
        }

        public Task<bool> TryDeleteAsync(NotificationChannelHealth row, string ifMatchETag)
        {
            var hook = BeforeWrite;
            BeforeWrite = null;
            hook?.Invoke(this);
            lock (_gate)
            {
                var key = (row.ScopeKey, row.ChannelId);
                if (!_rows.TryGetValue(key, out var current) || current.Version.ToString() != ifMatchETag)
                    return Task.FromResult(false);
                _rows.Remove(key);
                return Task.FromResult(true);
            }
        }

        /// <summary>Seeds a row as if its channel last sent at <paramref name="lastAttempt"/>.</summary>
        public void Seed(string scope, string channelId, DateTime lastAttempt)
        {
            lock (_gate)
            {
                var version = _rows.TryGetValue((scope, channelId), out var existing) ? existing.Version + 1 : 1;
                _rows[(scope, channelId)] = (new NotificationChannelHealth
                {
                    ScopeKey = scope, ChannelId = channelId, Fingerprint = "f", Recent = "1", LastAttemptUtc = lastAttempt, LastSuccessUtc = lastAttempt,
                }, version);
            }
        }

        public bool Has(string scope, string channelId)
        {
            lock (_gate) return _rows.ContainsKey((scope, channelId));
        }
    }

    private sealed class Harness
    {
        public FakeRepository Repository { get; } = new();
        public Mock<TenantNotificationService> TenantBell { get; } = new(Mock.Of<ITenantNotificationRepository>(), new FakeSignalRNotificationService(), NullLogger<TenantNotificationService>.Instance);
        public Mock<GlobalNotificationService> GlobalBell { get; } = new(Mock.Of<INotificationRepository>(), new FakeSignalRNotificationService(), NullLogger<GlobalNotificationService>.Instance);
        public Clock Clock { get; } = new();

        public NotificationChannelHealthRecorder Recorder()
            => new(Repository, TenantBell.Object, GlobalBell.Object, NullLogger<NotificationChannelHealthRecorder>.Instance, telemetry: null, time: Clock);

        public void VerifyTenantBells(int times)
            => TenantBell.Verify(b => b.CreateNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Exactly(times));
    }

    [Fact]
    public async Task Recorder_rings_the_tenant_bell_once_for_a_failing_channel()
    {
        var h = new Harness();
        var recorder = h.Recorder();
        var channel = Webhook();

        await recorder.RecordAsync(Tenant, channel, Fail(), ChannelSendKind.Delivery);
        h.VerifyTenantBells(0);
        await recorder.RecordAsync(Tenant, channel, Fail(), ChannelSendKind.Delivery);
        await recorder.RecordAsync(Tenant, channel, Fail(), ChannelSendKind.Delivery);

        h.VerifyTenantBells(1);
        h.TenantBell.Verify(b => b.CreateNotificationAsync(
            TenantKey,
            NotificationChannelHealthRecorder.TenantBellType,
            "Notification channel failing",
            "\"Service Desk\" could not deliver the last 2 notifications (HTTP 401 Unauthorized). Check the destination, then send a test.",
            NotificationChannelHealthRecorder.TenantSettingsHref), Times.Once);
        h.GlobalBell.Verify(b => b.CreateNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        Assert.Equal(3, h.Repository.Get(TenantKey, "c1")!.ConsecutiveFailures);
    }

    [Fact]
    public async Task A_writer_that_loses_the_race_to_the_bell_stamp_raises_no_second_bell()
    {
        var h = new Harness();
        var recorder = h.Recorder();
        var channel = Webhook();
        await recorder.RecordAsync(Tenant, channel, Fail(), ChannelSendKind.Delivery);

        // A concurrent send stamps the episode between this writer's read and its write.
        h.Repository.BeforeWrite = repo =>
        {
            var (stored, etag) = repo.GetWithETagAsync(TenantKey, "c1").Result;
            var competitor = NotificationChannelHealthEvaluator.Apply(stored, TenantKey, "c1",
                NotificationChannelHealthEvaluator.Fingerprint(channel), Fail(), ChannelSendKind.Delivery, T0);
            Assert.True(competitor.RaiseBell);
            Assert.True(repo.TryUpsertAsync(competitor.Next, etag).Result);
        };
        await recorder.RecordAsync(Tenant, channel, Fail(), ChannelSendKind.Delivery);

        h.VerifyTenantBells(0);   // the competitor owned the bell; this writer re-read and saw the stamp
        var row = h.Repository.Get(TenantKey, "c1")!;
        Assert.Equal(3, row.ConsecutiveFailures);
        Assert.NotNull(row.FailingNotifiedUtc);
    }

    [Fact]
    public async Task Concurrent_failures_raise_exactly_one_bell()
    {
        var h = new Harness();
        var recorder = h.Recorder();
        var channel = Webhook();

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => recorder.RecordAsync(Tenant, channel, Fail(), ChannelSendKind.Delivery))));

        h.VerifyTenantBells(1);
    }

    [Fact]
    public async Task An_ops_channel_rings_the_global_bell()
    {
        var h = new Harness();
        var recorder = h.Recorder();
        var channel = Webhook(id: "ops");

        await recorder.RecordAsync(NotificationScope.Platform, channel, Fail(), ChannelSendKind.Delivery);
        await recorder.RecordAsync(NotificationScope.Platform, channel, Fail(), ChannelSendKind.Delivery);

        h.GlobalBell.Verify(b => b.CreateNotificationAsync(NotificationChannelHealthRecorder.OpsBellType,
            It.IsAny<string>(), It.IsAny<string>(), NotificationChannelHealthRecorder.OpsSettingsHref), Times.Once);
        h.VerifyTenantBells(0);
        Assert.NotNull(h.Repository.Get("platform", "ops"));
    }

    [Fact]
    public async Task Tests_never_ring_and_push_channels_are_not_recorded()
    {
        var h = new Harness();
        var recorder = h.Recorder();

        await recorder.RecordAsync(Tenant, Webhook(), Fail(), ChannelSendKind.Test);
        await recorder.RecordAsync(Tenant, Webhook(), Fail(), ChannelSendKind.Test);
        var push = new NotificationChannel { Id = "push", Name = "Phones", ProviderType = (int)WebhookProviderType.Push, Enabled = true };
        await recorder.RecordAsync(Tenant, push, Fail(), ChannelSendKind.Delivery);

        h.VerifyTenantBells(0);
        Assert.Equal(2, h.Repository.Get(TenantKey, "c1")!.ConsecutiveFailures);
        Assert.Null(h.Repository.Get(TenantKey, "push"));
    }

    [Fact]
    public async Task A_storage_failure_never_reaches_the_caller()
    {
        var h = new Harness();
        h.Repository.Throw = true;

        await h.Recorder().RecordAsync(Tenant, Webhook(), Fail(), ChannelSendKind.Delivery);

        h.VerifyTenantBells(0);
    }

    // ── Dispatcher ────────────────────────────────────────────────────────

    private static NotificationChannelDispatcher Dispatcher(
        Mock<INotificationChannelHealthRecorder> recorder, Func<NotificationSendResult>? webhookResult = null, Mock<IPushChannelSender>? push = null)
    {
        var webhook = new Mock<WebhookNotificationService>(new HttpClient()) { CallBase = false };
        if (webhookResult == null)
        {
            webhook.Setup(w => w.SendAsync(It.IsAny<string>(), It.IsAny<WebhookProviderType>(), It.IsAny<NotificationAlert>(),
                    It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>()))
                .ReturnsAsync(Fail());
        }
        else
        {
            webhook.Setup(w => w.SendAsync(It.IsAny<string>(), It.IsAny<WebhookProviderType>(), It.IsAny<NotificationAlert>(),
                    It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>()))
                .ReturnsAsync(webhookResult);
        }
        var telegram = new Mock<TelegramNotificationService>(new HttpClient(), Mock.Of<IConfigRepository>(), NullLogger<TelegramNotificationService>.Instance) { CallBase = false };
        telegram.Setup(t => t.SendAlertAsync(It.IsAny<string>(), It.IsAny<NotificationAlert>())).ReturnsAsync(Ok());
        return new NotificationChannelDispatcher(webhook.Object, telegram.Object, (push ?? new Mock<IPushChannelSender>()).Object,
            Mock.Of<IEmailChannelSender>(), recorder.Object, NullLogger<NotificationChannelDispatcher>.Instance);
    }

    [Fact]
    public async Task Every_non_push_delivery_is_recorded_and_push_is_not()
    {
        var recorder = new Mock<INotificationChannelHealthRecorder>();
        var push = new Mock<IPushChannelSender>();
        var dispatcher = Dispatcher(recorder, push: push);
        var telegram = new NotificationChannel { Id = "tg", Name = "tg", ProviderType = (int)WebhookProviderType.Telegram, Url = "-100123", Enabled = true };
        var phones = new NotificationChannel { Id = "push", Name = "push", ProviderType = (int)WebhookProviderType.Push, Enabled = true };

        await dispatcher.SendToChannelsAsync(new[] { Webhook(), telegram, phones }, new NotificationAlert { Title = "t", Summary = "s" }, Tenant);

        recorder.Verify(r => r.RecordAsync(Tenant, It.Is<NotificationChannel>(c => c.Id == "c1"), It.Is<NotificationSendResult>(x => !x.Success), ChannelSendKind.Delivery), Times.Once);
        recorder.Verify(r => r.RecordAsync(Tenant, It.Is<NotificationChannel>(c => c.Id == "tg"), It.Is<NotificationSendResult>(x => x.Success), ChannelSendKind.Delivery), Times.Once);
        recorder.Verify(r => r.RecordAsync(It.IsAny<NotificationScope>(), It.Is<NotificationChannel>(c => c.Id == "push"), It.IsAny<NotificationSendResult>(), It.IsAny<ChannelSendKind>()), Times.Never);
        push.Verify(p => p.SendAsync(Tenant, It.IsAny<NotificationAlert>()), Times.Once);
    }

    [Fact]
    public async Task A_test_send_is_recorded_as_a_test()
    {
        var recorder = new Mock<INotificationChannelHealthRecorder>();

        var result = await Dispatcher(recorder).SendTestAsync(Webhook(), new NotificationAlert { Title = "t", Summary = "s" }, Tenant);

        Assert.False(result.Success);
        recorder.Verify(r => r.RecordAsync(Tenant, It.IsAny<NotificationChannel>(), result, ChannelSendKind.Test), Times.Once);
    }

    [Fact]
    public async Task A_throwing_transport_is_recorded_as_a_failure_and_the_next_channel_still_sends()
    {
        var recorder = new Mock<INotificationChannelHealthRecorder>();
        var dispatcher = Dispatcher(recorder, webhookResult: () => throw new InvalidOperationException("boom"));
        var telegram = new NotificationChannel { Id = "tg", Name = "tg", ProviderType = (int)WebhookProviderType.Telegram, Url = "-100123", Enabled = true };

        await dispatcher.SendToChannelsAsync(new[] { Webhook(), telegram }, new NotificationAlert { Title = "t", Summary = "s" }, Tenant);

        recorder.Verify(r => r.RecordAsync(Tenant, It.Is<NotificationChannel>(c => c.Id == "c1"),
            It.Is<NotificationSendResult>(x => !x.Success && x.Message == "Unexpected error: boom"), ChannelSendKind.Delivery), Times.Once);
        recorder.Verify(r => r.RecordAsync(Tenant, It.Is<NotificationChannel>(c => c.Id == "tg"), It.IsAny<NotificationSendResult>(), ChannelSendKind.Delivery), Times.Once);
    }

    // ── Read endpoint projection ──────────────────────────────────────────

    [Fact]
    public async Task The_health_list_follows_the_stored_channels()
    {
        var h = new Harness();
        var kept = Webhook(id: "kept");
        var moved = Webhook(id: "moved");
        var recorder = h.Recorder();
        await recorder.RecordAsync(Tenant, kept, Fail(), ChannelSendKind.Delivery);
        await recorder.RecordAsync(Tenant, kept, Fail(), ChannelSendKind.Delivery);
        await recorder.RecordAsync(Tenant, moved, Ok(), ChannelSendKind.Delivery);
        await recorder.RecordAsync(Tenant, Webhook(id: "deleted"), Ok(), ChannelSendKind.Delivery);

        var function = new NotificationChannelHealthFunction(NullLogger<NotificationChannelHealthFunction>.Instance,
            null!, null!, h.Repository, h.Clock);
        var phones = new NotificationChannel { Id = "push", Name = "push", ProviderType = (int)WebhookProviderType.Push, Enabled = true };
        var never = Webhook(id: "never");
        var response = await function.BuildAsync(
            new[] { never, kept, Webhook(id: "moved", url: "https://hooks.example.invalid/new"), phones }, Tenant);

        Assert.Equal(new[] { "never", "kept", "moved" }, response.Channels.Select(c => c.ChannelId));
        Assert.Equal(NotificationChannelHealthStatus.Unknown, response.Channels[0].Status);
        Assert.Equal(NotificationChannelHealthStatus.Failing, response.Channels[1].Status);
        Assert.Equal(2, response.Channels[1].RecentFailures);
        Assert.Equal("HTTP 401 Unauthorized", response.Channels[1].LastError);
        Assert.Equal(NotificationChannelHealthStatus.Unknown, response.Channels[2].Status);   // destination changed since
        Assert.Null(response.Channels[2].LastAttemptUtc);
    }

    // ── Orphan sweep (maintenance run) ────────────────────────────────────

    private const string OtherTenant = "22222222-2222-2222-2222-222222222222";
    private static readonly DateTime Old = T0.AddDays(-NotificationChannelHealthMaintenance.OrphanRetentionDays - 1);
    private static readonly DateTime Recent = T0.AddDays(-NotificationChannelHealthMaintenance.OrphanRetentionDays + 1);

    private static TenantConfiguration TenantWith(string tenantId, params string[] channelIds)
    {
        var config = TenantConfiguration.CreateDefault(tenantId);
        config.NotificationChannelsJson = NotificationChannel.SerializeList(channelIds.Select(id => Webhook(id: id)));
        return config;
    }

    private static NotificationChannelHealthMaintenance Sweep(FakeRepository repository, Mock<IConfigRepository> configs)
        => new(repository, configs.Object, NullLogger<NotificationChannelHealthMaintenance>.Instance);

    [Fact]
    public async Task The_sweep_deletes_old_rows_of_deleted_channels_and_keeps_everything_else()
    {
        var repository = new FakeRepository();
        repository.Seed(TenantKey, "live-old", Old);        // channel still configured, silent for a month
        repository.Seed(TenantKey, "gone-old", Old);        // channel deleted, last send older than the grace
        repository.Seed(TenantKey, "gone-recent", Recent);  // channel deleted, still inside the grace
        var configs = new Mock<IConfigRepository>();
        configs.Setup(c => c.GetTenantConfigurationAsync(TenantKey)).ReturnsAsync(TenantWith(TenantKey, "live-old", "never-sent"));

        var result = await Sweep(repository, configs).RunAsync(T0);

        Assert.True(repository.Has(TenantKey, "live-old"));
        Assert.False(repository.Has(TenantKey, "gone-old"));
        Assert.True(repository.Has(TenantKey, "gone-recent"));
        Assert.Equal(3, result.RowsScanned);
        Assert.Equal(1, result.RowsDeleted);
        Assert.Equal(0, result.ScopesSkipped);
    }

    [Fact]
    public async Task A_failed_config_read_skips_the_scope_and_deletes_nothing_there()
    {
        var repository = new FakeRepository();
        repository.Seed(TenantKey, "gone-old", Old);
        repository.Seed(OtherTenant, "gone-old", Old);
        var configs = new Mock<IConfigRepository>();
        configs.Setup(c => c.GetTenantConfigurationAsync(TenantKey)).ThrowsAsync(new InvalidOperationException("storage down"));
        configs.Setup(c => c.GetTenantConfigurationAsync(OtherTenant)).ReturnsAsync(TenantWith(OtherTenant, "live"));

        var result = await Sweep(repository, configs).RunAsync(T0);

        Assert.True(repository.Has(TenantKey, "gone-old"));       // read failure never reads as "no channels"
        Assert.False(repository.Has(OtherTenant, "gone-old"));    // the other scope is still swept
        Assert.Equal(1, result.ScopesSkipped);
    }

    [Fact]
    public async Task A_tenant_without_a_configuration_has_no_channels_left()
    {
        var repository = new FakeRepository();
        repository.Seed(TenantKey, "any", Old);
        repository.Seed(TenantKey, "fresh", Recent);
        var configs = new Mock<IConfigRepository>();
        configs.Setup(c => c.GetTenantConfigurationAsync(TenantKey)).ReturnsAsync((TenantConfiguration?)null);

        await Sweep(repository, configs).RunAsync(T0);

        Assert.False(repository.Has(TenantKey, "any"));
        Assert.True(repository.Has(TenantKey, "fresh"));
    }

    [Fact]
    public async Task The_synthesized_legacy_channel_counts_as_live()
    {
        var repository = new FakeRepository();
        repository.Seed(TenantKey, TenantConfiguration.LegacyChannelId, Old);
        var legacy = TenantConfiguration.CreateDefault(TenantKey);
        legacy.WebhookUrl = "https://hooks.example.invalid/legacy";
        legacy.WebhookProviderType = (int)WebhookProviderType.GenericJson;
        var configs = new Mock<IConfigRepository>();
        configs.Setup(c => c.GetTenantConfigurationAsync(TenantKey)).ReturnsAsync(legacy);

        await Sweep(repository, configs).RunAsync(T0);

        Assert.True(repository.Has(TenantKey, TenantConfiguration.LegacyChannelId));
    }

    [Fact]
    public async Task Ops_rows_follow_the_ops_channel_list_and_a_missing_admin_configuration_touches_nothing()
    {
        var repository = new FakeRepository();
        repository.Seed("platform", "ops-live", Old);
        repository.Seed("platform", "ops-gone", Old);
        var admin = AdminConfiguration.CreateDefault();
        admin.OpsNotificationChannelsJson = NotificationChannel.SerializeList(new[] { Webhook(id: "ops-live") });
        var configs = new Mock<IConfigRepository>();
        configs.Setup(c => c.GetAdminConfigurationAsync()).ReturnsAsync(admin);

        await Sweep(repository, configs).RunAsync(T0);
        Assert.True(repository.Has("platform", "ops-live"));
        Assert.False(repository.Has("platform", "ops-gone"));

        repository.Seed("platform", "ops-gone", Old);
        configs.Setup(c => c.GetAdminConfigurationAsync()).ReturnsAsync((AdminConfiguration?)null);
        var result = await Sweep(repository, configs).RunAsync(T0);
        Assert.True(repository.Has("platform", "ops-gone"));
        Assert.Equal(1, result.ScopesSkipped);
    }

    [Fact]
    public async Task A_row_a_send_rewrote_during_the_sweep_survives()
    {
        var repository = new FakeRepository();
        repository.Seed(TenantKey, "gone-old", Old);
        var configs = new Mock<IConfigRepository>();
        configs.Setup(c => c.GetTenantConfigurationAsync(TenantKey)).ReturnsAsync(TenantWith(TenantKey));
        // A revert brought the channel back and it sent between the sweep's read and its delete.
        repository.BeforeWrite = repo => repo.Seed(TenantKey, "gone-old", T0);

        var result = await Sweep(repository, configs).RunAsync(T0);

        Assert.True(repository.Has(TenantKey, "gone-old"));
        Assert.Equal(0, result.RowsDeleted);
    }

    [Fact]
    public async Task A_failed_listing_never_reaches_the_maintenance_run()
    {
        var repository = new FakeRepository { Throw = true };

        var result = await Sweep(repository, new Mock<IConfigRepository>()).RunAsync(T0);

        Assert.Equal(0, result.RowsScanned);
    }

    // ── Table mapping ─────────────────────────────────────────────────────

    [Fact]
    public void Mapping_roundtrips_every_field_and_omits_unset_dates()
    {
        var original = new NotificationChannelHealth
        {
            ScopeKey = TenantKey,
            ChannelId = "c1",
            Fingerprint = "0123456789abcdef",
            Recent = "1101",
            ConsecutiveFailures = 2,
            LastAttemptUtc = T0,
            LastSuccessUtc = T0.AddDays(-1),
            LastFailureUtc = T0,
            FailingSinceUtc = T0.AddHours(-1),
            LastStatusCode = 401,
            LastError = "HTTP 401 Unauthorized",
            FailingNotifiedUtc = T0,
        };

        var back = TableNotificationChannelHealthRepository.MapFromEntity(TableNotificationChannelHealthRepository.MapToEntity(original));
        Assert.Equivalent(original, back, strict: true);

        var sparse = TableNotificationChannelHealthRepository.MapToEntity(new NotificationChannelHealth { ScopeKey = TenantKey, ChannelId = "c2", Fingerprint = "f", Recent = "1", LastAttemptUtc = T0 });
        Assert.Null(sparse["FailingNotifiedUtc"]);
        var sparseBack = TableNotificationChannelHealthRepository.MapFromEntity(sparse);
        Assert.Null(sparseBack.LastStatusCode);
        Assert.Null(sparseBack.FailingNotifiedUtc);
    }
}
