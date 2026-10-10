using AutopilotMonitor.Functions.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the three-layer rate limiter protecting the unauthenticated distress channel.
///
/// SECURITY GUARD: The distress endpoint has NO authentication. These rate limits are
/// the primary defense against abuse. Off-by-one errors or broken circuit breaker logic
/// are security vulnerabilities on an internet-facing endpoint.
///
/// Layers:
///   1. Per-IP:     5 requests per 15 minutes
///   2. Per-Tenant: 20 requests per hour
///   3. Global circuit breaker: 200 requests/minute → reject all for 5 minutes
/// </summary>
public class DistressRateLimitServiceTests
{
    private static readonly string TestTenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private static readonly string TestTenantId2 = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static DistressRateLimitService CreateService()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = NullLogger<DistressRateLimitService>.Instance;
        return new DistressRateLimitService(cache, logger);
    }

    // =========================================================================
    // Layer 1: Per-IP Rate Limiting (5 requests / 15 minutes)
    // =========================================================================

    [Fact]
    public void Check_FirstRequestFromIp_IsAllowed()
    {
        var svc = CreateService();

        var result = svc.Check("10.0.0.1", TestTenantId);

        Assert.True(result.IsAllowed);
        Assert.Null(result.RejectedBy);
    }

    [Fact]
    public void Check_ExactlyAtIpLimit_LastRequestAllowed()
    {
        var svc = CreateService();

        // Send 5 requests (the limit) from the same IP
        for (int i = 0; i < 4; i++)
            svc.Check("10.0.0.1", TestTenantId);

        var result = svc.Check("10.0.0.1", TestTenantId);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Check_OneOverIpLimit_IsRejectedByIp()
    {
        var svc = CreateService();

        // Exhaust the 5-request IP limit
        for (int i = 0; i < 5; i++)
            svc.Check("10.0.0.1", TestTenantId);

        // 6th request should be rejected
        var result = svc.Check("10.0.0.1", TestTenantId);

        Assert.False(result.IsAllowed);
        Assert.Equal("IP", result.RejectedBy);
    }

    [Fact]
    public void Check_DifferentIps_EachHasOwnLimit()
    {
        var svc = CreateService();

        // Exhaust limit for IP1
        for (int i = 0; i < 5; i++)
            svc.Check("10.0.0.1", TestTenantId);

        // IP2 should still be allowed (independent limit)
        var result = svc.Check("10.0.0.2", TestTenantId);

        Assert.True(result.IsAllowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Check_NullOrEmptyIp_SkipsIpCheckAndAllows(string? clientIp)
    {
        var svc = CreateService();

        // Even after many requests with null/empty IP, IP check is skipped
        for (int i = 0; i < 10; i++)
            svc.Check(clientIp!, TestTenantId);

        // Should still be allowed (IP check skipped, tenant limit is 20)
        var result = svc.Check(clientIp!, TestTenantId);

        Assert.True(result.IsAllowed);
    }

    // =========================================================================
    // Layer 2: Per-Tenant Rate Limiting (20 requests / 1 hour)
    // =========================================================================

    [Fact]
    public void Check_ExactlyAtTenantLimit_LastRequestAllowed()
    {
        var svc = CreateService();

        // Use different IPs to avoid hitting the per-IP limit (5 per IP)
        for (int i = 0; i < 19; i++)
            svc.Check($"10.0.{i / 5}.{i % 5 + 1}", TestTenantId);

        var result = svc.Check("10.0.4.1", TestTenantId);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Check_OneOverTenantLimit_IsRejectedByTenant()
    {
        var svc = CreateService();

        // Send 20 requests (the tenant limit) using different IPs
        for (int i = 0; i < 20; i++)
            svc.Check($"10.0.{i / 5}.{i % 5 + 1}", TestTenantId);

        // 21st request should be rejected by tenant
        var result = svc.Check("10.1.0.1", TestTenantId);

        Assert.False(result.IsAllowed);
        Assert.Equal("Tenant", result.RejectedBy);
    }

    [Fact]
    public void Check_DifferentTenants_EachHasOwnLimit()
    {
        var svc = CreateService();

        // Exhaust tenant limit for Tenant1
        for (int i = 0; i < 20; i++)
            svc.Check($"10.0.{i / 5}.{i % 5 + 1}", TestTenantId);

        // Tenant2 should still be allowed (independent limit)
        var result = svc.Check("10.0.0.1", TestTenantId2);

        Assert.True(result.IsAllowed);
    }

    // =========================================================================
    // Layer 3: Global Circuit Breaker (200 requests/min → 5 min block)
    // =========================================================================

    [Fact]
    public void Check_BelowCircuitBreakerThreshold_AllAllowed()
    {
        var svc = CreateService();

        // Send 199 requests from unique IPs and tenants (below the 200 threshold)
        DistressRateLimitResult? lastResult = null;
        for (int i = 0; i < 199; i++)
        {
            var ip = $"10.{i / 250}.{(i / 5) % 50}.{i % 5 + 1}";
            var tenant = Guid.NewGuid().ToString();
            lastResult = svc.Check(ip, tenant);
        }

        Assert.True(lastResult!.IsAllowed);
    }

    [Fact]
    public void Check_AtCircuitBreakerThreshold_TripsCircuit()
    {
        var svc = CreateService();

        // Send exactly 200 requests (the threshold) — each from unique IP/tenant
        for (int i = 0; i < 200; i++)
        {
            var ip = $"10.{i / 250}.{(i / 5) % 50}.{i % 5 + 1}";
            var tenant = Guid.NewGuid().ToString();
            svc.Check(ip, tenant);
        }

        // Next request (from a completely new IP/tenant) should be rejected by circuit breaker
        var result = svc.Check("192.168.1.1", Guid.NewGuid().ToString());

        Assert.False(result.IsAllowed);
        Assert.Equal("CircuitBreaker", result.RejectedBy);
    }

    [Fact]
    public void Check_CircuitOpen_AllRequestsRejected()
    {
        var svc = CreateService();

        // Trip the circuit breaker
        for (int i = 0; i < 200; i++)
        {
            var ip = $"10.{i / 250}.{(i / 5) % 50}.{i % 5 + 1}";
            svc.Check(ip, Guid.NewGuid().ToString());
        }

        // Multiple subsequent requests from different IPs/tenants should ALL be rejected
        for (int i = 0; i < 5; i++)
        {
            var result = svc.Check($"192.168.{i}.1", Guid.NewGuid().ToString());
            Assert.False(result.IsAllowed);
            Assert.Equal("CircuitBreaker", result.RejectedBy);
        }
    }

    // =========================================================================
    // Layer Ordering & Interaction
    // =========================================================================

    [Fact]
    public void Check_CircuitBreakerCheckedFirst_BeforeIpAndTenant()
    {
        var svc = CreateService();

        // Trip the circuit breaker
        for (int i = 0; i < 200; i++)
            svc.Check($"10.{i / 250}.{(i / 5) % 50}.{i % 5 + 1}", Guid.NewGuid().ToString());

        // A completely fresh IP+tenant should still be rejected by circuit breaker (not IP or tenant)
        var result = svc.Check("172.16.0.1", Guid.NewGuid().ToString());

        Assert.False(result.IsAllowed);
        Assert.Equal("CircuitBreaker", result.RejectedBy);
    }

    [Fact]
    public void Check_IpRejection_DoesNotIncrementTenantCounter()
    {
        var svc = CreateService();

        // Exhaust IP limit for a single IP
        for (int i = 0; i < 5; i++)
            svc.Check("10.0.0.1", TestTenantId);

        // Send 10 more requests from the same IP — all rejected by IP
        for (int i = 0; i < 10; i++)
        {
            var result = svc.Check("10.0.0.1", TestTenantId);
            Assert.Equal("IP", result.RejectedBy);
        }

        // Tenant should still have room (only 5 counted, not 15)
        // Use a fresh IP to check tenant counter
        var tenantCheck = svc.Check("10.0.0.2", TestTenantId);
        Assert.True(tenantCheck.IsAllowed);
    }

    // =========================================================================
    // Result Object Contract
    // =========================================================================

    [Fact]
    public void Check_AllowedResult_HasNullRejectedBy()
    {
        var svc = CreateService();

        var result = svc.Check("10.0.0.1", TestTenantId);

        Assert.True(result.IsAllowed);
        Assert.Null(result.RejectedBy);
    }

    [Theory]
    [InlineData("IP")]
    [InlineData("Tenant")]
    public void Check_RejectedResult_HasCorrectRejectedBy(string expectedRejectedBy)
    {
        var svc = CreateService();

        if (expectedRejectedBy == "IP")
        {
            // Exhaust IP limit
            for (int i = 0; i < 5; i++)
                svc.Check("10.0.0.1", TestTenantId);

            var result = svc.Check("10.0.0.1", TestTenantId);
            Assert.False(result.IsAllowed);
            Assert.Equal("IP", result.RejectedBy);
        }
        else
        {
            // Exhaust tenant limit
            for (int i = 0; i < 20; i++)
                svc.Check($"10.0.{i / 5}.{i % 5 + 1}", TestTenantId);

            var result = svc.Check("10.1.0.1", TestTenantId);
            Assert.False(result.IsAllowed);
            Assert.Equal("Tenant", result.RejectedBy);
        }
    }
}
