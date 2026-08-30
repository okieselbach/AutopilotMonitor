using AutopilotMonitor.Functions.Functions.Sessions;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Regression guard: the device-supplied registration <c>StartedAt</c> anchors the SessionsIndex
/// RowKey, the retention filter ("StartedAt lt cutoff"), the TotalToday window and the
/// supersede-predecessor comparison. Without a clamp a far-future value (year 2999) would pin
/// the row at the head of every list — including the Global Admin cross-tenant list — inflate
/// today's counters forever and never become a retention candidate. The clamp lives in the
/// shared register core (<see cref="RegisterSessionFunction.ProcessRegisterAsync"/>) and mirrors
/// <see cref="EventTimestampValidator"/> so registration and events share one skew policy.
/// </summary>
public class RegisterSessionStartedAtClampTests
{
    private static readonly DateTime FixedUtcNow = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    private static SessionRegistration Registration(DateTime startedAt) => new()
    {
        TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
        SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901",
        StartedAt = startedAt,
    };

    [Fact]
    public void FarFuture_StartedAt_is_clamped_to_server_time()
    {
        var registration = Registration(new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        RegisterSessionFunction.ClampStartedAt(registration, FixedUtcNow, NullLogger.Instance);

        Assert.Equal(FixedUtcNow, registration.StartedAt);
        Assert.Equal(DateTimeKind.Utc, registration.StartedAt.Kind);
    }

    [Fact]
    public void Future_beyond_event_tolerance_is_clamped_but_within_tolerance_is_kept()
    {
        var justInside = FixedUtcNow.AddHours(EventTimestampValidator.MaxFutureToleranceHours).AddMinutes(-1);
        var justOutside = FixedUtcNow.AddHours(EventTimestampValidator.MaxFutureToleranceHours).AddMinutes(1);

        var kept = Registration(justInside);
        var clamped = Registration(justOutside);
        RegisterSessionFunction.ClampStartedAt(kept, FixedUtcNow, NullLogger.Instance);
        RegisterSessionFunction.ClampStartedAt(clamped, FixedUtcNow, NullLogger.Instance);

        Assert.Equal(justInside, kept.StartedAt);
        Assert.Equal(FixedUtcNow, clamped.StartedAt);
    }

    [Fact]
    public void Catastrophic_past_StartedAt_is_clamped_to_server_time()
    {
        var registration = Registration(DateTime.MinValue);

        RegisterSessionFunction.ClampStartedAt(registration, FixedUtcNow, NullLogger.Instance);

        Assert.Equal(FixedUtcNow, registration.StartedAt);
    }

    [Fact]
    public void Plausible_StartedAt_is_preserved_and_normalized_to_Utc()
    {
        var unspecified = new DateTime(2026, 8, 30, 11, 30, 0, DateTimeKind.Unspecified);
        var registration = Registration(unspecified);

        RegisterSessionFunction.ClampStartedAt(registration, FixedUtcNow, NullLogger.Instance);

        Assert.Equal(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc), registration.StartedAt);
        Assert.Equal(DateTimeKind.Utc, registration.StartedAt.Kind);
    }

    [Fact]
    public void ProcessRegisterAsync_body_calls_the_clamp_before_storing()
    {
        // Source-level wiring pin: the clamp must precede SupersedeOrphanedPredecessorsAsync and
        // StoreSessionAsync inside the shared core so both HTTP entries (cert-auth + bootstrap)
        // are covered. RegisterSessionGuardWiringTests already pins that bootstrap delegates here.
        var source = File.ReadAllText(FindSource("Functions/Sessions/RegisterSessionFunction.cs"));
        var core = source.IndexOf("internal async Task<RegisterSessionOutput> ProcessRegisterAsync(", StringComparison.Ordinal);
        var clamp = source.IndexOf("ClampStartedAt(registration, DateTime.UtcNow, _logger);", StringComparison.Ordinal);
        var store = source.IndexOf("_sessionRepo.StoreSessionAsync(registration", StringComparison.Ordinal);

        Assert.True(core >= 0 && clamp > core && store > clamp,
            "ClampStartedAt must be invoked inside ProcessRegisterAsync before StoreSessionAsync.");
    }

    private static string FindSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "AutopilotMonitor.Functions", relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
