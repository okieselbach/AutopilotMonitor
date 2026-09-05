using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AutopilotMonitor.Functions.Services;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

public class UserDelegationKeyCacheTests
{
    private static (UserDelegationKeyCache cache, Mock<BlobServiceClient> client, Func<DateTimeOffset> now, Action<TimeSpan> advance) Build()
    {
        var clock = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var client = new Mock<BlobServiceClient>();
        client
            .Setup(c => c.GetUserDelegationKeyAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns<DateTimeOffset?, DateTimeOffset, CancellationToken>((start, expires, _) =>
            {
                var key = BlobsModelFactory.UserDelegationKey("oid", "tid", start ?? clock, expires, "b", "2024-11-04", "c2lnbmVk");
                return Task.FromResult(Response.FromValue(key, Mock.Of<Response>()));
            });
        var cache = new UserDelegationKeyCache(client.Object, () => clock);
        return (cache, client, () => clock, delta => clock += delta);
    }

    [Fact]
    public async Task Second_sas_reuses_the_key_instead_of_minting_again()
    {
        var (cache, client, now, _) = Build();

        var first = await cache.GetAsync(now() + TimeSpan.FromMinutes(15));
        var second = await cache.GetAsync(now() + TimeSpan.FromMinutes(60));

        Assert.Same(first, second);
        client.Verify(c => c.GetUserDelegationKeyAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Key_is_minted_for_the_full_lifetime_with_clock_skew_on_the_start()
    {
        var (cache, _, now, _) = Build();

        var key = await cache.GetAsync(now() + TimeSpan.FromMinutes(15));

        Assert.Equal(now() - TimeSpan.FromMinutes(5), key.SignedStartsOn);
        Assert.Equal(now() + UserDelegationKeyCache.KeyLifetime, key.SignedExpiresOn);
    }

    [Fact]
    public async Task Key_is_re_minted_once_the_refresh_margin_is_reached()
    {
        var (cache, client, now, advance) = Build();
        await cache.GetAsync(now() + TimeSpan.FromMinutes(15));

        advance(UserDelegationKeyCache.KeyLifetime - UserDelegationKeyCache.RefreshMargin + TimeSpan.FromMinutes(1));
        await cache.GetAsync(now() + TimeSpan.FromMinutes(15));

        client.Verify(c => c.GetUserDelegationKeyAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Key_is_re_minted_when_the_sas_would_outlive_it()
    {
        var (cache, client, now, _) = Build();
        await cache.GetAsync(now() + TimeSpan.FromMinutes(15));

        await cache.GetAsync(now() + UserDelegationKeyCache.KeyLifetime + TimeSpan.FromMinutes(1));

        client.Verify(c => c.GetUserDelegationKeyAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Concurrent_first_callers_share_one_mint()
    {
        var (cache, client, now, _) = Build();

        var tasks = new Task<UserDelegationKey>[8];
        for (var i = 0; i < tasks.Length; i++)
            tasks[i] = Task.Run(() => cache.GetAsync(now() + TimeSpan.FromMinutes(15)));
        await Task.WhenAll(tasks);

        client.Verify(c => c.GetUserDelegationKeyAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
